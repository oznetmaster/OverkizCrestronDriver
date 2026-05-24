// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using OverKizApi;
using OverKizApi.Enums;
using OverKizApi.Models;

using Crestron.DeviceDrivers.EntityModel.Logging;

using OverkizCommand = OverKizApi.Models.Command;

namespace OverKiz.CrestronDriver;

/// <summary>
/// SDK V2 Platform Driver root entity.
/// Connects to an Overkiz-compatible gateway (cloud or local), discovers all
/// controllable shade devices, and exposes them as dynamic child entities
/// via the <c>platform:managedDevices</c> property.
/// </summary>
public class OverkizPlatformDriver : ReflectedAttributeDriverEntity
	{
	// ── Configuration (populated via DelegateDataDrivenConfigurationController) ─

	private string _cloudUsername = string.Empty;
	private string _cloudPassword = string.Empty;
	private string _cloudServer = "SomfyEurope";
	private string _gatewayIp = string.Empty;
	private string _localToken = string.Empty;

	private bool IsLocalMode =>
		!string.IsNullOrEmpty (_gatewayIp) && !string.IsNullOrEmpty (_localToken);

	// ── Last-applied config snapshot (used to suppress redundant reconnects) ─

	private string _appliedUsername = null;
	private string _appliedPassword = null;
	private string _appliedServer   = null;
	private string _appliedIp       = null;
	private string _appliedToken    = null;

	// ── Logger ────────────────────────────────────────────────────────────

	private readonly DriverControllerLogger _logger;

	// ── Driver creation context

	private readonly DriverControllerCreationArgs _args;
	private readonly DriverImplementationResources _resources;

	// ── OverkizClient ─────────────────────────────────────────────────────

	private OverkizClient _client;
	private readonly object _clientLock = new ();

	// ── Child shade tracking ──────────────────────────────────────────────

	/// <summary>Maps active execution IDs to the entity that triggered them.</summary>
	private readonly Dictionary<string, IOverkizEntity> _pendingExecs = new ();
	private readonly object _pendingExecsLock = new ();

	/// <summary>
	/// Managed-devices dictionary exposed to Crestron Home.
	/// Must be replaced as a whole (copy-on-write) per SDK requirements.
	/// </summary>
	[EntityProperty (
		Id = "platform:managedDevices",
		Type = DriverEntityValueType.DeviceDictionary,
		ItemTypeRef = "platform:ManagedDevice",
		FriendlyName = "Managed Shades")]
	public IDictionary<string, PlatformManagedDevice> ManagedDevices
		{
		get; private set;
		}

	// ── Online / ready indicators ─────────────────────────────────────────

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline
		{
		get; private set;
		}

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady
		{
		get; private set;
		}

	private void SetOnline (bool online)
		{
		if (OnlineIndicatorIsOnline == online)
			return;
		OnlineIndicatorIsOnline = online;
		NotifyPropertyChanged ("onlineIndicator:isOnline", new DriverEntityValue (online));
		}

	private void SetReady (bool ready)
		{
		if (ReadyIndicatorIsReady == ready)
			return;
		ReadyIndicatorIsReady = ready;
		NotifyPropertyChanged ("readyIndicator:isReady", new DriverEntityValue (ready));
		}

	private readonly Dictionary<string, IOverkizEntity> _entities
		= new (StringComparer.OrdinalIgnoreCase);

	private readonly object _entitiesLock = new ();

	// ── Connect cancellation ─────────────────────────────────────────────

	private CancellationTokenSource _connectCts;
	private bool _connectInFlight;
	private readonly object _connectLock = new ();

	// ── Event loop ────────────────────────────────────────────────────────

	private CancellationTokenSource _eventCts;
	private Task _eventLoopTask;
	private readonly object _eventLock = new ();

	// ── Work queue (serialises all OverkizClient access) ──────────────────

	private readonly OverkizWorkQueue _workQueue = new ();

	// ── HttpClient (one instance, reused across reconnects) ───────────────

	private readonly HttpClient _httpClient;
	private bool _disposed;

	// ── Configuration controller (exposed to entry point) ─────────────────

	internal DataDrivenConfigurationController ConfigurationController
		{
		get; private set;
		}

	// ── Constructor ───────────────────────────────────────────────────────

	public OverkizPlatformDriver (
		DriverControllerCreationArgs args,
		DriverImplementationResources resources)
		: base (DriverController.RootControllerId)
		{
		_logger = args.Logger;
		_args = args;
		_resources = resources;

		// Single HttpClient with KeepAlive disabled to prevent memory leak (SDK guideline).
		// CreateLocalHttpClientHandler() bypasses TLS validation for the self-signed cert on local gateways;
		// that handler is also fine for cloud connections (which use a trusted cert).
		HttpClientHandler handler = OverkizConst.CreateLocalHttpClientHandler ();
		handler.UseCookies = true;
		_httpClient = new HttpClient (handler);
		// Prevent the KeepAlive timer/DelayPromise memory leak (Crestron SDK guideline).
		_httpClient.DefaultRequestHeaders.ConnectionClose = true;

		var cfgArgs = DataDrivenConfigurationControllerArgs.FromResources (args, resources, ControllerId);
		ConfigurationController = new DelegateDataDrivenConfigurationController (
			cfgArgs,
			ApplyConfigurationItems,
			null,
			null);
		}

	public override void Dispose ()
		{
		if (_disposed)
			return;
		_disposed = true;
		lock (_connectLock)
			{
			_connectCts?.Cancel ();
			_connectCts?.Dispose ();
			_connectCts = null;
			}

		_workQueue.Stop ();
		StopEventLoop ();
		lock (_entitiesLock)
			{
			foreach (IOverkizEntity e in _entities.Values)
				e.StopPolling ();
			}

		DisposeClient ();
		_httpClient?.Dispose ();
		base.Dispose ();
		}

	// ── Private: configuration callback ──────────────────────────────────

	private ConfigurationItemErrors ApplyConfigurationItems (
		DataDrivenConfigurationController.ApplyConfigurationAction action,
		string stepId,
		IDictionary<string, DriverEntityValue?> values)
		{
		Log ("ApplyConfigurationItems: action=" + action);
		switch (action)
			{
			case DataDrivenConfigurationController.ApplyConfigurationAction.ApplyAll:
			case DataDrivenConfigurationController.ApplyConfigurationAction.ApplyStep:

				DriverEntityValue? v;

				if (values.TryGetValue ("CloudUsername", out v) && v.HasValue)
					_cloudUsername = v.Value.GetValue<string> () ?? _cloudUsername;

				if (values.TryGetValue ("CloudPassword", out v) && v.HasValue)
					_cloudPassword = v.Value.GetValue<string> () ?? _cloudPassword;

				if (values.TryGetValue ("CloudServer", out v) && v.HasValue)
					_cloudServer = v.Value.GetValue<string> () ?? _cloudServer;

				if (values.TryGetValue ("GatewayIP", out v) && v.HasValue)
					_gatewayIp = v.Value.GetValue<string> () ?? _gatewayIp;

				if (values.TryGetValue ("LocalToken", out v) && v.HasValue)
					_localToken = v.Value.GetValue<string> () ?? _localToken;

				SetReady (true);

				// Avoid double-init: the SDK fires ApplyConfigurationItems more than
				// once on startup with identical values, and both calls arrive before
				// the async connect task has had a chance to complete (so an
				// OnlineIndicatorIsOnline check would let both through).  Guard with
				// an in-flight flag instead.
				bool configChanged =
					_cloudUsername != _appliedUsername ||
					_cloudPassword != _appliedPassword ||
					_cloudServer   != _appliedServer   ||
					_gatewayIp     != _appliedIp       ||
					_localToken    != _appliedToken;

				bool shouldConnect;
				lock (_connectLock)
					shouldConnect = configChanged || !_connectInFlight;

				if (shouldConnect)
					{
					_appliedUsername = _cloudUsername;
					_appliedPassword = _cloudPassword;
					_appliedServer   = _cloudServer;
					_appliedIp       = _gatewayIp;
					_appliedToken    = _localToken;
					Connect ();
					}
				else
					{
					Log ("ApplyConfigurationItems: connect already in-flight with same config – skipping");
					}

				return null;

				case DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues:
					Disconnect ();
					SetReady (false);
					break;
			}

		return null;
		}

	// ── Connection lifecycle ──────────────────────────────────────────────

	private void Connect ()
		{
		CancellationTokenSource cts;
		lock (_connectLock)
			{
			_connectCts?.Cancel ();
			_connectCts?.Dispose ();
			_connectCts = new CancellationTokenSource ();
			cts = _connectCts;
			_connectInFlight = true;
			}

		_ = Task.Run (async () =>
			{
				try
					{
					Log ("Connect task started; isLocalMode=" + IsLocalMode);
					cts.Token.ThrowIfCancellationRequested ();
					await ConnectClientAsync ().ConfigureAwait (false);

					cts.Token.ThrowIfCancellationRequested ();
					SetOnline (true);

					bool hasEntities;
					lock (_entitiesLock)
						hasEntities = _entities.Count > 0;

					cts.Token.ThrowIfCancellationRequested ();
					if (!hasEntities)
						await DiscoverDevicesAsync (cts.Token).ConfigureAwait (false);
					else
						RegisterEntitiesWithFramework ();

					cts.Token.ThrowIfCancellationRequested ();
					lock (_entitiesLock)
						{
						foreach (IOverkizEntity e in _entities.Values)
							e.StartPolling (_workQueue);
						}

					StartEventLoop ();
					}
				catch (OperationCanceledException)
					{
					Log ("Connect superseded by newer request");
					}
				catch (Exception ex)
					{
					Log ("Connect failed: " + ex.ToString ());
					}
				finally
					{
					lock (_connectLock)
						{
						// Only clear the flag if this task still owns the current CTS.
						// A superseded task must not clear the flag set by its successor.
						if (ReferenceEquals (cts, _connectCts))
							_connectInFlight = false;
						}
					}
				});
		}

	private void Disconnect ()
		{
		// Clear the snapshot so the next ApplyConfigurationItems triggers a fresh connect.
		_appliedUsername = null;
		_appliedPassword = null;
		_appliedServer   = null;
		_appliedIp       = null;
		_appliedToken    = null;

		lock (_connectLock)
			{
			_connectCts?.Cancel ();
			_connectCts?.Dispose ();
			_connectCts = null;
			_connectInFlight = false;
			}

		StopEventLoop ();
		lock (_entitiesLock)
			{
			foreach (IOverkizEntity e in _entities.Values)
				e.StopPolling ();
			}

		DisposeClient ();
		SetOnline (false);
		}

	// ── Private: API client lifecycle ─────────────────────────────────────

	private async Task ConnectClientAsync ()
		{
		DisposeClient ();

		OverkizClient newClient;

		if (IsLocalMode)
			{
			newClient = new OverkizClient (
				username: string.Empty,
				password: string.Empty,
				server: OverkizConst.LocalServer (_gatewayIp),
				token: _localToken,
				httpClient: _httpClient);

			Log ("Using Local mode: " + _gatewayIp);
			}
		else
			{
			if (string.IsNullOrEmpty (_cloudUsername) || string.IsNullOrEmpty (_cloudPassword))
				throw new InvalidOperationException ("Cloud mode requires CloudUsername and CloudPassword.");

			if (!Enum.TryParse<Server> (_cloudServer, out Server serverEnum))
				serverEnum = Server.SomfyEurope;

			newClient = new OverkizClient (
				username: _cloudUsername,
				password: _cloudPassword,
				server: OverkizConst.SupportedServers[serverEnum],
				httpClient: _httpClient);

			Log ("Using Cloud mode: " + _cloudServer);
			}

		var ok = await newClient.Login ().ConfigureAwait (false);
		if (!ok)
			throw new InvalidOperationException ("Overkiz login failed.");

		lock (_clientLock)
			_client = newClient;

		_workQueue.SetClient (newClient);
		Log ("Connected to Overkiz API");
		}

	private void DisposeClient ()
		{
		_workQueue.SetClient (null);

		OverkizClient old;
		lock (_clientLock)
			{
			old = _client;
			_client = null;
			}

		if (old != null)
			{
			_ = Task.Run (async () =>
			{
				try
					{
					await old.DisposeAsync ().ConfigureAwait (false);
					}
				catch (Exception ex)
					{
					Log ("DisposeClient error: " + ex.ToString ());
					}
			});
			}
		}

	// ── Private: discovery ─────────────────────────────────────────────────

	private async Task DiscoverDevicesAsync (CancellationToken ct = default)
		{
		OverkizClient client;
		lock (_clientLock)
			client = _client;
		if (client == null)
			return;

		IReadOnlyList<Device> devices = await client.GetDevices ().ConfigureAwait (false);
		Log ("Discovered " + devices.Count + " total devices");
		foreach (var d in devices)
			{
			if (d.UiClass.HasValue && _shadeClasses.Contains (d.UiClass.Value))
				Log ("ShadeDevice: " + d.DeviceUrl + " definitionNull=" + (d.Definition == null)
					+ " commandCount=" + (d.Definition?.Commands?.Count ?? -1)
					+ " commands=[" + string.Join (",", d.Definition?.Commands?.Select (c => c.CommandName) ?? System.Linq.Enumerable.Empty<string> ()) + "]");
			}

		var controllersToAdd = new List<ConfigurableDriverEntity> ();
		var managedDevicesCopy = new Dictionary<string, PlatformManagedDevice> ();

		foreach (Device device in devices)
			{
			if (device.DeviceUrl == null)
				{
				Log ("Skipping device (no URL): " + (device.Label ?? "(no label)"));
				continue;
				}

			var url = device.DeviceUrl;

			// Exclude infrastructure/internal nodes
			if (url.StartsWith ("internal://", StringComparison.OrdinalIgnoreCase))
				{
				Log ("Skipping internal: " + url);
				continue;
				}

			if (url.StartsWith ("zigbee://", StringComparison.OrdinalIgnoreCase))
				{
				Log ("Skipping zigbee: " + url);
				continue;
				}

			// Require actuator type
			if (device.Type != ProductType.Actuator)
				{
				Log ("Skipping non-actuator: " + url + " | Type=" + device.Type);
				continue;
				}

			Log ("Evaluating: " + (device.Label ?? url) + " | UIClass=" + (device.UiClass.HasValue ? device.UiClass.Value.ToString () : "none") + " | Protocol=" + (device.Protocol.HasValue ? device.Protocol.Value.ToString () : "unknown") + " | URL=" + url);

			IOverkizEntity entity = TryCreateEntity (url, device);
			if (entity == null)
				{
				Log ("No entity factory match: " + url + " | UIClass=" + (device.UiClass.HasValue ? device.UiClass.Value.ToString () : "none"));
				continue;
				}

			var label = (device.Label ?? url).Trim ();
			Log ("Queuing device: " + label + " | UIClass: " + (device.UiClass.HasValue ? device.UiClass.Value.ToString () : "none") + " | Protocol: " + (device.Protocol.HasValue ? device.Protocol.Value.ToString () : "unknown") + " | URL: " + url);

			lock (_entitiesLock)
			{
			if (_entities.ContainsKey (url))
				continue;

			_entities[url] = entity;
			controllersToAdd.Add (new ConfigurableDriverEntity (entity.ControllerId, (ReflectedAttributeDriverEntity)entity, null));
			managedDevicesCopy[entity.ControllerId] = new PlatformManagedDevice (
				entity.UxCategory,
				label,
				"Somfy / Overkiz",
				device.UiClass.HasValue ? device.UiClass.Value.ToString () : entity.UxCategory.ToString (),
				null);

			Log ("Queued device: " + label + " (id=" + entity.ControllerId + ", url=" + url + ")");
			}
			}

		if (controllersToAdd.Count > 0)
			{
			ct.ThrowIfCancellationRequested ();

			UpdateSubControllers (controllersToAdd, null);

			ManagedDevices = managedDevicesCopy;
			NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

			Log ("Published " + controllersToAdd.Count + " device(s)");
			}
		else
			{
			Log ("All devices already registered");
			}

		Log ("Discovery complete");
		}

	/// <summary>
	/// Re-registers all already-discovered entities with the current framework
	/// context.  Called on every subsequent <see cref="Connect"/> after the
	/// initial discovery so that the active service context owns the children.
	/// </summary>
	private void RegisterEntitiesWithFramework ()
		{
		var controllers = new List<ConfigurableDriverEntity> ();
		var managedDevices = new Dictionary<string, PlatformManagedDevice> ();

		lock (_entitiesLock)
			{
			foreach (KeyValuePair<string, IOverkizEntity> kv in _entities)
				{
				IOverkizEntity entity = kv.Value;
				controllers.Add (new ConfigurableDriverEntity (entity.ControllerId, (ReflectedAttributeDriverEntity)entity, null));

				if (ManagedDevices != null && ManagedDevices.TryGetValue (entity.ControllerId, out PlatformManagedDevice existing))
					managedDevices[entity.ControllerId] = existing;
				}
			}

		if (controllers.Count == 0)
			return;

		UpdateSubControllers (controllers, null);

		ManagedDevices = managedDevices;
		NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

		Log ("Re-registered " + controllers.Count + " device(s) with framework");
		}

	// ── Private: event loop ───────────────────────────────────────────────

	private void StartEventLoop ()
		{
		lock (_eventLock)
			{
			_eventCts?.Cancel ();
			_eventCts?.Dispose ();
			_eventCts = new CancellationTokenSource ();
			var token = _eventCts.Token;
			_eventLoopTask = Task.Run (() => RunEventLoopAsync (token));
			}
		Log ("Event loop started");
		}

	private void StopEventLoop ()
		{
		lock (_eventLock)
			{
			_eventCts?.Cancel ();
			_eventCts?.Dispose ();
			_eventCts = null;
			}
		Log ("Event loop stopped");
		}

	private async Task RunEventLoopAsync (CancellationToken ct)
		{
		const int POLL_INTERVAL_MS = 2_000;
		const int RETRY_DELAY_MS   = 10_000;

		Log ("Event loop: registering listener");
		try
			{
			OverkizClient client;
			lock (_clientLock)
				client = _client;
			if (client == null)
				return;

			await client.RegisterEventListener ().ConfigureAwait (false);
			Log ("Event loop: listener registered");

			while (!ct.IsCancellationRequested)
				{
				try
					{
					await Task.Delay (POLL_INTERVAL_MS, ct).ConfigureAwait (false);

					IReadOnlyList<OverKizApi.Models.EventObject> events;
					lock (_clientLock)
						client = _client;
					if (client == null || ct.IsCancellationRequested)
						break;

					events = await client.FetchEvents ().ConfigureAwait (false);

					foreach (var ev in events)
						{
						if (ct.IsCancellationRequested)
							break;
						DispatchEvent (ev);
						}
					}
				catch (OperationCanceledException)
					{
					break;
					}
				catch (Exception ex)
					{
					Log ("Event loop: fetch error – " + ex.Message + "; retrying in " + RETRY_DELAY_MS + "ms");
					try { await Task.Delay (RETRY_DELAY_MS, ct).ConfigureAwait (false); }
					catch (OperationCanceledException) { break; }
					}
				}
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			Log ("Event loop: fatal error – " + ex.ToString ());
			}
		finally
			{
			try
				{
				OverkizClient client;
				lock (_clientLock)
					client = _client;
				if (client != null)
					await client.UnregisterEventListener ().ConfigureAwait (false);
				Log ("Event loop: listener unregistered");
				}
			catch (Exception ex)
				{
				Log ("Event loop: unregister error – " + ex.Message);
				}
			}
		}

	private void DispatchEvent (OverKizApi.Models.EventObject ev)
		{
		if (ev.Name == null)
			return;

		// The API appends "Event" to every event name (e.g. "DeviceUpdatedEvent").
		// Strip the suffix before parsing so the enum match is name-stable.
		var rawName = ev.Name.EndsWith ("Event", StringComparison.OrdinalIgnoreCase)
			? ev.Name.Substring (0, ev.Name.Length - 5)
			: ev.Name;

		if (!Enum.TryParse<OverKizApi.Enums.EventName> (rawName, out var eventName))
			return;

		switch (eventName)
			{
			case OverKizApi.Enums.EventName.DeviceStateChanged:
				HandleDeviceStateChanged (ev);
				break;

			case OverKizApi.Enums.EventName.DeviceAvailabilityChanged:
				HandleDeviceAvailabilityChanged (ev);
				break;

			case OverKizApi.Enums.EventName.DeviceCreated:
				_ = Task.Run (() => HandleDeviceCreatedAsync (ev));
				break;

			case OverKizApi.Enums.EventName.DeviceDeleted:
				HandleDeviceDeleted (ev);
				break;

			case OverKizApi.Enums.EventName.DeviceUpdated:
				HandleDeviceUpdated (ev);
				break;

			case OverKizApi.Enums.EventName.GatewaySynchronizationFinished:
			case OverKizApi.Enums.EventName.GatewaySynchronizationEnded:
				Log ("Event: GatewaySynchronizationFinished – triggering full resync");
				_ = Task.Run (() => DiscoverDevicesAsync (CancellationToken.None));
				break;

			case OverKizApi.Enums.EventName.ExecutionStateChanged:
				HandleExecutionStateChanged (ev);
				break;
			}
		}

	private void HandleDeviceStateChanged (OverKizApi.Models.EventObject ev)
		{
		if (ev.DeviceUrl == null)
			return;

		IOverkizEntity entity;
		lock (_entitiesLock)
			{
			if (!_entities.TryGetValue (ev.DeviceUrl, out entity))
				return;
			}

		entity.ApplyEventStates (ev.DeviceStates);
		}

	private void HandleExecutionStateChanged (OverKizApi.Models.EventObject ev)
		{
		if (ev.ExecId == null || ev.NewState == null)
			return;

		bool terminal = ev.NewState == OverKizApi.Enums.ExecutionState.Completed
			|| ev.NewState == OverKizApi.Enums.ExecutionState.Failed
			|| ev.NewState == OverKizApi.Enums.ExecutionState.Cancelled;

		if (!terminal)
			return;

		IOverkizEntity entity;
		lock (_pendingExecsLock)
			{
			if (!_pendingExecs.TryGetValue (ev.ExecId, out entity))
				return;
			_pendingExecs.Remove (ev.ExecId);
			}

		entity.SetMoving (false);
		}

	private void HandleDeviceAvailabilityChanged (OverKizApi.Models.EventObject ev)
		{
		if (ev.DeviceUrl == null)
			return;

		// SubType 0 = unavailable, 1 = available (Overkiz convention)
		bool available = ev.SubType == 1;

		IOverkizEntity entity;
		lock (_entitiesLock)
			{
			if (!_entities.TryGetValue (ev.DeviceUrl, out entity))
				return;
			}

		Log ("Event: DeviceAvailabilityChanged " + ev.DeviceUrl + " available=" + available);
		entity.UpdateAvailability (available);
		}

	private async Task HandleDeviceCreatedAsync (OverKizApi.Models.EventObject ev)
		{
		if (ev.DeviceUrl == null)
			return;

		lock (_entitiesLock)
			{
			if (_entities.ContainsKey (ev.DeviceUrl))
				return;
			}

		Log ("Event: DeviceCreated " + ev.DeviceUrl + " – fetching device info");

		try
			{
			OverkizClient client;
			lock (_clientLock)
				client = _client;
			if (client == null)
				return;

			IReadOnlyList<OverKizApi.Models.Device> devices = await client.GetDevices ().ConfigureAwait (false);
			var device = devices.FirstOrDefault (d =>
				string.Equals (d.DeviceUrl, ev.DeviceUrl, StringComparison.OrdinalIgnoreCase));

			if (device == null)
				{
				Log ("Event: DeviceCreated – device not found in refresh: " + ev.DeviceUrl);
				return;
				}

			IOverkizEntity entity = TryCreateEntity (ev.DeviceUrl, device);
			if (entity == null)
				{
				Log ("Event: DeviceCreated – no entity factory match for: " + ev.DeviceUrl);
				return;
				}

			var label = (device.Label ?? ev.DeviceUrl).Trim ();
			var managedDevice = new PlatformManagedDevice (
				entity.UxCategory,
				label,
				"Somfy / Overkiz",
				device.UiClass.HasValue ? device.UiClass.Value.ToString () : entity.UxCategory.ToString (),
				null);

			List<ConfigurableDriverEntity> controllers;
			Dictionary<string, PlatformManagedDevice> managedDevicesCopy;

			lock (_entitiesLock)
				{
				if (_entities.ContainsKey (ev.DeviceUrl))
					return;
				_entities[ev.DeviceUrl] = entity;
				controllers = [new ConfigurableDriverEntity (entity.ControllerId, (ReflectedAttributeDriverEntity)entity, null)];
				managedDevicesCopy = ManagedDevices != null
					? new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
					: new Dictionary<string, PlatformManagedDevice> ();
				managedDevicesCopy[entity.ControllerId] = managedDevice;
				}

			UpdateSubControllers (controllers, null);

			ManagedDevices = managedDevicesCopy;
			NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

			entity.StartPolling (_workQueue);

			Log ("Event: DeviceCreated – added child " + label + " (id=" + entity.ControllerId + ")");
			}
		catch (Exception ex)
			{
			Log ("Event: DeviceCreated – error: " + ex.ToString ());
			}
		}

	private void HandleDeviceDeleted (OverKizApi.Models.EventObject ev)
		{
		if (ev.DeviceUrl == null)
			return;

		IOverkizEntity entity;
		Dictionary<string, PlatformManagedDevice> managedDevicesCopy;

		lock (_entitiesLock)
			{
			if (!_entities.TryGetValue (ev.DeviceUrl, out entity))
				return;
			_entities.Remove (ev.DeviceUrl);
			managedDevicesCopy = ManagedDevices != null
				? new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
				: new Dictionary<string, PlatformManagedDevice> ();
			managedDevicesCopy.Remove (entity.ControllerId);
			}

		// Cancel any in-flight executions for this entity
		lock (_pendingExecsLock)
			{
			foreach (string execId in _pendingExecs.Keys
				.Where (k => ReferenceEquals (_pendingExecs[k], entity))
				.ToList ())
				_pendingExecs.Remove (execId);
			}

		entity.StopPolling ();

		UpdateSubControllers (null, [entity.ControllerId]);

		// Dispose after the framework has deregistered the sub-controller.
		(entity as IDisposable)?.Dispose ();

		ManagedDevices = managedDevicesCopy;
		NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

		Log ("Event: DeviceDeleted – removed child (id=" + entity.ControllerId + ", url=" + ev.DeviceUrl + ")");
		}

	private void HandleDeviceUpdated (OverKizApi.Models.EventObject ev)
		{
		if (ev.DeviceUrl == null || ev.Label == null)
			return;

		IOverkizEntity entity;
		lock (_entitiesLock)
			{
			if (!_entities.TryGetValue (ev.DeviceUrl, out entity))
				return;
			}

		if (ManagedDevices == null || !ManagedDevices.TryGetValue (entity.ControllerId, out PlatformManagedDevice existing))
			return;

		var updated = new PlatformManagedDevice (
			existing.UxCategory,
			ev.Label.Trim (),
			existing.Manufacturer,
			existing.Model,
			null);

		var copy = new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
			{
			[entity.ControllerId] = updated
			};

		ManagedDevices = copy;
		NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

		entity.UpdateLabel (ev.Label.Trim ());
		Log ("Event: DeviceUpdated – relabelled " + entity.ControllerId + " to '" + ev.Label + "'");
		}

	// ── Private: entity factory ──────────────────────────────────────────────

	private static readonly HashSet<UIClass> _shadeClasses =
		[
		UIClass.RollerShutter,
		UIClass.Screen,
		UIClass.UpDownRollerShutter,
		UIClass.UpDownScreen,
		UIClass.ExteriorScreen,
		UIClass.Awning,
		UIClass.Pergola,
		UIClass.SwingingShutter,
		UIClass.TiltOnlyVenetianBlind,
		UIClass.UpDownVenetianBlind,
		UIClass.UpDownWindow,
		UIClass.GarageDoor,
		];

	/// <summary>
	/// Maps a discovered Overkiz device to the appropriate
	/// <see cref="IOverkizEntity"/> implementation.
	/// Returns <c>null</c> when the device type is not yet supported.
	/// </summary>
	private IOverkizEntity TryCreateEntity (string url, Device device)
		{
		bool isShade = (device.UiClass.HasValue && _shadeClasses.Contains (device.UiClass.Value))
			|| url.StartsWith ("rts://", StringComparison.OrdinalIgnoreCase);

		if (isShade)
			{
			var safeId = MakeSafeControllerId (url);
			var isOneWay = url.StartsWith ("rts://", StringComparison.OrdinalIgnoreCase)
					|| device.Protocol == Protocol.Rts;
				var hasMyCommand = device.Definition?.Commands
					.Any (c => string.Equals (c.CommandName, "my", StringComparison.OrdinalIgnoreCase))
					?? false;
				var entity = new OverkizShadeEntity (
					controllerId: safeId,
					logControllerId: ControllerId,
					deviceUrl: url,
					deviceLabel: device.Label?.Trim () ?? string.Empty,
					isOneWay: isOneWay,
					hasMyCommand: hasMyCommand,
					sendCommand: cmd => SendDeviceCommand (url, cmd),
					sendCommandWithParams: (cmd, p) => SendDeviceCommand (url, cmd, p),
					driverDataDirectoryPath: _args.DriverDataDirectoryPath,
					logger: _logger,
					resources: _resources);
			Log ("Shade constructed: controllerId=" + safeId + " isOneWay=" + isOneWay + " hasMyCommand=" + hasMyCommand
				+ " definitionNull=" + (device.Definition == null)
				+ " commandCount=" + (device.Definition?.Commands?.Count ?? -1)
				+ " commands=[" + string.Join (",", device.Definition?.Commands?.Select (c => c.CommandName) ?? System.Linq.Enumerable.Empty<string> ()) + "]");
			return entity;
			}

		// Future device types: add additional cases here.
		return null;
		}

	// ── Private: send command to Overkiz ──────────────────────────────────

	private void SendDeviceCommand (string deviceUrl, string command, object[] parameters = null) =>
		_ = _workQueue.EnqueueAsync (async client =>
			{
				try
					{
					var cmds = new List<OverkizCommand>
						{
							new () {
							Name       = command,
							Parameters = parameters ?? []
							}
						};
					string execId = await client.ExecuteDeviceAction (deviceUrl, cmds).ConfigureAwait (false);

					// Track the execution so we can set isMoving=false when it completes.
					IOverkizEntity entity;
					lock (_entitiesLock)
						_entities.TryGetValue (deviceUrl, out entity);
					if (entity != null)
						{
						lock (_pendingExecsLock)
							_pendingExecs[execId] = entity;
						entity.SetMoving (true);
						}
					}
				catch (Exception ex)
					{
					Log ("SendDeviceCommand failed: " + ex.ToString ());
					}
			});

	// ── Utility ───────────────────────────────────────────────────────────

	/// <summary>
	/// Converts an Overkiz device URL (e.g. "io://1234-5678/9012") into a
	/// controller ID that contains only alphanumeric characters and underscores,
	/// safe for use as a Crestron SDK sub-controller ID.
	/// </summary>
	private static string MakeSafeControllerId (string url)
		{
		var chars = new System.Text.StringBuilder (url.Length);
		foreach (char c in url)
			_ = chars.Append (char.IsLetterOrDigit (c) ? c : '_');
		return chars.ToString ();
		}

	private void Log (string message) => _logger?.Log (ControllerId, LogEntryLevel.Info, message);
	}

