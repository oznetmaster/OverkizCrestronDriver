// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
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
using System.IO;

namespace OverKiz.CrestronDriver;

/// <summary>
/// Holds the parsed configuration for one room group entry.
/// <see cref="RoomDisplayName"/> is the display title for the room aggregate (falls back to the room key when not specified).
/// <see cref="Members"/> lists each shade with its Overkiz API label (used for matching) and an optional display name.
/// </summary>
internal sealed class RoomGroupEntry (string roomDisplayName, IReadOnlyList<RoomMemberConfig> members)
	{
	public string RoomDisplayName { get; } = roomDisplayName;
	public IReadOnlyList<RoomMemberConfig> Members { get; } = members;
	}

/// <summary>Pairing of the Overkiz API label (used for identity/matching) and the display name shown in the UI.</summary>
internal sealed class RoomMemberConfig (string apiLabel, string displayName)
	{
	/// <summary>Overkiz API label — used for device matching.</summary>
	public string ApiLabel { get; } = apiLabel;
	/// <summary>Display label shown as the subheading in the room tile (falls back to <see cref="ApiLabel"/> when not specified).</summary>
	public string DisplayName { get; } = displayName;
	}

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
	private string _roomGroupsRaw = string.Empty;
	private string _shadeDisplayNamesRaw = string.Empty;

	/// <summary>Parsed room groups: room key → entry with display name and per-member (apiLabel, displayName) pairs.</summary>
	private Dictionary<string, RoomGroupEntry> _roomGroups = [];

	/// <summary>Optional per-shade display name overrides: normalised API label → display name.</summary>
	private Dictionary<string, string> _shadeDisplayNames = new (StringComparer.OrdinalIgnoreCase);

	private bool IsLocalMode =>
		!string.IsNullOrWhiteSpace (_gatewayIp) && !string.IsNullOrWhiteSpace (_localToken);

	// ── Last-applied config snapshot (used to suppress redundant reconnects) ─

	private string _appliedUsername = null;
	private string _appliedPassword = null;
	private string _appliedServer = null;
	private string _appliedIp = null;
	private string _appliedToken = null;
	private string _appliedRoomGroups = null;
	private string _appliedShadeDisplayNames = null;

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
	private readonly Dictionary<string, IOverkizEntity> _pendingExecs = [];
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

	/// <summary>placeOid → room aggregate entity.  Guarded by _entitiesLock.</summary>
	private readonly Dictionary<string, OverkizRoomEntity> _roomEntities
		= new (StringComparer.OrdinalIgnoreCase);

	private readonly object _entitiesLock = new ();

	// ── Room / place tracking (lock-free, copy-on-write lists) ───────────

	/// <summary>placeOid → user-visible room label.</summary>
	private readonly ConcurrentDictionary<string, string> _placeLabels
		= new (StringComparer.OrdinalIgnoreCase);

	/// <summary>deviceUrl → placeOid of its assigned room.</summary>
	private readonly ConcurrentDictionary<string, string> _shadeToRoom
		= new (StringComparer.OrdinalIgnoreCase);

	/// <summary>placeOid → snapshot list of deviceUrls in that room.
	/// The list is <em>replaced</em>, never mutated in place, so any reader
	/// holding a reference sees a stable snapshot.</summary>
	private readonly ConcurrentDictionary<string, List<string>> _roomToShades
		= new (StringComparer.OrdinalIgnoreCase);

	// ── Connect cancellation ─────────────────────────────────────────────

	private CancellationTokenSource _connectCts;
	private bool _connectInFlight;
	private bool _hasConnectedWithAppliedConfig;
	private readonly object _connectLock = new ();

	// ── Registration gate (signals ApplyConfigurationItems when UpdateSubControllers is done) ──

	private TaskCompletionSource<bool> _registrationTcs;

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
		Log ("Constructor START");
		_logger = args.Logger;
		_args = args;
		_resources = resources;

		// TEMP DIAG: write DriverId and Logger presence to file so we can see the actual key value
		try
			{
			var diagDir = args.DriverDataDirectoryPath ?? "/tmp";
			if (!Directory.Exists (diagDir))
				_ = Directory.CreateDirectory (diagDir);
			var diagContent = $"DriverId={args.DriverId}\nControllerId={DriverController.RootControllerId}\nLogger={(args.Logger == null ? "NULL" : args.Logger.GetType ().FullName)}\nDriverDataDirectoryPath={args.DriverDataDirectoryPath}\n";
			File.WriteAllText (Path.Combine (diagDir, "diag_logger.txt"), diagContent);
			// Also try /tmp as fallback
			File.WriteAllText ("/tmp/overkiz_diag.txt", diagContent);
			}
		catch (Exception diagEx)
			{
			// Write failure info somewhere we can read it
			try
				{
				File.WriteAllText ("/tmp/overkiz_diag_error.txt", diagEx.ToString ());
				}
			catch { }
			}

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

		ManagedDevices = new Dictionary<string, PlatformManagedDevice> ();

		Log ("Constructor END");
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
			foreach (OverkizRoomEntity r in _roomEntities.Values)
				r.StopPolling ();
			}

		DisposeClient ();
		_httpClient?.Dispose ();
		base.Dispose ();
		}

	// ── Private: configuration callback ──────────────────────────────────

	private ConfigurationItemErrors ValidateConnectionConfiguration ()
		{
		var errors = new Dictionary<string, string> ();

		bool hasGatewayIp = !string.IsNullOrWhiteSpace (_gatewayIp);
		bool hasLocalToken = !string.IsNullOrWhiteSpace (_localToken);
		bool hasAnyLocal = hasGatewayIp || hasLocalToken;
		bool hasCompleteLocal = hasGatewayIp && hasLocalToken;

		bool hasCloudUsername = !string.IsNullOrWhiteSpace (_cloudUsername);
		bool hasCloudPassword = !string.IsNullOrWhiteSpace (_cloudPassword);
		bool hasCompleteCloud = hasCloudUsername && hasCloudPassword;

		// Local mode wins if the user supplied either local field.
		// Do not silently fall back to cloud if local config is partial.
		if (hasAnyLocal)
			{
			if (!hasGatewayIp)
				errors["GatewayIP"] = "Gateway IP is required when using local mode.";

			if (!hasLocalToken)
				errors["LocalToken"] = "Local token is required when using local mode.";
			}
		else if (!hasCompleteCloud)
			{
			if (!hasCloudUsername)
				errors["CloudUsername"] = "Cloud username is required when local mode is not configured.";

			if (!hasCloudPassword)
				errors["CloudPassword"] = "Cloud password is required when local mode is not configured.";
			}

		if (errors.Count == 0)
			return null;

		return new ConfigurationItemErrors (
			errors,
			"Enter either both local gateway values, or both cloud username and password.");
		}

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

				if (values.TryGetValue ("RoomGroups", out v) && v.HasValue)
					_roomGroupsRaw = v.Value.GetValue<string> () ?? _roomGroupsRaw;

				if (values.TryGetValue ("ShadeDisplayNames", out v) && v.HasValue)
					_shadeDisplayNamesRaw = v.Value.GetValue<string> () ?? _shadeDisplayNamesRaw;

				ConfigurationItemErrors configErrors = ValidateConnectionConfiguration ();
				if (configErrors != null)
					{
					SetReady (false);
					SetOnline (false);
					return configErrors;
					}

				_roomGroups = ParseRoomGroups (_roomGroupsRaw);
				_shadeDisplayNames = ParseShadeDisplayNames (_shadeDisplayNamesRaw);

				var connectionChanged =
					_cloudUsername != _appliedUsername ||
					_cloudPassword != _appliedPassword ||
					_cloudServer != _appliedServer ||
					_gatewayIp != _appliedIp ||
					_localToken != _appliedToken;

				var displayChanged =
					_roomGroupsRaw != _appliedRoomGroups ||
					_shadeDisplayNamesRaw != _appliedShadeDisplayNames;

				bool shouldConnect;
				lock (_connectLock)
					{
					// Same config while a connect is already running: do nothing.
					if (_connectInFlight && !connectionChanged)
						shouldConnect = false;
					else
						shouldConnect =
							connectionChanged ||
							!_hasConnectedWithAppliedConfig ||
							!OnlineIndicatorIsOnline;
					}

				Log (
					"ApplyConfigurationItems: shouldConnect=" + shouldConnect +
					" connectionChanged=" + connectionChanged +
					" displayChanged=" + displayChanged +
					" connectInFlight=" + _connectInFlight +
					" hasConnected=" + _hasConnectedWithAppliedConfig);

				if (shouldConnect)
					{
					SetReady (true);

					_appliedUsername = _cloudUsername;
					_appliedPassword = _cloudPassword;
					_appliedServer = _cloudServer;
					_appliedIp = _gatewayIp;
					_appliedToken = _localToken;
					_appliedRoomGroups = _roomGroupsRaw;
					_appliedShadeDisplayNames = _shadeDisplayNamesRaw;

					Connect ();
					Log ("ApplyConfigurationItems: Connect called");
					}
				else if (displayChanged)
					{
					_appliedRoomGroups = _roomGroupsRaw;
					_appliedShadeDisplayNames = _shadeDisplayNamesRaw;

					ApplyDisplayConfig ();
					}
				else
					{
					Log ("ApplyConfigurationItems: same config already applied – skipping");
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
		TaskCompletionSource<bool> tcs;
		lock (_connectLock)
			{
			_connectCts?.Cancel ();
			_connectCts?.Dispose ();
			_connectCts = new CancellationTokenSource ();
			cts = _connectCts;
			_connectInFlight = true;
			_registrationTcs = new TaskCompletionSource<bool> ();
			tcs = _registrationTcs;
			}

		_ = Task.Run (async () =>
			{
				try
					{
					Log ("Connect task started; isLocalMode=" + IsLocalMode);

					cts.Token.ThrowIfCancellationRequested ();

					// Stop event loop and dispose old client, but preserve children
					StopEventLoop ();
					DisposeClient ();

					cts.Token.ThrowIfCancellationRequested ();
					await ConnectClientAsync ().ConfigureAwait (false);

					cts.Token.ThrowIfCancellationRequested ();
					SetOnline (true);
					SetReady (true);

					cts.Token.ThrowIfCancellationRequested ();
					await DiscoverDevicesAsync (cts.Token).ConfigureAwait (false);

					_ = tcs.TrySetResult (true);

					cts.Token.ThrowIfCancellationRequested ();

					// Known issue/workaround context:
					// After reboot, the first commissioned child under this gateway can fail to reach full
					// Crestron wrapper promotion (status-only / not controllable), while removing and re-adding
					// the same child in the same gateway session can then succeed. Discovery, entity registration,
					// platform:managedDevices publication, control-surface registration, child polling, and event
					// listener startup have all been verified before that first failed commission. If this startup
					// sequence is revisited, preserve the current ordering evidence and keep the proven operational
					// workaround documented in GPT55_DIAGNOSIS.md with any changes.
					StartAllChildPolling ();
					StartEventLoop ();

					lock (_connectLock)
						{
						if (ReferenceEquals (cts, _connectCts))
							_hasConnectedWithAppliedConfig = true;
						}
					}
				catch (OperationCanceledException)
					{
					_ = tcs.TrySetCanceled ();
					Log ("Connect superseded by newer request");
					}
				catch (Exception ex)
					{
					_ = tcs.TrySetException (ex);

					SetOnline (false);
					SetReady (false);
					StopEventLoop ();
					StopAllChildPolling ();

					lock (_connectLock)
						{
						if (ReferenceEquals (cts, _connectCts))
							_hasConnectedWithAppliedConfig = false;
						}

					Log ("Connect failed: " + ex);
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

	private void StopAllChildPolling ()
		{
		lock (_entitiesLock)
			{
			foreach (IOverkizEntity e in _entities.Values)
				e.StopPolling ();

			foreach (OverkizRoomEntity r in _roomEntities.Values)
				r.StopPolling ();
			}
		}

	private void StartAllChildPolling ()
		{
		lock (_entitiesLock)
			{
			foreach (IOverkizEntity e in _entities.Values)
				{
				Log ("StartAllChildPolling - StartPolling for " + e.ControllerId + " type=" + e.GetType ().Name);
				if (e is OverkizShadeEntity shade)
					Log ("StartAllChildPolling - shade pre-state id=" + shade.ControllerId + " online=" + shade.OnlineIndicatorIsOnline + " ready=" + shade.ReadyIndicatorIsReady + " label='" + shade.DeviceLabel + "'");
				e.StartPolling (_workQueue);
				if (e is OverkizShadeEntity startedShade)
					Log ("StartAllChildPolling - shade post-state id=" + startedShade.ControllerId + " online=" + startedShade.OnlineIndicatorIsOnline + " ready=" + startedShade.ReadyIndicatorIsReady + " label='" + startedShade.DeviceLabel + "'");
				}

			foreach (OverkizRoomEntity r in _roomEntities.Values)
				{
				Log ("StartAllChildPolling - StartPolling for room " + r.ControllerId);
				Log ("StartAllChildPolling - room pre-state id=" + r.ControllerId + " online=" + r.OnlineIndicatorIsOnline + " ready=" + r.ReadyIndicatorIsReady + " label='" + r.DeviceLabel + "'");
				r.StartPolling (_workQueue);
				Log ("StartAllChildPolling - room post-state id=" + r.ControllerId + " online=" + r.OnlineIndicatorIsOnline + " ready=" + r.ReadyIndicatorIsReady + " label='" + r.DeviceLabel + "'");
				}
			}
		}

	private void PrepareForReconnect ()
		{
		StopEventLoop ();
		StopAllChildPolling ();
		DisposeClient ();
		SetOnline (false);
		}

	private void Disconnect ()
		{
		// Clear the snapshot so the next ApplyConfigurationItems triggers a fresh connect.
		_appliedUsername = null;
		_appliedPassword = null;
		_appliedServer = null;
		_appliedIp = null;
		_appliedToken = null;

		lock (_connectLock)
			{
			_connectCts?.Cancel ();
			_connectCts?.Dispose ();
			_connectCts = null;
			_connectInFlight = false;
			_hasConnectedWithAppliedConfig = false;
			}

		StopEventLoop ();
		lock (_entitiesLock)
			{
			foreach (IOverkizEntity e in _entities.Values)
				e.StopPolling ();
			foreach (OverkizRoomEntity r in _roomEntities.Values)
				r.StopPolling ();
			}

		DisposeClient ();
		SetOnline (false);
		}

	// ── Private: API client lifecycle ─────────────────────────────────────

	private async Task ConnectClientAsync ()
		{
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

	// ── Room / place helpers ──────────────────────────────────────────────

	/// <summary>
	/// Recursively walks the <see cref="Place"/> tree rooted at
	/// <paramref name="place"/> and populates <see cref="_placeLabels"/>
	/// with every oid → label pair found.
	/// </summary>
	private void BuildPlaceLabels (Place place)
		{
		if (place?.Oid == null)
			return;

		_placeLabels[place.Oid] = place.Label ?? place.Oid;

		if (place.SubPlaces == null)
			return;

		foreach (Place child in place.SubPlaces)
			BuildPlaceLabels (child);
		}

	/// <summary>
	/// Records that <paramref name="deviceUrl"/> belongs to
	/// <paramref name="placeOid"/>.  Both tables are updated atomically
	/// using copy-on-write so no lock is required.
	/// </summary>
	private void TrackShadeInRoom (string deviceUrl, string placeOid)
		{
		if (deviceUrl == null || placeOid == null)
			return;

		_shadeToRoom[deviceUrl] = placeOid;

		_ = _roomToShades.AddOrUpdate (
			placeOid,
			_ => [deviceUrl],
			(_, existing) =>
				{
					if (existing.Contains (deviceUrl, StringComparer.OrdinalIgnoreCase))
						return existing;                         // already present — keep same snapshot
					var next = new List<string> (existing) { deviceUrl };
					return next;
				});
		}

	/// <summary>
	/// Removes <paramref name="deviceUrl"/> from both room-tracking tables.
	/// </summary>
	private void UntrackShadeFromRoom (string deviceUrl)
		{
		if (deviceUrl == null)
			return;

		if (!_shadeToRoom.TryRemove (deviceUrl, out var placeOid))
			return;

		_ = _roomToShades.AddOrUpdate (
			placeOid,
			_ => [],
			(_, existing) =>
				{
					var next = existing
						.Where (u => !string.Equals (u, deviceUrl, StringComparison.OrdinalIgnoreCase))
						.ToList ();
					return next;
				});
		}

	/// <summary>
	/// Returns the number of shades currently tracked in the room identified
	/// by <paramref name="placeOid"/>, or 0 if unknown.
	/// </summary>
	internal int GetRoomShadeCount (string placeOid) =>
		placeOid != null && _roomToShades.TryGetValue (placeOid, out List<string> shades) ? shades.Count : 0;

	/// <summary>
	/// Returns the user-visible label for <paramref name="placeOid"/>,
	/// or <c>null</c> if not yet populated.
	/// </summary>
	internal string GetRoomLabel (string placeOid) =>
		placeOid != null && _placeLabels.TryGetValue (placeOid, out var label) ? label : null;

	/// <summary>
	/// Returns the placeOid the shade at <paramref name="deviceUrl"/> belongs
	/// to, or <c>null</c> if not tracked.
	/// </summary>
	internal string GetShadeRoom (string deviceUrl) =>
		deviceUrl != null && _shadeToRoom.TryGetValue (deviceUrl, out var oid) ? oid : null;

	// ── Room entity helpers ───────────────────────────────────────────────

	/// <summary>
	/// Builds the controller ID used for the room aggregate entity.
	/// </summary>
	private static string RoomControllerId (string placeOid) =>
		"room_" + MakeSafeControllerId (placeOid);

	/// <summary>
	/// Sends <paramref name="command"/> (with optional <paramref name="parameters"/>)
	/// to every shade currently tracked in <paramref name="placeOid"/>.
	/// </summary>
	private void SendRoomCommand (string placeOid, string command, object[] parameters = null)
		{
		if (!_roomToShades.TryGetValue (placeOid, out List<string> shades))
			return;
		foreach (var url in shades)
			SendDeviceCommand (url, command, parameters);
		}

	/// <summary>
	/// Creates and registers a <see cref="OverkizRoomEntity"/> for
	/// <paramref name="placeOid"/> and adds it to the managed-devices snapshot.
	/// Must be called while <see cref="_entitiesLock"/> is held.
	/// Returns the updated managed-devices copy (caller must assign to
	/// <see cref="ManagedDevices"/> and call <see cref="ReflectedAttributeDriverEntity.NotifyPropertyChanged"/>).
	/// </summary>
	private (OverkizRoomEntity entity, Dictionary<string, PlatformManagedDevice> devices) CreateRoomEntityLocked (
		string placeOid,
		Dictionary<string, PlatformManagedDevice> existing)
		{
		if (_roomEntities.ContainsKey (placeOid))
			return (null, existing);

		var label = GetRoomLabel (placeOid) ?? placeOid;
		var cid = RoomControllerId (placeOid);

		// Prefer config-supplied room display name; fall back to API/place label.
		string roomDisplayName;
		IReadOnlyList<RoomMemberConfig> slotConfigs;
		if (_roomGroups.TryGetValue (placeOid, out RoomGroupEntry grp))
			{
			roomDisplayName = grp.RoomDisplayName;
			slotConfigs = grp.Members;
			}
		else
			{
			roomDisplayName = label;
			// No configured slots — build from whatever members exist right now.
			List<RoomMember> fallbackMembers = BuildMemberList (placeOid);
			slotConfigs = [.. fallbackMembers.Select (m => new RoomMemberConfig (m.Label, m.Label))];
			}

		// Use config display name if explicitly provided; only fall back to stored name when no config display name is set.
		bool hasConfigDisplayName = _roomGroups.ContainsKey (placeOid) && !string.IsNullOrEmpty (roomDisplayName);
		var roomLabel = hasConfigDisplayName
			? roomDisplayName
			: ManagedDevices != null && ManagedDevices.TryGetValue (cid, out PlatformManagedDevice stored)
				? stored.Name ?? roomDisplayName
				: roomDisplayName;

		List<RoomMember> members = BuildMemberList (placeOid);

		Log ("CreateRoomEntityLocked: placeOid=" + placeOid + " label=" + roomLabel + " members=" + members.Count);

		var room = new OverkizRoomEntity (
			controllerId: cid,
			roomLabel: roomLabel,
			slotConfigs: slotConfigs,
			members: members,
			openAll: () => SendRoomCommand (placeOid, "open"),
			closeAll: () => SendRoomCommand (placeOid, "close"),
			stopAll: () => SendRoomCommand (placeOid, "stop"),
			myAll: () => SendRoomCommand (placeOid, "my"),
			setOpenPercentAll: pct =>
				{
					var closure = 100 - Math.Max (0, Math.Min (100, pct));
					SendRoomCommand (placeOid, "setClosure", [closure]);
				},
			initLogger: _resources.InitLogger,
				logger: _logger,
				resources: _resources,
				driverDataDirectoryPath: _args.DriverDataDirectoryPath);

		_roomEntities[placeOid] = room;

		// Preserve the user-defined name if Crestron Home has already stored one for this controller.
		PlatformManagedDevice managedDevice =
			ManagedDevices != null && ManagedDevices.TryGetValue (cid, out PlatformManagedDevice prev)
				? prev
				: new PlatformManagedDevice (DeviceUxCategory.Room, roomLabel, "Somfy / Overkiz", "Room", null);

		var copy = new Dictionary<string, PlatformManagedDevice> (existing)
			{
			[cid] = managedDevice
			};
		return (room, copy);
		}

	/// <summary>
	/// Builds the <see cref="RoomMember"/> list for <paramref name="placeOid"/> from
	/// the current <see cref="_roomToShades"/> and <see cref="_entities"/> state.
	/// Must be called while <see cref="_entitiesLock"/> is held.
	/// </summary>
	private List<RoomMember> BuildMemberList (string placeOid)
		{
		var members = new List<RoomMember> ();
		if (!_roomToShades.TryGetValue (placeOid, out List<string> memberUrls))
			return members;

		// Build a lookup of configured display names for this room's members.
		var displayNameMap = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
		if (_roomGroups.TryGetValue (placeOid, out RoomGroupEntry entry))
			{
			foreach (RoomMemberConfig cfg in entry.Members)
				displayNameMap[cfg.ApiLabel] = cfg.DisplayName;
			}

		foreach (var url in memberUrls)
			{
			if (_entities.TryGetValue (url, out IOverkizEntity e) && e is OverkizShadeEntity shade)
				{
				var capturedUrl = url;
				var apiLabel = shade.ApiLabel;
				var displayName = displayNameMap.TryGetValue (apiLabel, out string dn) ? dn : apiLabel;
				members.Add (new RoomMember (
					label: apiLabel,
					displayName: displayName,
					isTwoWay: shade.IsTwoWay,
					hasMy: shade.HasMyCommand,
					open: () => SendDeviceCommand (capturedUrl, "open"),
					close: () => SendDeviceCommand (capturedUrl, "close"),
					stop: () => SendDeviceCommand (capturedUrl, "stop"),
					my: () => SendDeviceCommand (capturedUrl, "my"),
					setOpenPercent: pct =>
						{
							var closure = 100 - Math.Max (0, Math.Min (100, pct));
							SendDeviceCommand (capturedUrl, "setClosure", [closure]);
						},
					getOpenPercent: () => shade.OpenPercent));
				}
			}

		return members;
		}

	private void LogDiscoveryAudit (
		string entityType,
		string label,
		string overkizUrl,
		string entityControllerId,
		string configurableDriverEntityId,
		string managedDevicesKey,
		PlatformManagedDevice managedDevice)
		{
		Log (
			"DISCOVERY AUDIT: " +
			"EntityType=" + entityType +
			" Label='" + label + "'" +
			" ControllerId=" + entityControllerId +
			" ConfigurableDriverEntityId=" + configurableDriverEntityId +
			" ManagedDevicesKey=" + managedDevicesKey +
			" ManagedDevice.Name='" + managedDevice?.Name + "'" +
			" ManagedDevice.Category=" + managedDevice?.UxCategory +
			" ManagedDevice.Model='" + managedDevice?.Model + "'" +
			" ManagedDevice.Manufacturer='" + managedDevice?.Manufacturer + "'" +
			" OverkizUrl='" + overkizUrl + "'");
		}

	/// <summary>
	/// Updates an existing room entity's members in place (or creates it if it does not yet
	/// exist). Unlike the old Destroy+Create pattern this preserves the entity's
	/// <c>ControllerId</c> and any user-defined <c>deviceLabel</c> already written by
	/// Crestron Home, so no <c>UpdateSubControllers</c> remove/add cycle is needed.
	/// Must be called while <see cref="_entitiesLock"/> is held.
	/// Returns <c>true</c> if the entity already existed and was updated in place,
	/// <c>false</c> if it was newly created (caller must register with the framework).
	/// </summary>
	private bool RebuildRoomMembersLocked (
		string placeOid,
		ref Dictionary<string, PlatformManagedDevice> managedDevices,
		out OverkizRoomEntity room)
		{
		List<RoomMember> members = BuildMemberList (placeOid);
		IReadOnlyList<RoomMemberConfig> slotConfigs = _roomGroups.TryGetValue (placeOid, out RoomGroupEntry entry)
			? entry.Members
			: [.. members.Select (m => new RoomMemberConfig (m.Label, m.DisplayName))];

		if (_roomEntities.TryGetValue (placeOid, out room))
			{
			// Entity already registered — update members in place.
			room.UpdateMembers (members, slotConfigs);
			Log ("RebuildRoomMembersLocked: updated in place placeOid=" + placeOid + " members=" + members.Count);
			return true;
			}

		// Entity does not exist yet — create it.
		(OverkizRoomEntity created, Dictionary<string, PlatformManagedDevice> afterCreate) =
			CreateRoomEntityLocked (placeOid, managedDevices);
		if (created != null)
			{
			managedDevices = afterCreate;
			room = created;
			}

		return false;
		}

	/// <summary>
	/// Removes the room aggregate entity for <paramref name="placeOid"/> and
	/// returns the updated managed-devices copy.
	/// Must be called while <see cref="_entitiesLock"/> is held.
	/// </summary>
	private (OverkizRoomEntity entity, Dictionary<string, PlatformManagedDevice> devices) DestroyRoomEntityLocked (
		string placeOid,
		Dictionary<string, PlatformManagedDevice> existing)
		{
		if (!_roomEntities.TryGetValue (placeOid, out OverkizRoomEntity room))
			return (null, existing);

		_ = _roomEntities.Remove (placeOid);
		Dictionary<string, PlatformManagedDevice> copy = new Dictionary<string, PlatformManagedDevice> (existing);
		_ = copy.Remove (room.ControllerId);
		return (room, copy);
		}

	/// <summary>
	/// Applies updated display-name configuration to already-discovered entities
	/// without reconnecting. Updates shade display labels and rebuilds room member
	/// lists in place.
	/// </summary>
	private void ApplyDisplayConfig ()
		{
		Log ("ApplyDisplayConfig: applying updated display names");
		lock (_entitiesLock)
			{
			// Update each shade's display label.
			foreach (IOverkizEntity e in _entities.Values)
				{
				if (e is OverkizShadeEntity shade)
					{
					_ = _shadeDisplayNames.TryGetValue (shade.ApiLabel, out string dn);
					shade.UpdateDisplayName (dn);
					}
				}

			// Rebuild room member lists in place and update room titles.
			var managedDevices = ManagedDevices != null
				? new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
				: new Dictionary<string, PlatformManagedDevice> ();

			foreach (string placeOid in _roomEntities.Keys)
				{
				_ = RebuildRoomMembersLocked (placeOid, ref managedDevices, out OverkizRoomEntity room);

				if (room != null &&
					_roomGroups.TryGetValue (placeOid, out RoomGroupEntry grp) &&
					!string.IsNullOrEmpty (grp.RoomDisplayName))
					{
					room.UpdateLabel (grp.RoomDisplayName);
					}
				}

			ManagedDevices = managedDevices;
			NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
			}
		}

	// ── Private: room-group config parsing ───────────────────────────────

	/// <summary>
	/// Returns the room-group name that lists <paramref name="shadeLabel"/>,
	/// or <c>null</c> if the label does not appear in any configured group.
	/// </summary>
	private string FindRoomGroupForLabel (string shadeLabel)
		{
		if (string.IsNullOrEmpty (shadeLabel))
			return null;
		foreach (KeyValuePair<string, RoomGroupEntry> kv in _roomGroups)
			{
			if (kv.Value.Members.Any (m => string.Equals (m.ApiLabel, shadeLabel, StringComparison.OrdinalIgnoreCase)))
				return kv.Key;
			}

		return null;
		}

	/// <summary>
	/// Strips all leading/trailing Unicode whitespace (including non-breaking spaces)
	/// and collapses internal runs of whitespace to a single ASCII space.
	/// </summary>
	private static string NormalizeLabel (string s)
		{
		if (s == null)
			return string.Empty;
		// Replace all Unicode whitespace variants with a plain space, then trim.
		var sb = new System.Text.StringBuilder (s.Length);
		foreach (var c in s)
			_ = sb.Append (char.IsWhiteSpace (c) ? ' ' : c);
		// Collapse multiple spaces and trim ends.
		return System.Text.RegularExpressions.Regex.Replace (sb.ToString (), @" {2,}", " ").Trim ();
		}

	/// <summary>
	/// Parses the single-line RoomGroups config string into a dictionary.
	/// Format: "RoomKey:Room Display Title=Shade1:Shade Display 1,Shade2; RoomKey2=Shade3"
	/// The part before <c>:</c> in the room segment is the API key used for matching;
	/// the optional part after <c>:</c> is the display name shown as the room title.
	/// Similarly for each member shade: "ApiLabel:Display Name".
	/// When a display name is omitted the API label is used for display.
	/// </summary>
	private static Dictionary<string, RoomGroupEntry> ParseRoomGroups (string raw)
		{
		var result = new Dictionary<string, RoomGroupEntry> (StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace (raw))
			return result;

		foreach (var group in raw.Split (';'))
			{
			var eq = group.IndexOf ('=');
			if (eq <= 0)
				continue;

			// Parse room key and optional room display name: "RoomKey:Room Title"
			var roomSegment = group[..eq];
			var roomColon = roomSegment.IndexOf (':');
			string roomKey, roomDisplayName;
			if (roomColon > 0)
				{
				roomKey = NormalizeLabel (roomSegment[..roomColon]);
				roomDisplayName = NormalizeLabel (roomSegment[(roomColon + 1)..]);
				if (string.IsNullOrEmpty (roomDisplayName))
					roomDisplayName = roomKey;
				}
			else
				{
				roomKey = NormalizeLabel (roomSegment);
				roomDisplayName = roomKey;
				}

			if (string.IsNullOrEmpty (roomKey))
				continue;

			// Parse member list: "ApiLabel:Display Name,ApiLabel2:Display Name2,..."
			var members = group[(eq + 1)..]
				.Split (',')
				.Select (part =>
					{
						var colon = part.IndexOf (':');
						if (colon > 0)
							{
							var api = NormalizeLabel (part[..colon]);
							var display = NormalizeLabel (part[(colon + 1)..]);
							if (string.IsNullOrEmpty (display))
								display = api;
							return new RoomMemberConfig (api, display);
							}
						else
							{
							var api = NormalizeLabel (part);
							return new RoomMemberConfig (api, api);
							}
					})
				.Where (m => m.ApiLabel.Length > 0)
				.ToList ();

			if (members.Count > 0)
				result[roomKey] = new RoomGroupEntry (roomDisplayName, members);
			}

		return result;
		}

	/// <summary>
	/// Parses the <c>ShadeDisplayNames</c> config string into a lookup dictionary.
	/// Format: "ApiLabel:Display Name;ApiLabel2:Display Name2"
	/// Each entry maps the Overkiz API label to the display name shown as the shade title.
	/// Entries without a <c>:</c> separator are ignored.
	/// </summary>
	private static Dictionary<string, string> ParseShadeDisplayNames (string raw)
		{
		var result = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace (raw))
			return result;

		foreach (var entry in raw.Split (';'))
			{
			var colon = entry.IndexOf (':');
			if (colon <= 0)
				continue;

			var apiLabel = NormalizeLabel (entry[..colon]);
			var displayName = NormalizeLabel (entry[(colon + 1)..]);
			if (apiLabel.Length > 0 && displayName.Length > 0)
				result[apiLabel] = displayName;
			}

		return result;
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

		List<ConfigurableDriverEntity> controllersToAdd = [];
		Dictionary<string, PlatformManagedDevice> managedDevicesCopy =
			ManagedDevices != null
				? new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
				: new Dictionary<string, PlatformManagedDevice> ();

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

			var label = NormalizeLabel (device.Label ?? url);
			Log ("Queuing device: " + label + " | UIClass: " + (device.UiClass.HasValue ? device.UiClass.Value.ToString () : "none") + " | Protocol: " + (device.Protocol.HasValue ? device.Protocol.Value.ToString () : "unknown") + " | URL: " + url);

			lock (_entitiesLock)
				{
				if (_entities.ContainsKey (url))
					continue;

				_entities[url] = entity;

				var configurableEntity = new ConfigurableDriverEntity (entity.ControllerId, (ReflectedAttributeDriverEntity)entity, null);
				controllersToAdd.Add (configurableEntity);
				var managedDevice = new PlatformManagedDevice (
						entity.UxCategory,
						label,
						"Somfy / Overkiz",
						device.UiClass.HasValue ? device.UiClass.Value.ToString () : entity.UxCategory.ToString (),
						null);
				managedDevicesCopy[entity.ControllerId] = managedDevice;
				LogDiscoveryAudit (
					entityType: entity.GetType ().Name,
					label: label,
					overkizUrl: url,
					entityControllerId: entity.ControllerId,
					configurableDriverEntityId: entity.ControllerId,
					managedDevicesKey: entity.ControllerId,
					managedDevice: managedDevice);

				Log ("Queued device: " + label + " (id=" + entity.ControllerId + ", url=" + url + ")");
				}
			}

		if (controllersToAdd.Count > 0)
			{
			ct.ThrowIfCancellationRequested ();
			Log ("DiscoverDevicesAsync - UpdateSubControllers start count=" + controllersToAdd.Count);
			UpdateSubControllers (controllersToAdd, null);
			Log ("DiscoverDevicesAsync - UpdateSubControllers complete count=" + controllersToAdd.Count);

			ManagedDevices = managedDevicesCopy;
			Log ("DiscoverDevicesAsync - publishing platform:managedDevices count=" + ManagedDevices.Count);
			NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

			Log ("Published " + controllersToAdd.Count + " shade(s)");

			// Diagnostic: log what's in ManagedDevices
			foreach (var kvp in managedDevicesCopy)
				Log ("  ManagedDevice[" + kvp.Key + "] = Name:'" + kvp.Value.Name + "', Mfr:'" + kvp.Value.Manufacturer + "'");
			}

		// Build room groupings unconditionally — a previously-known shade may now
		// satisfy the minimum count even if no new shades were discovered this pass.
		if (_roomGroups.Count > 0)
			{
			ct.ThrowIfCancellationRequested ();

			// Build a label → url reverse map from all known entities (not just newly queued ones).
			var labelToUrl = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
			lock (_entitiesLock)
				{
				foreach (KeyValuePair<string, IOverkizEntity> kv in _entities)
					{
					if (kv.Value is OverkizShadeEntity shade)
						{
						labelToUrl[shade.ApiLabel] = kv.Key;
						Log ("RoomGroup labelMap: '" + shade.ApiLabel + "' → " + kv.Key);
						}
					}
				}

			// Reset room tracking so we always rebuild from the full known set.
			lock (_entitiesLock)
				{
				foreach (KeyValuePair<string, RoomGroupEntry> group in _roomGroups)
					{
					var roomKey = group.Key;
					RoomGroupEntry entry = group.Value;
					var matchedUrls = entry.Members
						.Where (m => labelToUrl.ContainsKey (m.ApiLabel))
						.Select (m => labelToUrl[m.ApiLabel])
						.ToList ();

					if (matchedUrls.Count == 0)
						{
						Log ("RoomGroup '" + roomKey + "': no matching shades found");
						continue;
						}

					_placeLabels[roomKey] = entry.RoomDisplayName;
					foreach (var shadeUrl in matchedUrls)
						TrackShadeInRoom (shadeUrl, roomKey);

					Log ("RoomGroup '" + roomKey + "': matched " + matchedUrls.Count + " shade(s): " + string.Join (", ", entry.Members.Where (m => labelToUrl.ContainsKey (m.ApiLabel)).Select (m => m.ApiLabel)));
					}
				}

			// Create room aggregate entities for any room with ≥1 tracked shade.
			// If more shades arrive later (via events), the room will be rebuilt then.
			List<ConfigurableDriverEntity> roomControllersToAdd = [];
			managedDevicesCopy = new Dictionary<string, PlatformManagedDevice> (ManagedDevices ?? managedDevicesCopy);
			lock (_entitiesLock)
				{
				foreach (KeyValuePair<string, List<string>> kv in _roomToShades.ToList ())
					{
					if (kv.Value.Count < 1)
						continue;
					(OverkizRoomEntity roomEntity, Dictionary<string, PlatformManagedDevice> updatedDevices) = CreateRoomEntityLocked (kv.Key, managedDevicesCopy);
					if (roomEntity != null)
						{
						managedDevicesCopy = updatedDevices;
						roomControllersToAdd.Add (new ConfigurableDriverEntity (roomEntity.ControllerId, roomEntity, null));
						Log ("Room entity created: " + roomEntity.ControllerId + " (" + kv.Value.Count + " shade(s))");
						}
					}
				}

			if (roomControllersToAdd.Count > 0)
				{
				UpdateSubControllers (roomControllersToAdd, null);
				ManagedDevices = managedDevicesCopy;
				NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
				}

			// Start polling for room entities (marks them online).
			lock (_entitiesLock)
				{
				foreach (OverkizRoomEntity r in _roomEntities.Values)
					r.StartPolling (_workQueue);
				}

			Log ("Published " + roomControllersToAdd.Count + " room(s)");
			}
		else
			{
			Log ("RoomGroups not configured — no room entities will be created");
			}

		Log ("Discovery complete");
		}

	/// <summary>
	/// UNUSED. Do not call during ordinary reconnect; reconnect preserves the
	/// existing child entities and merely restarts transport/polling.
	/// Re-registers all already-discovered entities with the current framework
	/// context.  Called on every subsequent <see cref="Connect"/> after the
	/// initial discovery so that the active service context owns the children.
	/// </summary>
	private void RegisterEntitiesWithFramework ()
		{
		List<ConfigurableDriverEntity> controllers = [];
		Dictionary<string, PlatformManagedDevice> managedDevices = [];

		lock (_entitiesLock)
			{
			foreach (KeyValuePair<string, IOverkizEntity> kv in _entities)
				{
				IOverkizEntity entity = kv.Value;
				controllers.Add (new ConfigurableDriverEntity (entity.ControllerId, (ReflectedAttributeDriverEntity)entity, null));

				if (ManagedDevices != null && ManagedDevices.TryGetValue (entity.ControllerId, out PlatformManagedDevice existing))
					managedDevices[entity.ControllerId] = existing;
				}

			// Re-register room aggregate entities.
			foreach (KeyValuePair<string, OverkizRoomEntity> kv in _roomEntities)
				{
				OverkizRoomEntity room = kv.Value;
				controllers.Add (new ConfigurableDriverEntity (room.ControllerId, room, null));

				if (ManagedDevices != null && ManagedDevices.TryGetValue (room.ControllerId, out PlatformManagedDevice existingRoom))
					managedDevices[room.ControllerId] = existingRoom;
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
			CancellationToken token = _eventCts.Token;
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
		const int RETRY_DELAY_MS = 10_000;

		OverkizClient eventClient;
		lock (_clientLock)
			eventClient = _client;

		if (eventClient == null)
			return;

		Log ("Event loop: registering listener");

		try
			{
			await eventClient.RegisterEventListener ().ConfigureAwait (false);
			Log ("Event loop: listener registered");

			while (!ct.IsCancellationRequested)
				{
				try
					{
					await Task.Delay (POLL_INTERVAL_MS, ct).ConfigureAwait (false);

					if (ct.IsCancellationRequested)
						break;

					IReadOnlyList<OverKizApi.Models.EventObject> events =
						await eventClient.FetchEvents ().ConfigureAwait (false);

					foreach (OverKizApi.Models.EventObject ev in events)
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

					try
						{
						await Task.Delay (RETRY_DELAY_MS, ct).ConfigureAwait (false);
						}
					catch (OperationCanceledException)
						{
						break;
						}
					}
				}
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			Log ("Event loop: fatal error – " + ex);
			}
		finally
			{
			try
				{
				await eventClient.UnregisterEventListener ().ConfigureAwait (false);
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
			? ev.Name[..^5]
			: ev.Name;

		if (!Enum.TryParse<OverKizApi.Enums.EventName> (rawName, out OverKizApi.Enums.EventName eventName))
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

		var terminal = ev.NewState is OverKizApi.Enums.ExecutionState.Completed
			or OverKizApi.Enums.ExecutionState.Failed
			or OverKizApi.Enums.ExecutionState.Cancelled;

		if (!terminal)
			return;

		IOverkizEntity entity;
		lock (_pendingExecsLock)
			{
			if (!_pendingExecs.TryGetValue (ev.ExecId, out entity))
				return;
			_ = _pendingExecs.Remove (ev.ExecId);
			}

		entity.SetMoving (false);
		}

	private void HandleDeviceAvailabilityChanged (OverKizApi.Models.EventObject ev)
		{
		if (ev.DeviceUrl == null)
			return;

		// SubType 0 = unavailable, 1 = available (Overkiz convention)
		var available = ev.SubType == 1;

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
			OverKizApi.Models.Device device = devices.FirstOrDefault (d =>
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

			var label = NormalizeLabel (device.Label ?? ev.DeviceUrl);
			Log ("Event: DeviceCreated – label=" + label + " url=" + ev.DeviceUrl);
			Log ("Event: DeviceCreated – entity created type=" + entity.GetType ().Name
				+ " controllerId=" + entity.ControllerId
				+ " uxCategory=" + entity.UxCategory
				+ " label='" + label + "'");
			PlatformManagedDevice managedDevice = new PlatformManagedDevice (
				entity.UxCategory,
				label,
				"Somfy / Overkiz",
				device.UiClass.HasValue ? device.UiClass.Value.ToString () : entity.UxCategory.ToString (),
				null);

			List<ConfigurableDriverEntity> controllers;
			Dictionary<string, PlatformManagedDevice> managedDevicesCopy;
			OverkizRoomEntity newRoomEntity = null;

			lock (_entitiesLock)
				{
				if (_entities.ContainsKey (ev.DeviceUrl))
					{
					Log ("Event: DeviceCreated – entity already existed during lock for url=" + ev.DeviceUrl);
					return;
					}
				_entities[ev.DeviceUrl] = entity;
				Log ("Event: DeviceCreated – inserted entity url=" + ev.DeviceUrl + " controllerId=" + entity.ControllerId + " entityCount=" + _entities.Count);

				if (entity is OverkizShadeEntity shade)
					{
					Log ("Event: DeviceCreated – shade pre-seed state controllerId=" + shade.ControllerId + " online=" + shade.OnlineIndicatorIsOnline + " ready=" + shade.ReadyIndicatorIsReady);
					shade.SetInitialOnlineState (true);
					Log ("Event: DeviceCreated – shade post-seed state controllerId=" + shade.ControllerId + " online=" + shade.OnlineIndicatorIsOnline + " ready=" + shade.ReadyIndicatorIsReady);
					}

				// Determine if this shade belongs to any configured room group by label.
				var roomName = FindRoomGroupForLabel (label);
				if (roomName != null)
					TrackShadeInRoom (ev.DeviceUrl, roomName);
				Log ("Event: DeviceCreated – room mapping label='" + label + "' room='" + (roomName ?? "(none)") + "'");

				controllers = [new ConfigurableDriverEntity (entity.ControllerId, (ReflectedAttributeDriverEntity)entity, null)];
				managedDevicesCopy = ManagedDevices != null
					? new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
					: [];
				managedDevicesCopy[entity.ControllerId] = managedDevice;
				Log ("Event: DeviceCreated – managedDevices staged count=" + managedDevicesCopy.Count + " containsNewChild=" + managedDevicesCopy.ContainsKey (entity.ControllerId));

				var effectiveRoom = roomName;
				// Update or create the room aggregate for the new member.
				if (effectiveRoom != null && _roomToShades.TryGetValue (effectiveRoom, out List<string> roomShades) && roomShades.Count >= 1)
					{
					var updated = RebuildRoomMembersLocked (effectiveRoom, ref managedDevicesCopy, out OverkizRoomEntity roomEntity);
					if (roomEntity != null)
						{
						newRoomEntity = updated ? null : roomEntity;
						if (!updated)
							controllers.Add (new ConfigurableDriverEntity (roomEntity.ControllerId, roomEntity, null));
						Log ("Event: DeviceCreated – room " + (updated ? "updated" : "created") + " for '" + effectiveRoom + "' (" + roomShades.Count + " shade(s))");
						}
					}
				}

			ManagedDevices = managedDevicesCopy;
			Log ("Event: DeviceCreated - publishing platform:managedDevices for " + entity.ControllerId + " count=" + ManagedDevices.Count);
			NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));

			// Register new shade and any newly created room entity.
			Log ("Event: DeviceCreated - UpdateSubControllers start for " + entity.ControllerId + " count=" + controllers.Count + " newRoomEntity=" + (newRoomEntity?.ControllerId ?? "(none)"));
			UpdateSubControllers (controllers, null);
			Log ("Event: DeviceCreated - UpdateSubControllers complete for " + entity.ControllerId);
			if (entity is OverkizShadeEntity registeredShade)
				Log ("Event: DeviceCreated - post-register shade state controllerId=" + registeredShade.ControllerId + " online=" + registeredShade.OnlineIndicatorIsOnline + " ready=" + registeredShade.ReadyIndicatorIsReady);

			Log ("Event: DeviceCreated - StartPolling for " + entity.ControllerId + " type=" + entity.GetType ().Name);
			entity.StartPolling (_workQueue);
			if (entity is OverkizShadeEntity startedShade)
				Log ("Event: DeviceCreated - post-StartPolling shade state controllerId=" + startedShade.ControllerId + " online=" + startedShade.OnlineIndicatorIsOnline + " ready=" + startedShade.ReadyIndicatorIsReady);
			newRoomEntity?.StartPolling (_workQueue);
			if (newRoomEntity != null)
				Log ("Event: DeviceCreated - room entity started controllerId=" + newRoomEntity.ControllerId + " online=" + newRoomEntity.OnlineIndicatorIsOnline + " ready=" + newRoomEntity.ReadyIndicatorIsReady);

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
		string placeOid;

		lock (_entitiesLock)
			{
			if (!_entities.TryGetValue (ev.DeviceUrl, out entity))
				return;
			_ = _entities.Remove (ev.DeviceUrl);
			managedDevicesCopy = ManagedDevices != null
				? new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
				: [];
			_ = managedDevicesCopy.Remove (entity.ControllerId);
			}

		// Untrack BEFORE reading the new count so GetRoomShadeCount reflects the removal.
		placeOid = GetShadeRoom (ev.DeviceUrl);
		UntrackShadeFromRoom (ev.DeviceUrl);

		// Update or destroy the room aggregate depending on how many shades remain.
		OverkizRoomEntity destroyedRoom = null;
		OverkizRoomEntity newlyCreatedRoom = null;
		if (placeOid != null)
			{
			var remaining = GetRoomShadeCount (placeOid);
			lock (_entitiesLock)
				{
				if (remaining >= 1)
					{
					// Update members in place; room entity identity is preserved.
					var updated = RebuildRoomMembersLocked (placeOid, ref managedDevicesCopy, out OverkizRoomEntity roomEntity);
					if (!updated && roomEntity != null)
						newlyCreatedRoom = roomEntity;
					Log ("Event: DeviceDeleted – room " + (updated ? "updated" : "created") + " for '" + placeOid + "' (" + remaining + " shade(s) remaining)");
					}
				else
					{
					(OverkizRoomEntity old, Dictionary<string, PlatformManagedDevice> afterDestroy) = DestroyRoomEntityLocked (placeOid, managedDevicesCopy);
					if (old != null)
						{
						managedDevicesCopy = afterDestroy;
						destroyedRoom = old;
						Log ("Event: DeviceDeleted – room entity removed for '" + placeOid + "' (no shades remaining)");
						}
					}
				}
			}

		// Cancel any in-flight executions for this entity
		lock (_pendingExecsLock)
			{
			foreach (var execId in _pendingExecs.Keys
				.Where (k => ReferenceEquals (_pendingExecs[k], entity))
				.ToList ())
				{
				_ = _pendingExecs.Remove (execId);
				}
			}

		entity.StopPolling ();
		destroyedRoom?.StopPolling ();

		// Deregister the shade (and destroyed room if it had no members left).
		var toRemove = new List<string> { entity.ControllerId };
		if (destroyedRoom != null)
			toRemove.Add (destroyedRoom.ControllerId);
		UpdateSubControllers (null, [.. toRemove]);

		// Register a newly created room (only happens if it didn't exist before deletion).
		if (newlyCreatedRoom != null)
			{
			UpdateSubControllers ([new ConfigurableDriverEntity (newlyCreatedRoom.ControllerId, newlyCreatedRoom, null)], null);
			newlyCreatedRoom.StartPolling (_workQueue);
			}

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

		var newLabel = NormalizeLabel (ev.Label);

		IOverkizEntity entity;
		lock (_entitiesLock)
			{
			if (!_entities.TryGetValue (ev.DeviceUrl, out entity))
				return;
			}

		if (ManagedDevices == null || !ManagedDevices.TryGetValue (entity.ControllerId, out PlatformManagedDevice existing))
			return;

		// Determine old and new room membership based on configured groups.
		var oldRoom = GetShadeRoom (ev.DeviceUrl);          // room tracked under old label
		var newRoom = FindRoomGroupForLabel (newLabel);     // room the new label maps to

		var roomChanged = !string.Equals (oldRoom, newRoom, StringComparison.OrdinalIgnoreCase);

		// Update the shade entity's label first so CreateRoomEntityLocked sees the new label.
		entity.UpdateLabel (newLabel);

		// Update the ManagedDevices entry for the shade itself.
		var managedDevicesCopy = new Dictionary<string, PlatformManagedDevice> (ManagedDevices)
			{
			[entity.ControllerId] = new PlatformManagedDevice (
				existing.UxCategory, newLabel, existing.Manufacturer, existing.Model, null)
			};

		if (roomChanged)
			{
			// Leave old room (if any).
			OverkizRoomEntity oldRoomDestroyed = null;
			if (oldRoom != null)
				{
				UntrackShadeFromRoom (ev.DeviceUrl);
				var remaining = GetRoomShadeCount (oldRoom);
				lock (_entitiesLock)
					{
					if (remaining >= 1)
						{
						_ = RebuildRoomMembersLocked (oldRoom, ref managedDevicesCopy, out _);
						}
					else
						{
						(OverkizRoomEntity old, Dictionary<string, PlatformManagedDevice> afterDestroy) = DestroyRoomEntityLocked (oldRoom, managedDevicesCopy);
						if (old != null)
							{
							managedDevicesCopy = afterDestroy;
							oldRoomDestroyed = old;
							}
						}
					}
				}

			// Join new room (if any).
			OverkizRoomEntity newRoomCreated = null;
			if (newRoom != null)
				{
				TrackShadeInRoom (ev.DeviceUrl, newRoom);
				lock (_entitiesLock)
					{
					var updated = RebuildRoomMembersLocked (newRoom, ref managedDevicesCopy, out OverkizRoomEntity roomEntity);
					if (!updated && roomEntity != null)
						newRoomCreated = roomEntity;
					}
				}

			// Deregister old room if it was destroyed (last shade left).
			oldRoomDestroyed?.StopPolling ();
			if (oldRoomDestroyed != null)
				UpdateSubControllers (null, [oldRoomDestroyed.ControllerId]);

			// Register new room if it was just created.
			if (newRoomCreated != null)
				{
				UpdateSubControllers ([new ConfigurableDriverEntity (newRoomCreated.ControllerId, newRoomCreated, null)], null);
				newRoomCreated.StartPolling (_workQueue);
				}

			Log ("Event: DeviceUpdated – relabelled " + entity.ControllerId + " to '" + newLabel + "'"
				+ " oldRoom=" + (oldRoom ?? "(none)") + " newRoom=" + (newRoom ?? "(none)"));
			}
		else if (oldRoom != null)
			{
			// Label changed but stays in the same room — update member label in place.
			lock (_entitiesLock)
				_ = RebuildRoomMembersLocked (oldRoom, ref managedDevicesCopy, out _);

			Log ("Event: DeviceUpdated – relabelled " + entity.ControllerId + " to '" + newLabel + "' (room member label refreshed)");
			}
		else
			{
			Log ("Event: DeviceUpdated – relabelled " + entity.ControllerId + " to '" + newLabel + "'");
			}

		ManagedDevices = managedDevicesCopy;
		NotifyPropertyChanged ("platform:managedDevices", CreateValueForEntries (ManagedDevices));
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
		var isShade = (device.UiClass.HasValue && _shadeClasses.Contains (device.UiClass.Value))
			|| url.StartsWith ("rts://", StringComparison.OrdinalIgnoreCase);

		if (isShade)
			{
			var safeId = MakeSafeControllerId (url);
			var isOneWay = url.StartsWith ("rts://", StringComparison.OrdinalIgnoreCase)
					|| device.Protocol == Protocol.Rts;
			var hasMyCommand = device.Definition?.Commands
				.Any (c => string.Equals (c.CommandName, "my", StringComparison.OrdinalIgnoreCase))
				?? false;
			var apiLabel = NormalizeLabel (device.Label) ?? string.Empty;
			_ = _shadeDisplayNames.TryGetValue (apiLabel, out string displayName);

			var entity = new OverkizShadeEntity (
				controllerId: safeId,
				deviceUrl: url,
				deviceLabel: apiLabel,
				displayName: displayName,
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
				+ " commands=[" + string.Join (",", device.Definition?.Commands?.Select (c => c.CommandName) ?? []) + "]");
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
					var execId = await client.ExecuteDeviceAction (deviceUrl, cmds).ConfigureAwait (false);

					// Track the execution so we can set isMoving=false when it completes.
					IOverkizEntity entity;
					lock (_entitiesLock)
						_ = _entities.TryGetValue (deviceUrl, out entity);
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
		foreach (var c in url)
			_ = chars.Append (char.IsLetterOrDigit (c) ? c : '_');
		return chars.ToString ();
		}

	private void Log (string message) => _logger?.Log (_args.DriverId, LogEntryLevel.Info, message);
	}

