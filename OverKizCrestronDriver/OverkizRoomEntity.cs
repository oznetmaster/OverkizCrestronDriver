// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace OverKiz.CrestronDriver;

/// <summary>
/// Describes a single shade member of a room group.
/// </summary>
internal sealed class RoomMember (
	string label,
	string displayName,
	bool isTwoWay,
	bool hasMy,
	Action open,
	Action close,
	Action stop,
	Action my,
	Action<int> setOpenPercent,
	Func<int> getOpenPercent)
	{
	/// <summary>Overkiz API label — used for identity and slot matching.</summary>
	public string Label
		{
		get;
		} = label ?? string.Empty;

	/// <summary>Display name shown in the room subheading (falls back to <see cref="Label"/> when not specified).</summary>
	public string DisplayName
		{
		get;
		} = string.IsNullOrEmpty (displayName) ? (label ?? string.Empty) : displayName;
	public bool IsTwoWay
		{
		get;
		} = isTwoWay;
	public bool HasMy
		{
		get;
		} = hasMy;
	public Action Open
		{
		get;
		} = open;
	public Action Close
		{
		get;
		} = close;
	public Action Stop
		{
		get;
		} = stop;
	public Action My
		{
		get;
		} = my;
	public Action<int> SetOpenPercent
		{
		get;
		} = setOpenPercent;

	/// <summary>Returns the current open-percent (0–100) for this shade, used for the position slider.</summary>
	public Func<int> GetOpenPercent
		{
		get;
		} = getOpenPercent;
	}

/// <summary>
/// SDK V2 Room aggregate entity.
/// Represents a configured room group that contains multiple shades and provides
/// both aggregate room commands and individual shade sub-pages in the extension UI.
/// </summary>
internal class OverkizRoomEntity : ReflectedAttributeDriverEntity, IOverkizEntity
	{
	private readonly Action _openAll;
	private readonly Action _closeAll;
	private readonly Action _stopAll;
	private readonly Action _myAll;
	private readonly Action<int> _setOpenPercentAll;
	private IReadOnlyList<RoomMember> _members;
	private readonly DriverControllerLogger _logger;
	private readonly string _driverDataDirectoryPath;

	// Maximum number of shade slots supported by the static UI definition.
	private const int MAX_SLOTS = 10;

	// The configured slot list for this room (from RoomGroups config).
	// May have fewer than MaxSlots entries; extra slots stay hidden.
	// ApiLabel is used for matching; DisplayName is shown in the subheading.
	private readonly IReadOnlyList<RoomMemberConfig> _slotConfigs;

	// ── Dynamic per-member property values ───────────────────────────────
	// "openPercent_N"   → current open percent (int)
	// "shadeLabel_N"    → shade label string
	// "visible_N"       → bool: whether slot N is currently present
	// "visible_slider_N"→ bool: present AND two-way
	private readonly Dictionary<string, DriverEntityValue> _memberProps = [];

	// Set once the framework has commissioned the entity.
	private int _frameworkReady;

	private readonly UiDefinitionProperty _uiDefinition;

	private void Log (string msg) =>
		_logger?.Log (ControllerId, LogEntryLevel.Info, msg);

	// ── IOverkizEntity ────────────────────────────────────────────────────

	public DeviceUxCategory UxCategory => DeviceUxCategory.Room;

	// ── Online / ready ────────────────────────────────────────────────────

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
		if (OnlineIndicatorIsOnline == online)
			return;
		OnlineIndicatorIsOnline = online;
		NotifyPropertyChanged ("onlineIndicator:isOnline", new DriverEntityValue (online));
		}

	public void UpdateAvailability (bool available) => SetOnline (available);

	// ── IOverkizEntity (no-op overrides for shade-specific members) ───────

	public void SetMoving (bool moving)
		{
		}

	public void ApplyEventStates (IReadOnlyList<OverKizApi.Models.EventState> states)
		{
		}

	public void UpdateLabel (string newLabel)
		{
		var label = newLabel?.Trim () ?? string.Empty;
		if (string.Equals (DeviceLabel, label, StringComparison.Ordinal))
			return;
		DeviceLabel = label;
		if (_frameworkReady != 0)
			NotifyPropertyChanged ("deviceLabel", new DriverEntityValue (DeviceLabel));
		}

	// ── Extension UI properties ───────────────────────────────────────────

	[EntityProperty (Id = "deviceLabel", FriendlyName = "Room Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string DeviceLabel
		{
		get; private set;
		}

	[EntityProperty (Id = "isTwoWay", FriendlyName = "Two-Way")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool IsTwoWay
		{
		get; private set;
		}

	[EntityProperty (Id = "hasMy", FriendlyName = "Has My")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasMy
		{
		get; private set;
		}

	// ── Extension UI commands (room-level) ────────────────────────────────

	[EntityCommand (Id = "open", FriendlyName = "Open All")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open () => _openAll ();

	[EntityCommand (Id = "close", FriendlyName = "Close All")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close () => _closeAll ();

	[EntityCommand (Id = "stop", FriendlyName = "Stop All")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop () => _stopAll ();

	[EntityCommand (Id = "my", FriendlyName = "My All")]
	[EntityCommandMetadata (Programmable = true)]
	public void My () => _myAll ();

	[EntityCommand (Id = "setOpenPercent", FriendlyName = "Set Open % (All)")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent (
		[EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value)
		=> _setOpenPercentAll (value);

	/// <summary>
	/// Updates the displayed open-percent for member <paramref name="index"/> and
	/// notifies the UI.  Called by the platform driver when the shade's state changes.
	/// </summary>
	public void UpdateMemberOpenPercent (int index, int openPercent)
		{
		if (index < 0 || index >= _members.Count)
			return;
		var key = "openPercent_" + index;
		_memberProps[key] = new DriverEntityValue (openPercent);
		if (_frameworkReady != 0)
			NotifyPropertyChanged (key, new DriverEntityValue (openPercent));
		}

	// ── Polling

	public void StartPolling (OverkizWorkQueue queue)
		{
		if (Interlocked.CompareExchange (ref _frameworkReady, 1, 0) != 0)
			{
			// Already commissioned — just go online.
			SetOnline (true);
			return;
			}

		Log ("StartPolling: pushing initial notifications");

		if (_uiDefinition != null)
			{
			DriverEntityValue? uiValue = _uiDefinition.GetValue (null, null);
			if (uiValue.HasValue)
				NotifyPropertyChanged (UiDefinitionProperty.Name, uiValue.Value);
			}

		NotifyPropertyChanged ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady));
		NotifyPropertyChanged ("deviceLabel", new DriverEntityValue (DeviceLabel));
		NotifyPropertyChanged ("isTwoWay", new DriverEntityValue (IsTwoWay));
		NotifyPropertyChanged ("hasMy", new DriverEntityValue (HasMy));
		for (var i = 0; i < MAX_SLOTS; i++)
			{
			NotifyPropertyChanged ("openPercent_" + i, _memberProps["openPercent_" + i]);
			NotifyPropertyChanged ("shadeLabel_" + i, _memberProps["shadeLabel_" + i]);
			NotifyPropertyChanged ("visible_" + i, _memberProps["visible_" + i]);
			NotifyPropertyChanged ("visible_slider_" + i, _memberProps["visible_slider_" + i]);
			NotifyPropertyChanged ("visible_my_" + i, _memberProps["visible_my_" + i]);
			}

		SetOnline (true);
		}

	public void StopPolling () => SetOnline (false);

	// ── Constructor ───────────────────────────────────────────────────────

	public OverkizRoomEntity (
		string controllerId,
		string roomLabel,
		IReadOnlyList<RoomMemberConfig> slotConfigs,
		IReadOnlyList<RoomMember> members,
		Action openAll,
		Action closeAll,
		Action stopAll,
		Action myAll,
		Action<int> setOpenPercentAll,
		IComponentLogger initLogger,
		DriverControllerLogger logger,
		DriverImplementationResources resources,
		string driverDataDirectoryPath)
		: base (controllerId)
		{
		_ = initLogger; // reserved for future use
		_slotConfigs = slotConfigs ?? throw new ArgumentNullException (nameof (slotConfigs));
		_members = members ?? throw new ArgumentNullException (nameof (members));
		_openAll = openAll ?? throw new ArgumentNullException (nameof (openAll));
		_closeAll = closeAll ?? throw new ArgumentNullException (nameof (closeAll));
		_stopAll = stopAll ?? throw new ArgumentNullException (nameof (stopAll));
		_myAll = myAll ?? throw new ArgumentNullException (nameof (myAll));
		_setOpenPercentAll = setOpenPercentAll ?? throw new ArgumentNullException (nameof (setOpenPercentAll));
		_logger = logger;
		_driverDataDirectoryPath = driverDataDirectoryPath;

		DeviceLabel = roomLabel ?? string.Empty;

		// Build a label → member map for initial visibility.
		var labelToMember = new Dictionary<string, RoomMember> (StringComparer.OrdinalIgnoreCase);
		foreach (RoomMember m in _members)
			labelToMember[m.Label] = m;

		// Initialise all MaxSlots property caches.  Slots beyond the configured
		// count are permanently hidden (visible_N = false).
		for (var i = 0; i < MAX_SLOTS; i++)
			{
			var configured = i < _slotConfigs.Count;
			var apiLabel   = configured ? _slotConfigs[i].ApiLabel : string.Empty;
			var dispName   = configured ? _slotConfigs[i].DisplayName : string.Empty;
			RoomMember m   = null;
			var present    = configured && labelToMember.TryGetValue (apiLabel, out m);
			if (present)
				{
				IsTwoWay |= m.IsTwoWay;
				HasMy |= m.HasMy;
				}

			_memberProps["openPercent_" + i] = new DriverEntityValue (present ? (m?.GetOpenPercent?.Invoke () ?? 0) : 0);
			_memberProps["shadeLabel_" + i] = new DriverEntityValue (dispName);
			_memberProps["visible_" + i] = new DriverEntityValue (present);
			_memberProps["visible_slider_" + i] = new DriverEntityValue (present && m != null && m.IsTwoWay);
			_memberProps["visible_my_" + i] = new DriverEntityValue (present && m != null && m.HasMy);
			}

		Log ("Constructed: label=" + DeviceLabel + " slots=" + _slotConfigs.Count + " members=" + _members.Count + " isTwoWay=" + IsTwoWay + " hasMy=" + HasMy);

		// Load the shared static room UI definition from the package directory.
		try
			{
			var baseDir = driverDataDirectoryPath ?? Path.GetTempPath ();
			var roomRoot = Path.Combine (baseDir, "room");
			Log ("UiDefinition: looking in " + roomRoot);
			_uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists (roomRoot, resources.InitLogger, LogEntryLevel.Error);
			Log ("UiDefinition loaded=" + (_uiDefinition != null));
			}
		catch (Exception ex)
			{
			Log ("UiDefinition load failed: " + ex.Message);
			}

		AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);

		// Register extension command executors.
		var doCommand = new ExtensionDoCommandExecutor (GetCommand, resources.Logger);
		AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);

		var setPropertyValue = new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger);
		AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);

		// Register per-member dynamic commands and properties.
		RegisterMemberCommandsAndProperties ();

		ReadyIndicatorIsReady = true;
		}

	// ── Dynamic UI generation ─────────────────────────────────────────────

	/// <summary>
	/// Registers per-member commands (open_N, close_N, stop_N, my_N, setOpenPercent_N)
	/// and properties (openPercent_N, shadeLabel_N) for each member shade using the
	/// <see cref="BaseDriverEntity"/> AddCommand / AddProperty APIs so that
	/// ExtensionDoCommandExecutor / ExtensionSetPropertyValueExecutor can route to them.
	/// </summary>
	private void RegisterMemberCommandsAndProperties ()
		{
		// Shared no-op definitions.
		var noName = new DriverEntityLocalizedString (null, null);
		var noDef = new DriverEntityCommandDefinition (null, null, null, noName);
		var noMeta = new DriverEntityCommandMetadata (false, false);

		// Type definitions for int (0–100), string, and bool.
		var intRange = new DriverEntityValueRange (0.0, 100.0, null);
		var intType = new DriverEntityTypeDefinition (DriverEntityValueType.Integer, DriverEntityValueType.Uninitialized, null, intRange, null, null, null);
		var strType = new DriverEntityTypeDefinition (DriverEntityValueType.String, DriverEntityValueType.Uninitialized, null, null, null, null, null);
		var boolType = new DriverEntityTypeDefinition (DriverEntityValueType.Boolean, DriverEntityValueType.Uninitialized, null, null, null, null, null);

		// Success result (not failed, no return value).
		var ok = new DriverEntityCommandResult (false, null);

		var propMeta = new DriverEntityPropertyMetadata (false, true, false);
		var propMetaPgm = new DriverEntityPropertyMetadata (true, true, false);

		for (var i = 0; i < MAX_SLOTS; i++)
			{
			var capturedIdx = i;

			// ── Commands — delegate through to current member in this slot ───

			AddCommand (this, "open_" + i, new DelegateCommandInstance ("open_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
						GetSlotMember (capturedIdx)?.Open ();
						cb?.Invoke (ok);
					}, null));

			AddCommand (this, "close_" + i, new DelegateCommandInstance ("close_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
						GetSlotMember (capturedIdx)?.Close ();
						cb?.Invoke (ok);
					}, null));

			AddCommand (this, "stop_" + i, new DelegateCommandInstance ("stop_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
						GetSlotMember (capturedIdx)?.Stop ();
						cb?.Invoke (ok);
					}, null));

			AddCommand (this, "my_" + i, new DelegateCommandInstance ("my_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
						GetSlotMember (capturedIdx)?.My ();
						cb?.Invoke (ok);
					}, null));

			var pctParamDef = new DriverEntityParameterDefinition (noName, null, intType, null, null, null, null, false, false, null);
			var pctParams = new Dictionary<string, DriverEntityParameterDefinition> { ["value"] = pctParamDef };
			var pctCmdDef = new DriverEntityCommandDefinition (pctParams, intType, null, noName);
			AddCommand (this, "setOpenPercent_" + i, new DelegateCommandInstance ("setOpenPercent_" + i, pctCmdDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
						if (args != null && args.TryGetValue ("value", out DriverEntityValue pv))
							{
							_ = pv.TryGetValue (out int pct);
							GetSlotMember (capturedIdx)?.SetOpenPercent (pct);
							}

						cb?.Invoke (ok);
					}, ["value"]));

			// ── Properties ───────────────────────────────────────────────

			var intPropDef = new DriverEntityPropertyDefinition (noName, null, intType, null, null, null, null);
			var strPropDef = new DriverEntityPropertyDefinition (noName, null, strType, null, null, null, null);
			var boolPropDef = new DriverEntityPropertyDefinition (noName, null, boolType, null, null, null, null);

			AddProperty (this, "openPercent_" + i, new DelegatePropertyInstance (intPropDef, propMetaPgm,
				(inst, lookup) =>
					{
						_ = _memberProps.TryGetValue ("openPercent_" + capturedIdx, out DriverEntityValue v);
						return v;
					}));

			AddProperty (this, "shadeLabel_" + i, new DelegatePropertyInstance (strPropDef, propMeta,
				(inst, lookup) =>
					{
						_ = _memberProps.TryGetValue ("shadeLabel_" + capturedIdx, out DriverEntityValue v);
						return v;
					}));

			AddProperty (this, "visible_" + i, new DelegatePropertyInstance (boolPropDef, propMeta,
				(inst, lookup) =>
					{
						_ = _memberProps.TryGetValue ("visible_" + capturedIdx, out DriverEntityValue v);
						return v;
					}));

			AddProperty (this, "visible_slider_" + i, new DelegatePropertyInstance (boolPropDef, propMeta,
				(inst, lookup) =>
					{
						_ = _memberProps.TryGetValue ("visible_slider_" + capturedIdx, out DriverEntityValue v);
						return v;
					}));

			AddProperty (this, "visible_my_" + i, new DelegatePropertyInstance (boolPropDef, propMeta,
				(inst, lookup) =>
					{
						_ = _memberProps.TryGetValue ("visible_my_" + capturedIdx, out DriverEntityValue v);
						return v;
					}));
			}
		}

	/// <summary>
	/// Helper to look up the currently-present member for a configured slot index.
	/// Returns null if that blind is not currently online/present.
	/// </summary>
	private RoomMember GetSlotMember (int slotIndex)
		{
		if (slotIndex < 0 || slotIndex >= _slotConfigs.Count)
			return null;
		var apiLabel = _slotConfigs[slotIndex].ApiLabel;
		foreach (RoomMember m in _members)
			{
			if (string.Equals (m.Label, apiLabel, StringComparison.OrdinalIgnoreCase))
				return m;
			}

		return null;
		}

	/// <summary>
	/// Updates the room's active member list in place.
	/// The UI XML is never rewritten — only the <c>visible_N</c> boolean properties
	/// are updated to show/hide the slots whose blinds are currently present.
	/// </summary>
	public void UpdateMembers (IReadOnlyList<RoomMember> newMembers)
		{
		_members = newMembers ?? throw new ArgumentNullException (nameof (newMembers));

		// Rebuild a label → member map.
		var labelToMember = new Dictionary<string, RoomMember> (StringComparer.OrdinalIgnoreCase);
		foreach (RoomMember m in _members)
			labelToMember[m.Label] = m;

		// Recompute aggregate flags and update per-slot caches.
		IsTwoWay = false;
		HasMy = false;
		for (var i = 0; i < MAX_SLOTS; i++)
			{
			var configured = i < _slotConfigs.Count;
			var apiLabel   = configured ? _slotConfigs[i].ApiLabel : string.Empty;
			RoomMember m   = null;
			var present    = configured && labelToMember.TryGetValue (apiLabel, out m);
			if (present)
				{
				IsTwoWay |= m.IsTwoWay;
				HasMy |= m.HasMy;
				}

			_memberProps["openPercent_" + i] = new DriverEntityValue (present ? (m?.GetOpenPercent?.Invoke () ?? 0) : 0);
			_memberProps["visible_" + i] = new DriverEntityValue (present);
			_memberProps["visible_slider_" + i] = new DriverEntityValue (present && m != null && m.IsTwoWay);
			_memberProps["visible_my_" + i] = new DriverEntityValue (present && m != null && m.HasMy);
			}

		if (_frameworkReady != 0)
			{
			NotifyPropertyChanged ("isTwoWay", new DriverEntityValue (IsTwoWay));
			NotifyPropertyChanged ("hasMy", new DriverEntityValue (HasMy));
			for (var i = 0; i < MAX_SLOTS; i++)
				{
				NotifyPropertyChanged ("openPercent_" + i, _memberProps["openPercent_" + i]);
				NotifyPropertyChanged ("visible_" + i, _memberProps["visible_" + i]);
				NotifyPropertyChanged ("visible_slider_" + i, _memberProps["visible_slider_" + i]);
				NotifyPropertyChanged ("visible_my_" + i, _memberProps["visible_my_" + i]);
				}
			}

		Log ("UpdateMembers: members=" + _members.Count + " slots=" + _slotConfigs.Count + " isTwoWay=" + IsTwoWay + " hasMy=" + HasMy);
		}

	}
