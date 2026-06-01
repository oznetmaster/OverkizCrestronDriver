// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using OverKizApi;
using OverKizApi.Models;

namespace OverKiz.CrestronDriver;

/// <summary>
/// SDK V2 extension entity representing a single Overkiz controllable shade,
/// exposed to Crestron Home as a Shade device type via the extension UI mechanism.
/// </summary>
internal class OverkizShadeEntity : ReflectedAttributeDriverEntity, IOverkizEntity
	{
	internal const string STATE_CLOSURE = "core:ClosureState";
	internal const string STATE_MOVING = "core:MovingState";

	private readonly Action<string> _sendCommand;
	private readonly Action<string, object[]> _sendCommandWithParams;
	private readonly string _deviceUrl;
	private readonly DriverControllerLogger _logger;
	private readonly UiDefinitionProperty _uiDefinition;

	internal bool IsOneWay
		{
		get;
		}

	internal bool HasMyCommand
		{
		get;
		}

	private string _deviceLabel;

	private void Log (string msg) =>
		_logger?.Log (ControllerId, LogEntryLevel.Info, msg);

	private void LogError (string msg) =>
		_logger?.Log (ControllerId, LogEntryLevel.Error, msg);

	[Conditional ("DEBUG")]
	private void DebugLog (string msg) =>
		Log (msg);

	private void TraceNotify (string propertyName, DriverEntityValue value)
		{
		DebugLog ("NOTIFY " + propertyName + " -> " + value);
		NotifyPropertyChanged (propertyName, value);
		}

	private void TraceUiDefinitionNotification (string source)
		{
		if (_uiDefinition == null)
			{
			DebugLog (source + ": uiDefinition unavailable");
			return;
			}

		DebugLog (source + ": reading uiDefinition value");
		DriverEntityValue? uiValue = _uiDefinition.GetValue (null, null);
		DebugLog (source + ": uiDefinition hasValue=" + uiValue.HasValue);
		if (uiValue.HasValue)
			TraceNotify (UiDefinitionProperty.Name, uiValue.Value);
		}

	private BoundItem<CommandInstance> TraceGetCommand (string commandName)
		{
		DebugLog ("GetCommand requested: commandName='" + commandName + "'");
		return GetCommand (commandName);
		}

	private void LogDeclaredEntitySurface ()
		{
		try
			{
			var propertyEntries = new List<string> ();
			foreach (PropertyInfo prop in GetType ().GetProperties (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
				EntityPropertyAttribute attr = prop.GetCustomAttribute<EntityPropertyAttribute> ();
				if (attr != null)
					propertyEntries.Add ((attr.Id ?? prop.Name) + "<=" + prop.Name);
				}

			var commandEntries = new List<string> ();
			foreach (MethodInfo method in GetType ().GetMethods (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
				EntityCommandAttribute attr = method.GetCustomAttribute<EntityCommandAttribute> ();
				if (attr != null)
					commandEntries.Add ((attr.Id ?? method.Name) + "<=" + method.Name);
				}

			DebugLog ("EntitySurface properties(" + propertyEntries.Count + "): " + string.Join (", ", propertyEntries));
			DebugLog ("EntitySurface commands(" + commandEntries.Count + "): " + string.Join (", ", commandEntries));
			}
		catch (Exception ex)
			{
			DebugLog ("EntitySurface logging failed: " + ex.Message);
			}
		}

	// ── IOverkizEntity ────────────────────────────────────────────────────

	public DeviceUxCategory UxCategory => DeviceUxCategory.Shade;

	// ── Online indicator ─────────────────────────────────────────

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set;
		}

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set;
		}

	public void SetOnline (bool online)
		{
		Log ("SetOnline(" + online + ") called; current online=" + OnlineIndicatorIsOnline + " ready=" + ReadyIndicatorIsReady);

		bool changed = false;

		if (OnlineIndicatorIsOnline != online)
			{
			OnlineIndicatorIsOnline = online;
			TraceNotify ("onlineIndicator:isOnline", new DriverEntityValue (online));
			Log ("SetOnline: onlineIndicator:isOnline -> " + online);
			changed = true;
			}

		if (ReadyIndicatorIsReady != online)
			{
			ReadyIndicatorIsReady = online;
			TraceNotify ("readyIndicator:isReady", new DriverEntityValue (online));
			Log ("SetOnline: readyIndicator:isReady -> " + online);
			changed = true;
			}

		if (!changed)
			Log ("SetOnline: no change needed");
		}

	/// <summary>
	/// Sets the initial online/ready state BEFORE UpdateSubControllers() registration.
	/// Does NOT call NotifyPropertyChanged() — this just makes the property values
	/// correct for the framework's initial GetState() snapshot at registration time.
	/// </summary>
	internal void SetInitialOnlineState (bool online)
		{
		ReadyIndicatorIsReady = online;
		OnlineIndicatorIsOnline = online;
		Log ("SetInitialOnlineState: ready=" + online + " online=" + online + " (no notifications)");
		}

	/// <summary>
	/// Called by the platform driver when a <c>DeviceAvailabilityChanged</c> event is received.
	/// No-op for one-way (RTS) devices — the gateway has no feedback channel to determine their availability.
	/// </summary>
	public void UpdateAvailability (bool available)
		{
		if (IsOneWay)
			return;
		SetOnline (available);
		}

	/// <summary>
	/// Push initial ready/online state to the framework immediately after UpdateSubControllers.
	/// This is needed because ValuesChanged may not fire for dynamically-discovered entities
	/// until they're reloaded from persisted state.
	/// NOTE: We do NOT set _frameworkReady here - that must wait for ValuesChanged to fire.
	/// </summary>
	internal void PushInitialState ()
		{
		Log ("PushInitialState: pushing ready=" + ReadyIndicatorIsReady + " online=" + OnlineIndicatorIsOnline);

		// Push all initial properties WITHOUT setting _frameworkReady
		// This allows ValuesChanged to fire later and complete the initialization
		TraceNotify ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady));
		TraceNotify ("onlineIndicator:isOnline", new DriverEntityValue (OnlineIndicatorIsOnline));
		TraceNotify ("deviceLabel", new DriverEntityValue (DeviceLabel));
		TraceNotify ("hasMy", new DriverEntityValue (HasMy));
		TraceNotify ("isTwoWay", new DriverEntityValue (IsTwoWay));
		TraceUiDefinitionNotification ("PushInitialState");

		Log ("PushInitialState: notifications sent");
		}

	internal void ForcePublishOnlineReadyTrue ()
		{
		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		TraceNotify ("onlineIndicator:isOnline", new DriverEntityValue (true));
		TraceNotify ("readyIndicator:isReady", new DriverEntityValue (true));
		Log ("ForcePublishOnlineReadyTrue: published online=true ready=true");
		}

	internal void ForceOnlineReadyEdge ()
		{
		SetOnline (false);
		SetOnline (true);
		Log ("ForceOnlineReadyEdge: published false->true edge");
		}

	/// <inheritdoc/>
	public void SetMoving (bool moving)
		{
		// Two-way devices get isMoving from core:MovingState via ApplyEventStates.
		// For one-way (RTS) devices the gateway never sends state feedback, so we
		// drive isMoving directly from the execution lifecycle.
		if (!IsOneWay)
			return;
		IsMoving = moving;
		}

	/// <summary>Stored display-name override (from <c>ShadeDisplayNames</c> config); null means use <see cref="ApiLabel"/>.</summary>
	private string _displayNameOverride;

	/// <inheritdoc/>
	public void UpdateLabel (string newApiLabel)
		{
		var label = newApiLabel?.Trim () ?? string.Empty;
		if (string.Equals (ApiLabel, label, StringComparison.Ordinal))
			return;

		ApiLabel = label;
		_deviceLabel = _displayNameOverride ?? ApiLabel;

		TraceNotify ("deviceLabel", new DriverEntityValue (_deviceLabel));
		Log ("UpdateLabel: apiLabel='" + ApiLabel + "' deviceLabel='" + _deviceLabel + "'");
		}

	/// <summary>Updates the display-name override and refreshes <see cref="DeviceLabel"/>.</summary>
	public void UpdateDisplayName (string displayName)
		{
		_displayNameOverride = !string.IsNullOrEmpty (displayName) ? displayName : null;
		_deviceLabel = _displayNameOverride ?? ApiLabel;

		TraceNotify ("deviceLabel", new DriverEntityValue (_deviceLabel));
		Log ("UpdateDisplayName: apiLabel='" + ApiLabel + "' deviceLabel='" + _deviceLabel + "'");
		}

	/// <inheritdoc/>
	public void ApplyEventStates (IReadOnlyList<EventState> states)
		{
		if (IsOneWay || states == null || states.Count == 0)
			return;

		var closure = OpenPercent == 0 ? 100 : 100 - OpenPercent; // preserve current if not in event
		var isMoving = IsMoving;
		var got = false;

		foreach (EventState s in states)
			{
			if (string.Equals (s.Name, STATE_CLOSURE, StringComparison.OrdinalIgnoreCase) && s.Value != null)
				{
				if (int.TryParse (s.Value.ToString (), out var v))
					{
					closure = v;
					got = true;
					}
				}
			else if (string.Equals (s.Name, STATE_MOVING, StringComparison.OrdinalIgnoreCase) && s.Value != null)
				{
				isMoving = string.Equals (s.Value.ToString (), "moving", StringComparison.OrdinalIgnoreCase);
				got = true;
				}
			}

		if (got)
			UpdateState (closure, isMoving);
		}

	// ── Polling ──────────────────────────────────────────────────────

	// One-way (RTS) devices have no feedback; polling is skipped.
	private const int POLL_INTERVAL_MS = 30_000;

	private Timer _pollTimer;
	private OverkizWorkQueue _queue;

	private static int _startPollingCallCount = 0;

	public void StartPolling (OverkizWorkQueue queue)
		{
		int callNumber = System.Threading.Interlocked.Increment (ref _startPollingCallCount);
		try
			{
			Log (">>> StartPolling ENTRY #" + callNumber + "; this=" + this.GetType().FullName + " controllerId=" + ControllerId);
			Log (">>> StartPolling ENTRY; isOneWay=" + IsOneWay);
			Log (">>> StartPolling PRE-STATE online=" + OnlineIndicatorIsOnline + " ready=" + ReadyIndicatorIsReady);
			if (!IsOneWay)
				{
				_queue = queue;
				StopPolling ();
				_pollTimer = new Timer (PollCallback, null, POLL_INTERVAL_MS, POLL_INTERVAL_MS);
				}

			Log (">>> StartPolling: calling SetOnline(true)");
			SetOnline (true);
			Log (">>> StartPolling POST-STATE online=" + OnlineIndicatorIsOnline + " ready=" + ReadyIndicatorIsReady);
			Log (">>> StartPolling EXIT #" + callNumber);
			}
		catch (Exception ex)
			{
			Log (">>> StartPolling EXCEPTION #" + callNumber + ": " + ex.ToString ());
			throw;
			}
		}

	public void StopPolling ()
		{
		Log ("StopPolling called; pre-state online=" + OnlineIndicatorIsOnline + " ready=" + ReadyIndicatorIsReady + " timerExists=" + (_pollTimer != null));
		Timer t = Interlocked.Exchange (ref _pollTimer, null);
		_ = (t?.Change (Timeout.Infinite, Timeout.Infinite));
		t?.Dispose ();
		SetOnline (false);
		Log ("StopPolling complete; post-state online=" + OnlineIndicatorIsOnline + " ready=" + ReadyIndicatorIsReady);
		}

	private void PollCallback (object _) =>
		_ = _queue?.EnqueueAsync (PollAsync);

	// ── Extension UI properties ───────────────────────────────────────────

	/// <summary>
	/// Open percentage: 0 = fully closed, 100 = fully open.
	/// Exposed as an extension UI property so the Crestron Home tile can show
	/// and control the shade position.
	/// </summary>
	[EntityProperty (Id = "openPercent", FriendlyName = "Open %", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent
		{
		get;
		private set
			{
			if (field == value)
				return;
			field = value;
			TraceNotify ("openPercent", new DriverEntityValue (value));
			}
		} = 100;

	/// <summary>The raw Overkiz API label for this device — used for room-group matching and identity. Never overridden by display-name config.</summary>
	internal string ApiLabel { get; private set; }

	/// <summary>Human-readable label of the device from the Overkiz API (with optional display-name override from ShadeDisplayNames config).</summary>
	[EntityProperty (Id = "deviceLabel", FriendlyName = "Device Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string DeviceLabel => _deviceLabel;

	/// <summary>
	/// True when the shade supports position feedback (two-way).
	/// Used by the UI definition to show/hide the position slider.
	/// </summary>
	[EntityProperty (Id = "isTwoWay", FriendlyName = "Two-Way")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool IsTwoWay => !IsOneWay;

	/// <summary>
	/// True when the Overkiz device exposes a "my" command (favourite position).
	/// Used by the UI definition to show/hide the My button.
	/// </summary>
	[EntityProperty (Id = "hasMy", FriendlyName = "Has My")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasMy => HasMyCommand;

	[EntityProperty (Id = "isMoving", FriendlyName = "Is Moving")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool IsMoving
		{
		get;
		private set
			{
			if (field == value)
				return;
			field = value;
			TraceNotify ("isMoving", new DriverEntityValue (value));
			}
		}

	// ── Extension UI commands ─────────────────────────────────────────────

	[EntityCommand (Id = "open", FriendlyName = "Open")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open ()
		{
		DebugLog ("COMMAND open invoked");
		_sendCommand ("open");
		}

	[EntityCommand (Id = "close", FriendlyName = "Close")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close ()
		{
		DebugLog ("COMMAND close invoked");
		_sendCommand ("close");
		}

	[EntityCommand (Id = "stop", FriendlyName = "Stop")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop ()
		{
		DebugLog ("COMMAND stop invoked");
		_sendCommand ("stop");
		}

	public void My ()
		{
		DebugLog ("COMMAND my invoked");
		_sendCommand ("my");
		}

	public void SetOpenPercent (
		[EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value)
		{
		DebugLog ("COMMAND setOpenPercent invoked value=" + value);
		// Overkiz closure is the inverse of open: 0 = open, 100 = closed.
		var closure = 100 - Math.Max (0, Math.Min (100, value));
		_sendCommandWithParams ("setClosure", [closure]);
		}

	// ── Constructor ───────────────────────────────────────────────────────

	public OverkizShadeEntity (
		string controllerId,
		string deviceUrl,
		string deviceLabel,
		string displayName,
		bool isOneWay,
		bool hasMyCommand,
		Action<string> sendCommand,
		Action<string, object[]> sendCommandWithParams,
		string driverDataDirectoryPath,
		DriverControllerLogger logger,
		DriverImplementationResources resources)
		: base (controllerId)
		{
		_deviceUrl = deviceUrl ?? throw new ArgumentNullException (nameof (deviceUrl));
		ApiLabel            = deviceLabel ?? string.Empty;
		_displayNameOverride = !string.IsNullOrEmpty (displayName) ? displayName : null;
		_deviceLabel        = _displayNameOverride ?? ApiLabel;
		IsOneWay = isOneWay;
		HasMyCommand = hasMyCommand;
		_sendCommand = sendCommand ?? throw new ArgumentNullException (nameof (sendCommand));
		_sendCommandWithParams = sendCommandWithParams ?? throw new ArgumentNullException (nameof (sendCommandWithParams));
		_logger = logger;
		var programmableMeta = new DriverEntityCommandMetadata (true, false);
		var noName = new DriverEntityLocalizedString (null, null);
		var noResult = new DriverEntityCommandResult (false, null);

		Log ("Constructed: controllerId=" + controllerId + " isOneWay=" + isOneWay);

		// Load the shared UI definition
		Log ("UiDefinition: driverDataDirectoryPath=" + driverDataDirectoryPath);
		var uiDir = Path.Combine (driverDataDirectoryPath, "uidefinitions");
		Log ("UiDefinition: looking in " + uiDir + " exists=" + Directory.Exists (uiDir));
		_uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists (
			driverDataDirectoryPath,
			resources.InitLogger,
			LogEntryLevel.Error);
		Log ("UiDefinition: loaded=" + (_uiDefinition != null));
		try
			{
			Log (">>> About to AddProperty UiDefinition");
			AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
			Log (">>> AddProperty UiDefinition succeeded");
			}
		catch (Exception ex)
			{
			LogError (">>> AddProperty UiDefinition FAILED: " + ex.Message);
			}

		// Wire up the extension command executors so that the Crestron Home extension
		// UI can invoke commands and set property values on this entity.
		try
			{
			Log (">>> About to create ExtensionDoCommandExecutor");
			if (HasMyCommand)
				{
				var myDef = new DriverEntityCommandDefinition (null, null, null, new DriverEntityLocalizedString ("My", null));
				AddCommand (this, "my", new DelegateCommandInstance (
					"my",
					myDef,
					programmableMeta,
					(id, inst, args, lookup, cb) =>
						{
						My ();
						cb?.Invoke (noResult);
						},
					null));
				}

			if (!IsOneWay)
				{
				var intRange = new DriverEntityValueRange (0.0, 100.0, null);
				var intType = new DriverEntityTypeDefinition (DriverEntityValueType.Integer, DriverEntityValueType.Uninitialized, null, intRange, null, null, null);
				var pctParamDef = new DriverEntityParameterDefinition (noName, null, intType, null, null, null, null, false, false, null);
				var pctParams = new Dictionary<string, DriverEntityParameterDefinition> { ["value"] = pctParamDef };
				var pctCmdDef = new DriverEntityCommandDefinition (pctParams, intType, null, new DriverEntityLocalizedString ("Set Open %", null));
				AddCommand (this, "setOpenPercent", new DelegateCommandInstance (
					"setOpenPercent",
					pctCmdDef,
					programmableMeta,
					(id, inst, args, lookup, cb) =>
						{
						if (args != null && args.TryGetValue ("value", out DriverEntityValue pv))
							{
							_ = pv.TryGetValue (out int pct);
							SetOpenPercent (pct);
							}

						cb?.Invoke (noResult);
						},
					["value"]));
				}

			var doCommand = new ExtensionDoCommandExecutor (TraceGetCommand, resources.Logger);
			AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);
			Log (">>> ExtensionDoCommandExecutor succeeded");
			}
		catch (Exception ex)
			{
			LogError (">>> ExtensionDoCommandExecutor FAILED: " + ex.Message);
			}

		try
			{
			Log (">>> About to create ExtensionSetPropertyValueExecutor");
			var setPropertyValue = new ExtensionSetPropertyValueExecutor (TraceGetCommand, resources.Logger);
			AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);
			Log (">>> ExtensionSetPropertyValueExecutor succeeded");
			}
		catch (Exception ex)
			{
			LogError (">>> ExtensionSetPropertyValueExecutor FAILED: " + ex.Message);
			}

		LogDeclaredEntitySurface ();

		// Both indicators start false; they will be set true BEFORE UpdateSubControllers()
		// via SetInitialOnlineState() so the framework's initial GetState() sees them as ready.
		ReadyIndicatorIsReady = false;
		OnlineIndicatorIsOnline = false;
		}

	// ── State update ──────────────────────────────────────────────────────

	internal void UpdateState (int closurePercent, bool isMoving)
		{
		if (IsOneWay)
			return;

		// Convert Overkiz closure (0=open, 100=closed) to open percent.
		OpenPercent = 100 - Math.Max (0, Math.Min (100, closurePercent));
		IsMoving = isMoving;
		}

	// ── Private: poll ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

	private async Task PollAsync (OverkizClient client)
		{
		var deviceUrl = _deviceUrl;
		IReadOnlyList<State> states = await client.GetDeviceStates (deviceUrl).ConfigureAwait (false);

		var closure = 0;
		var isMoving = false;

		foreach (State state in states)
			{
			if (state.Name == STATE_CLOSURE && state.Value != null)
				_ = int.TryParse (state.Value.ToString (), out closure);

			if (state.Name == STATE_MOVING && state.Value != null)
				{
				isMoving = string.Equals (state.Value.ToString (), "moving",
					StringComparison.OrdinalIgnoreCase);
				}
			}

		UpdateState (closure, isMoving);
		}
	}

