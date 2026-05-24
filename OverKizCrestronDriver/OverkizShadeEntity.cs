// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
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

	// Set once the framework has commissioned the entity
	// a framework thread after commissioning completes).
	private int _frameworkReady;

	private readonly string _logControllerId;

	private void Log (string msg) =>
		_logger?.Log (_logControllerId, LogEntryLevel.Info, "[Shade:" + ControllerId + "] " + msg);

	// ── IOverkizEntity ────────────────────────────────────────────────────

	public DeviceUxCategory UxCategory => DeviceUxCategory.Shade;

	// ── Online indicator ─────────────────────────────────────────

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

	public void SetOnline (bool online)
		{
		Log ("SetOnline(" + online + ") called; current=" + OnlineIndicatorIsOnline + " frameworkReady=" + _frameworkReady);
		if (OnlineIndicatorIsOnline == online)
			return;
		OnlineIndicatorIsOnline = online;
		NotifyPropertyChanged ("onlineIndicator:isOnline", new DriverEntityValue (online));
		Log ("SetOnline: NotifyPropertyChanged sent");
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

	/// <inheritdoc/>
	public void UpdateLabel (string newLabel)
		{
		var label = newLabel?.Trim () ?? string.Empty;
		if (string.Equals (_deviceLabel, label, StringComparison.Ordinal))
			return;

		_deviceLabel = label;

		// Only notify if the framework has commissioned the entity.
		if (_frameworkReady != 0)
			NotifyPropertyChanged ("deviceLabel", new DriverEntityValue (_deviceLabel));

		Log ("UpdateLabel: label updated to '" + _deviceLabel + "'");
		}

	/// <inheritdoc/>
	public void ApplyEventStates (IReadOnlyList<EventState> states)
		{
		if (IsOneWay || states == null || states.Count == 0)
			return;

		int closure = OpenPercent == 0 ? 100 : 100 - OpenPercent; // preserve current if not in event
		bool isMoving = IsMoving;
		bool got = false;

		foreach (EventState s in states)
			{
			if (string.Equals (s.Name, STATE_CLOSURE, StringComparison.OrdinalIgnoreCase) && s.Value != null)
				{
				if (int.TryParse (s.Value.ToString (), out int v))
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

	public void StartPolling (OverkizWorkQueue queue)
		{
		Log ("StartPolling called; isOneWay=" + IsOneWay + " frameworkReady=" + _frameworkReady);
		if (!IsOneWay)
			{
			_queue = queue;
			StopPolling ();
			_pollTimer = new Timer (PollCallback, null, POLL_INTERVAL_MS, POLL_INTERVAL_MS);
			}

		SetOnline (true);
		}

	public void StopPolling ()
		{
		Log ("StopPolling called");
		Timer t = Interlocked.Exchange (ref _pollTimer, null);
		_ = (t?.Change (Timeout.Infinite, Timeout.Infinite));
		t?.Dispose ();
		SetOnline (false);
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
			NotifyPropertyChanged ("openPercent", new DriverEntityValue (value));
			}
		} = 100;

	/// <summary>Human-readable label of the device as named by the user in the Overkiz app.</summary>
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
			NotifyPropertyChanged ("isMoving", new DriverEntityValue (value));
			}
		}

	// ── Extension UI commands ─────────────────────────────────────────────

	[EntityCommand (Id = "open", FriendlyName = "Open")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open () => _sendCommand ("open");

	[EntityCommand (Id = "close", FriendlyName = "Close")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close () => _sendCommand ("close");

	[EntityCommand (Id = "stop", FriendlyName = "Stop")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop () => _sendCommand ("stop");

	[EntityCommand (Id = "my", FriendlyName = "My")]
	[EntityCommandMetadata (Programmable = true)]
	public void My () => _sendCommand ("my");

	[EntityCommand (Id = "setOpenPercent", FriendlyName = "Set Open %")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent (
		[EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value)
		{
		// Overkiz closure is the inverse of open: 0 = open, 100 = closed.
		var closure = 100 - Math.Max (0, Math.Min (100, value));
		_sendCommandWithParams ("setClosure", [closure]);
		}

	// ── Constructor ───────────────────────────────────────────────────────

	public OverkizShadeEntity (
		string controllerId,
		string logControllerId,
		string deviceUrl,
		string deviceLabel,
		bool isOneWay,
		bool hasMyCommand,
		Action<string> sendCommand,
		Action<string, object[]> sendCommandWithParams,
		string driverDataDirectoryPath,
		DriverControllerLogger logger,
		DriverImplementationResources resources)
		: base (controllerId)
		{
		_logControllerId = logControllerId;
		_deviceUrl = deviceUrl ?? throw new ArgumentNullException (nameof (deviceUrl));
		_deviceLabel = deviceLabel ?? string.Empty;
		IsOneWay = isOneWay;
		HasMyCommand = hasMyCommand;
		_sendCommand = sendCommand ?? throw new ArgumentNullException (nameof (sendCommand));
		_sendCommandWithParams = sendCommandWithParams ?? throw new ArgumentNullException (nameof (sendCommandWithParams));
		_logger = logger;

		Log ("Constructed: controllerId=" + controllerId + " isOneWay=" + isOneWay);

		// Load the shared UI definition
		Log ("UiDefinition: driverDataDirectoryPath=" + driverDataDirectoryPath);
		var uiDir = System.IO.Path.Combine (driverDataDirectoryPath, "uidefinitions");
		Log ("UiDefinition: looking in " + uiDir + " exists=" + System.IO.Directory.Exists (uiDir));
		_uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists (
			driverDataDirectoryPath,
			resources.InitLogger,
			LogEntryLevel.Error);
		Log ("UiDefinition: loaded=" + (_uiDefinition != null));
		AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);

		// Wire up the extension command executors so that the Crestron Home extension
		// UI can invoke commands and set property values on this entity.
		var doCommand = new ExtensionDoCommandExecutor (GetCommand, resources.Logger);
		AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);

		var setPropertyValue = new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger);
		AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);

		// Child is ready the moment it is constructed — it will go online when
		// the platform's gateway connection is established.
		// NotifyPropertyChanged is not reliable before the framework has commissioned
		// the entity.  ValuesChanged fires on a framework-owned CSTP thread after
		// commissioning, so we use it as the trigger to push the initial state.
		ReadyIndicatorIsReady = true;
		ValuesChanged += OnFirstValuesChanged;
		}

	// ── State update ──────────────────────────────────────────────────────

	private void OnFirstValuesChanged (object sender, DevicePropertyChangeEventArgs args)
		{
		Log ("OnFirstValuesChanged fired; frameworkReady=" + _frameworkReady
			+ " ready=" + ReadyIndicatorIsReady + " online=" + OnlineIndicatorIsOnline);

		// Run only once: atomically flip 0 → 1.
		if (Interlocked.CompareExchange (ref _frameworkReady, 1, 0) != 0)
			{
			Log ("OnFirstValuesChanged: already fired, skipping");
			return;
			}

		ValuesChanged -= OnFirstValuesChanged;

		// Now on a framework CSTP thread — safe to notify.
		if (_uiDefinition != null)
			{
			DriverEntityValue? uiValue = _uiDefinition.GetValue (null, null);
			Log ("OnFirstValuesChanged: uiValue.HasValue=" + uiValue.HasValue);
			if (uiValue.HasValue)
				{
				NotifyPropertyChanged (UiDefinitionProperty.Name, uiValue.Value);
				Log ("OnFirstValuesChanged: UiDefinition NotifyPropertyChanged sent");
				}
			}
		else
			{
			Log ("OnFirstValuesChanged: _uiDefinition is null — UI definition not loaded");
			}

		NotifyPropertyChanged ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady));
		NotifyPropertyChanged ("onlineIndicator:isOnline", new DriverEntityValue (OnlineIndicatorIsOnline));
		NotifyPropertyChanged ("deviceLabel", new DriverEntityValue (_deviceLabel));
		NotifyPropertyChanged ("hasMy", new DriverEntityValue (HasMy));
		NotifyPropertyChanged ("isTwoWay", new DriverEntityValue (IsTwoWay));
		Log ("OnFirstValuesChanged: ready+online+hasMy(" + HasMy + ")+isTwoWay(" + IsTwoWay + ") notifications sent");
		}

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

		int closure = 0;
		bool isMoving = false;

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

