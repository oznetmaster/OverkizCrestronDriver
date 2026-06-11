// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
	private IReadOnlyList<RoomMemberConfig> _slotConfigs;

	// Set once the framework has commissioned the entity.
	private int _frameworkReady;

	private readonly UiDefinitionProperty _uiDefinition;

	[Conditional ("DEBUG")]
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
		TraceNotify ("onlineIndicator:isOnline", new DriverEntityValue (online));
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
			TraceNotify ("deviceLabel", new DriverEntityValue (DeviceLabel));
		}

	// ── Extension UI properties ───────────────────────────────────────────

	[EntityProperty (Id = "deviceLabel", FriendlyName = "Room Label")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string DeviceLabel
		{
		get;
		private set;
		}

	[EntityProperty (Id = "isTwoWay", FriendlyName = "Two-Way")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool IsTwoWay
		{
		get;
		private set;
		}

	[EntityProperty (Id = "hasMy", FriendlyName = "Has My")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasMy
		{
		get;
		private set;
		}

	[EntityProperty (Id = "openPercent_0", FriendlyName = "Open % 1", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_0 { get; private set; }

	[EntityProperty (Id = "shadeLabel_0", FriendlyName = "Shade Label 1")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_0 { get; private set; }

	[EntityProperty (Id = "visible_0", FriendlyName = "Visible 1")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_0 { get; private set; }

	[EntityProperty (Id = "visible_slider_0", FriendlyName = "Visible Slider 1")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_0 { get; private set; }

	[EntityProperty (Id = "visible_my_0", FriendlyName = "Visible My 1")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_0 { get; private set; }

	[EntityProperty (Id = "openPercent_1", FriendlyName = "Open % 2", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_1 { get; private set; }

	[EntityProperty (Id = "shadeLabel_1", FriendlyName = "Shade Label 2")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_1 { get; private set; }

	[EntityProperty (Id = "visible_1", FriendlyName = "Visible 2")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_1 { get; private set; }

	[EntityProperty (Id = "visible_slider_1", FriendlyName = "Visible Slider 2")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_1 { get; private set; }

	[EntityProperty (Id = "visible_my_1", FriendlyName = "Visible My 2")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_1 { get; private set; }

	[EntityProperty (Id = "openPercent_2", FriendlyName = "Open % 3", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_2 { get; private set; }

	[EntityProperty (Id = "shadeLabel_2", FriendlyName = "Shade Label 3")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_2 { get; private set; }

	[EntityProperty (Id = "visible_2", FriendlyName = "Visible 3")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_2 { get; private set; }

	[EntityProperty (Id = "visible_slider_2", FriendlyName = "Visible Slider 3")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_2 { get; private set; }

	[EntityProperty (Id = "visible_my_2", FriendlyName = "Visible My 3")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_2 { get; private set; }

	[EntityProperty (Id = "openPercent_3", FriendlyName = "Open % 4", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_3 { get; private set; }

	[EntityProperty (Id = "shadeLabel_3", FriendlyName = "Shade Label 4")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_3 { get; private set; }

	[EntityProperty (Id = "visible_3", FriendlyName = "Visible 4")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_3 { get; private set; }

	[EntityProperty (Id = "visible_slider_3", FriendlyName = "Visible Slider 4")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_3 { get; private set; }

	[EntityProperty (Id = "visible_my_3", FriendlyName = "Visible My 4")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_3 { get; private set; }

	[EntityProperty (Id = "openPercent_4", FriendlyName = "Open % 5", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_4 { get; private set; }

	[EntityProperty (Id = "shadeLabel_4", FriendlyName = "Shade Label 5")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_4 { get; private set; }

	[EntityProperty (Id = "visible_4", FriendlyName = "Visible 5")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_4 { get; private set; }

	[EntityProperty (Id = "visible_slider_4", FriendlyName = "Visible Slider 5")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_4 { get; private set; }

	[EntityProperty (Id = "visible_my_4", FriendlyName = "Visible My 5")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_4 { get; private set; }

	[EntityProperty (Id = "openPercent_5", FriendlyName = "Open % 6", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_5 { get; private set; }

	[EntityProperty (Id = "shadeLabel_5", FriendlyName = "Shade Label 6")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_5 { get; private set; }

	[EntityProperty (Id = "visible_5", FriendlyName = "Visible 6")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_5 { get; private set; }

	[EntityProperty (Id = "visible_slider_5", FriendlyName = "Visible Slider 6")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_5 { get; private set; }

	[EntityProperty (Id = "visible_my_5", FriendlyName = "Visible My 6")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_5 { get; private set; }

	[EntityProperty (Id = "openPercent_6", FriendlyName = "Open % 7", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_6 { get; private set; }

	[EntityProperty (Id = "shadeLabel_6", FriendlyName = "Shade Label 7")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_6 { get; private set; }

	[EntityProperty (Id = "visible_6", FriendlyName = "Visible 7")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_6 { get; private set; }

	[EntityProperty (Id = "visible_slider_6", FriendlyName = "Visible Slider 7")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_6 { get; private set; }

	[EntityProperty (Id = "visible_my_6", FriendlyName = "Visible My 7")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_6 { get; private set; }

	[EntityProperty (Id = "openPercent_7", FriendlyName = "Open % 8", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_7 { get; private set; }

	[EntityProperty (Id = "shadeLabel_7", FriendlyName = "Shade Label 8")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_7 { get; private set; }

	[EntityProperty (Id = "visible_7", FriendlyName = "Visible 8")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_7 { get; private set; }

	[EntityProperty (Id = "visible_slider_7", FriendlyName = "Visible Slider 8")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_7 { get; private set; }

	[EntityProperty (Id = "visible_my_7", FriendlyName = "Visible My 8")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_7 { get; private set; }

	[EntityProperty (Id = "openPercent_8", FriendlyName = "Open % 9", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_8 { get; private set; }

	[EntityProperty (Id = "shadeLabel_8", FriendlyName = "Shade Label 9")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_8 { get; private set; }

	[EntityProperty (Id = "visible_8", FriendlyName = "Visible 9")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_8 { get; private set; }

	[EntityProperty (Id = "visible_slider_8", FriendlyName = "Visible Slider 9")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_8 { get; private set; }

	[EntityProperty (Id = "visible_my_8", FriendlyName = "Visible My 9")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_8 { get; private set; }

	[EntityProperty (Id = "openPercent_9", FriendlyName = "Open % 10", RangeMinimum = 0, RangeMaximum = 100, RangeStepSize = 1)]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public int OpenPercent_9 { get; private set; }

	[EntityProperty (Id = "shadeLabel_9", FriendlyName = "Shade Label 10")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ShadeLabel_9 { get; private set; }

	[EntityProperty (Id = "visible_9", FriendlyName = "Visible 10")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool Visible_9 { get; private set; }

	[EntityProperty (Id = "visible_slider_9", FriendlyName = "Visible Slider 10")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleSlider_9 { get; private set; }

	[EntityProperty (Id = "visible_my_9", FriendlyName = "Visible My 10")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool VisibleMy_9 { get; private set; }

	// ── Extension UI commands (room-level) ────────────────────────────────

	[EntityCommand (Id = "open", FriendlyName = "Open All")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open ()
		{
		DebugLog ("COMMAND open invoked");
		_openAll ();
		}

	[EntityCommand (Id = "close", FriendlyName = "Close All")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close ()
		{
		DebugLog ("COMMAND close invoked");
		_closeAll ();
		}

	[EntityCommand (Id = "stop", FriendlyName = "Stop All")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop ()
		{
		DebugLog ("COMMAND stop invoked");
		_stopAll ();
		}

	[EntityCommand (Id = "my", FriendlyName = "My All")]
	[EntityCommandMetadata (Programmable = true)]
	public void My ()
		{
		DebugLog ("COMMAND my invoked");
		_myAll ();
		}

	[EntityCommand (Id = "setOpenPercent", FriendlyName = "Set Open % (All)")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercentAll (
		[EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value)
		{
		DebugLog ("COMMAND setOpenPercent invoked value=" + value);
		_setOpenPercentAll (value);
		}

	private void InvokeSlotCommand (int index, string commandName, Action<RoomMember> action)
		{
		Log ("COMMAND " + commandName + " invoked");
		action (GetSlotMember (index));
		}

	private void InvokeSlotSetOpenPercent (int index, int value)
		{
		DebugLog ("COMMAND setOpenPercent_" + index + " invoked value=" + value);
		GetSlotMember (index)?.SetOpenPercent (value);
		}

	[EntityCommand (Id = "open_0", FriendlyName = "Open 1")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_0 () => InvokeSlotCommand (0, "open_0", member => member?.Open ());

	[EntityCommand (Id = "close_0", FriendlyName = "Close 1")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_0 () => InvokeSlotCommand (0, "close_0", member => member?.Close ());

	[EntityCommand (Id = "stop_0", FriendlyName = "Stop 1")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_0 () => InvokeSlotCommand (0, "stop_0", member => member?.Stop ());

	[EntityCommand (Id = "my_0", FriendlyName = "My 1")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_0 () => InvokeSlotCommand (0, "my_0", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_0", FriendlyName = "Set Open % 1")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_0 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (0, value);

	[EntityCommand (Id = "open_1", FriendlyName = "Open 2")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_1 () => InvokeSlotCommand (1, "open_1", member => member?.Open ());

	[EntityCommand (Id = "close_1", FriendlyName = "Close 2")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_1 () => InvokeSlotCommand (1, "close_1", member => member?.Close ());

	[EntityCommand (Id = "stop_1", FriendlyName = "Stop 2")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_1 () => InvokeSlotCommand (1, "stop_1", member => member?.Stop ());

	[EntityCommand (Id = "my_1", FriendlyName = "My 2")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_1 () => InvokeSlotCommand (1, "my_1", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_1", FriendlyName = "Set Open % 2")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_1 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (1, value);

	[EntityCommand (Id = "open_2", FriendlyName = "Open 3")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_2 () => InvokeSlotCommand (2, "open_2", member => member?.Open ());

	[EntityCommand (Id = "close_2", FriendlyName = "Close 3")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_2 () => InvokeSlotCommand (2, "close_2", member => member?.Close ());

	[EntityCommand (Id = "stop_2", FriendlyName = "Stop 3")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_2 () => InvokeSlotCommand (2, "stop_2", member => member?.Stop ());

	[EntityCommand (Id = "my_2", FriendlyName = "My 3")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_2 () => InvokeSlotCommand (2, "my_2", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_2", FriendlyName = "Set Open % 3")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_2 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (2, value);

	[EntityCommand (Id = "open_3", FriendlyName = "Open 4")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_3 () => InvokeSlotCommand (3, "open_3", member => member?.Open ());

	[EntityCommand (Id = "close_3", FriendlyName = "Close 4")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_3 () => InvokeSlotCommand (3, "close_3", member => member?.Close ());

	[EntityCommand (Id = "stop_3", FriendlyName = "Stop 4")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_3 () => InvokeSlotCommand (3, "stop_3", member => member?.Stop ());

	[EntityCommand (Id = "my_3", FriendlyName = "My 4")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_3 () => InvokeSlotCommand (3, "my_3", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_3", FriendlyName = "Set Open % 4")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_3 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (3, value);

	[EntityCommand (Id = "open_4", FriendlyName = "Open 5")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_4 () => InvokeSlotCommand (4, "open_4", member => member?.Open ());

	[EntityCommand (Id = "close_4", FriendlyName = "Close 5")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_4 () => InvokeSlotCommand (4, "close_4", member => member?.Close ());

	[EntityCommand (Id = "stop_4", FriendlyName = "Stop 5")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_4 () => InvokeSlotCommand (4, "stop_4", member => member?.Stop ());

	[EntityCommand (Id = "my_4", FriendlyName = "My 5")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_4 () => InvokeSlotCommand (4, "my_4", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_4", FriendlyName = "Set Open % 5")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_4 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (4, value);

	[EntityCommand (Id = "open_5", FriendlyName = "Open 6")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_5 () => InvokeSlotCommand (5, "open_5", member => member?.Open ());

	[EntityCommand (Id = "close_5", FriendlyName = "Close 6")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_5 () => InvokeSlotCommand (5, "close_5", member => member?.Close ());

	[EntityCommand (Id = "stop_5", FriendlyName = "Stop 6")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_5 () => InvokeSlotCommand (5, "stop_5", member => member?.Stop ());

	[EntityCommand (Id = "my_5", FriendlyName = "My 6")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_5 () => InvokeSlotCommand (5, "my_5", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_5", FriendlyName = "Set Open % 6")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_5 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (5, value);

	[EntityCommand (Id = "open_6", FriendlyName = "Open 7")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_6 () => InvokeSlotCommand (6, "open_6", member => member?.Open ());

	[EntityCommand (Id = "close_6", FriendlyName = "Close 7")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_6 () => InvokeSlotCommand (6, "close_6", member => member?.Close ());

	[EntityCommand (Id = "stop_6", FriendlyName = "Stop 7")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_6 () => InvokeSlotCommand (6, "stop_6", member => member?.Stop ());

	[EntityCommand (Id = "my_6", FriendlyName = "My 7")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_6 () => InvokeSlotCommand (6, "my_6", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_6", FriendlyName = "Set Open % 7")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_6 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (6, value);

	[EntityCommand (Id = "open_7", FriendlyName = "Open 8")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_7 () => InvokeSlotCommand (7, "open_7", member => member?.Open ());

	[EntityCommand (Id = "close_7", FriendlyName = "Close 8")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_7 () => InvokeSlotCommand (7, "close_7", member => member?.Close ());

	[EntityCommand (Id = "stop_7", FriendlyName = "Stop 8")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_7 () => InvokeSlotCommand (7, "stop_7", member => member?.Stop ());

	[EntityCommand (Id = "my_7", FriendlyName = "My 8")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_7 () => InvokeSlotCommand (7, "my_7", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_7", FriendlyName = "Set Open % 8")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_7 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (7, value);

	[EntityCommand (Id = "open_8", FriendlyName = "Open 9")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_8 () => InvokeSlotCommand (8, "open_8", member => member?.Open ());

	[EntityCommand (Id = "close_8", FriendlyName = "Close 9")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_8 () => InvokeSlotCommand (8, "close_8", member => member?.Close ());

	[EntityCommand (Id = "stop_8", FriendlyName = "Stop 9")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_8 () => InvokeSlotCommand (8, "stop_8", member => member?.Stop ());

	[EntityCommand (Id = "my_8", FriendlyName = "My 9")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_8 () => InvokeSlotCommand (8, "my_8", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_8", FriendlyName = "Set Open % 9")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_8 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (8, value);

	[EntityCommand (Id = "open_9", FriendlyName = "Open 10")]
	[EntityCommandMetadata (Programmable = true)]
	public void Open_9 () => InvokeSlotCommand (9, "open_9", member => member?.Open ());

	[EntityCommand (Id = "close_9", FriendlyName = "Close 10")]
	[EntityCommandMetadata (Programmable = true)]
	public void Close_9 () => InvokeSlotCommand (9, "close_9", member => member?.Close ());

	[EntityCommand (Id = "stop_9", FriendlyName = "Stop 10")]
	[EntityCommandMetadata (Programmable = true)]
	public void Stop_9 () => InvokeSlotCommand (9, "stop_9", member => member?.Stop ());

	[EntityCommand (Id = "my_9", FriendlyName = "My 10")]
	[EntityCommandMetadata (Programmable = true)]
	public void My_9 () => InvokeSlotCommand (9, "my_9", member => member?.My ());

	[EntityCommand (Id = "setOpenPercent_9", FriendlyName = "Set Open % 10")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOpenPercent_9 ([EntityParameter (RangeMinimum = 0, RangeMaximum = 100)] int value) => InvokeSlotSetOpenPercent (9, value);

	/// <summary>
	/// Updates the displayed open-percent for member <paramref name="index"/> and
	/// notifies the UI.  Called by the platform driver when the shade's state changes.
	/// </summary>
	public void UpdateMemberOpenPercent (int index, int openPercent)
		{
		if (index < 0 || index >= MAX_SLOTS)
			return;
		SetSlotOpenPercent (index, openPercent, _frameworkReady != 0);
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

		DebugLog ("StartPolling: pushing initial notifications");

		TraceUiDefinitionNotification ("StartPolling");

		TraceNotify ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady));
		TraceNotify ("deviceLabel", new DriverEntityValue (DeviceLabel));
		TraceNotify ("isTwoWay", new DriverEntityValue (IsTwoWay));
		TraceNotify ("hasMy", new DriverEntityValue (HasMy));
		for (var i = 0; i < MAX_SLOTS; i++)
			NotifySlotProperties (i);

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

		// Initialise all fixed slot properties. Slots beyond the configured
		// count are permanently hidden (visible_N = false).
		for (var i = 0; i < MAX_SLOTS; i++)
			{
			var configured = i < _slotConfigs.Count;
			var apiLabel = configured ? _slotConfigs[i].ApiLabel : string.Empty;
			var dispName = configured ? _slotConfigs[i].DisplayName : string.Empty;
			RoomMember m = null;
			var present = configured && labelToMember.TryGetValue (apiLabel, out m);
			if (present)
				{
				IsTwoWay |= m.IsTwoWay;
				HasMy |= m.HasMy;
				}

			SetSlotState (
				i,
				present ? (m?.GetOpenPercent?.Invoke () ?? 0) : 0,
				dispName,
				present,
				present && m != null && m.IsTwoWay,
				present && m != null && m.HasMy,
				false);
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
			LogError ("UiDefinition load failed: " + ex.Message);
			}

		AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);

		var doCommand = new ExtensionDoCommandExecutor (GetCommand, resources.Logger);
		Log ("Registering ExtensionDoCommandExecutor");
		AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);

		var setPropertyValue = new ExtensionSetPropertyValueExecutor (GetCommand, resources.Logger);
		Log ("Registering ExtensionSetPropertyValueExecutor");
		AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);

		ReadyIndicatorIsReady = true;
		}

	private void UpdateIntProperty (Func<int> getter, Action<int> setter, int value, string propertyName, bool notify)
		{
		if (getter () == value)
			return;

		setter (value);
		if (notify)
			TraceNotify (propertyName, new DriverEntityValue (value));
		}

	private void UpdateStringProperty (Func<string> getter, Action<string> setter, string value, string propertyName, bool notify)
		{
		if (string.Equals (getter (), value, StringComparison.Ordinal))
			return;

		setter (value);
		if (notify)
			TraceNotify (propertyName, new DriverEntityValue (value));
		}

	private void UpdateBoolProperty (Func<bool> getter, Action<bool> setter, bool value, string propertyName, bool notify)
		{
		if (getter () == value)
			return;

		setter (value);
		if (notify)
			TraceNotify (propertyName, new DriverEntityValue (value));
		}

	private void SetSlotState (int index, int openPercent, string shadeLabel, bool visible, bool visibleSlider, bool visibleMy, bool notify)
		{
		SetSlotOpenPercent (index, openPercent, notify);
		SetSlotLabel (index, shadeLabel, notify);
		SetSlotVisible (index, visible, notify);
		SetSlotVisibleSlider (index, visibleSlider, notify);
		SetSlotVisibleMy (index, visibleMy, notify);
		}

	private void NotifySlotProperties (int index)
		{
		switch (index)
			{
			case 0:
				TraceNotify ("openPercent_0", new DriverEntityValue (OpenPercent_0));
				TraceNotify ("shadeLabel_0", new DriverEntityValue (ShadeLabel_0));
				TraceNotify ("visible_0", new DriverEntityValue (Visible_0));
				TraceNotify ("visible_slider_0", new DriverEntityValue (VisibleSlider_0));
				TraceNotify ("visible_my_0", new DriverEntityValue (VisibleMy_0));
				break;
			case 1:
				TraceNotify ("openPercent_1", new DriverEntityValue (OpenPercent_1));
				TraceNotify ("shadeLabel_1", new DriverEntityValue (ShadeLabel_1));
				TraceNotify ("visible_1", new DriverEntityValue (Visible_1));
				TraceNotify ("visible_slider_1", new DriverEntityValue (VisibleSlider_1));
				TraceNotify ("visible_my_1", new DriverEntityValue (VisibleMy_1));
				break;
			case 2:
				TraceNotify ("openPercent_2", new DriverEntityValue (OpenPercent_2));
				TraceNotify ("shadeLabel_2", new DriverEntityValue (ShadeLabel_2));
				TraceNotify ("visible_2", new DriverEntityValue (Visible_2));
				TraceNotify ("visible_slider_2", new DriverEntityValue (VisibleSlider_2));
				TraceNotify ("visible_my_2", new DriverEntityValue (VisibleMy_2));
				break;
			case 3:
				TraceNotify ("openPercent_3", new DriverEntityValue (OpenPercent_3));
				TraceNotify ("shadeLabel_3", new DriverEntityValue (ShadeLabel_3));
				TraceNotify ("visible_3", new DriverEntityValue (Visible_3));
				TraceNotify ("visible_slider_3", new DriverEntityValue (VisibleSlider_3));
				TraceNotify ("visible_my_3", new DriverEntityValue (VisibleMy_3));
				break;
			case 4:
				TraceNotify ("openPercent_4", new DriverEntityValue (OpenPercent_4));
				TraceNotify ("shadeLabel_4", new DriverEntityValue (ShadeLabel_4));
				TraceNotify ("visible_4", new DriverEntityValue (Visible_4));
				TraceNotify ("visible_slider_4", new DriverEntityValue (VisibleSlider_4));
				TraceNotify ("visible_my_4", new DriverEntityValue (VisibleMy_4));
				break;
			case 5:
				TraceNotify ("openPercent_5", new DriverEntityValue (OpenPercent_5));
				TraceNotify ("shadeLabel_5", new DriverEntityValue (ShadeLabel_5));
				TraceNotify ("visible_5", new DriverEntityValue (Visible_5));
				TraceNotify ("visible_slider_5", new DriverEntityValue (VisibleSlider_5));
				TraceNotify ("visible_my_5", new DriverEntityValue (VisibleMy_5));
				break;
			case 6:
				TraceNotify ("openPercent_6", new DriverEntityValue (OpenPercent_6));
				TraceNotify ("shadeLabel_6", new DriverEntityValue (ShadeLabel_6));
				TraceNotify ("visible_6", new DriverEntityValue (Visible_6));
				TraceNotify ("visible_slider_6", new DriverEntityValue (VisibleSlider_6));
				TraceNotify ("visible_my_6", new DriverEntityValue (VisibleMy_6));
				break;
			case 7:
				TraceNotify ("openPercent_7", new DriverEntityValue (OpenPercent_7));
				TraceNotify ("shadeLabel_7", new DriverEntityValue (ShadeLabel_7));
				TraceNotify ("visible_7", new DriverEntityValue (Visible_7));
				TraceNotify ("visible_slider_7", new DriverEntityValue (VisibleSlider_7));
				TraceNotify ("visible_my_7", new DriverEntityValue (VisibleMy_7));
				break;
			case 8:
				TraceNotify ("openPercent_8", new DriverEntityValue (OpenPercent_8));
				TraceNotify ("shadeLabel_8", new DriverEntityValue (ShadeLabel_8));
				TraceNotify ("visible_8", new DriverEntityValue (Visible_8));
				TraceNotify ("visible_slider_8", new DriverEntityValue (VisibleSlider_8));
				TraceNotify ("visible_my_8", new DriverEntityValue (VisibleMy_8));
				break;
			case 9:
				TraceNotify ("openPercent_9", new DriverEntityValue (OpenPercent_9));
				TraceNotify ("shadeLabel_9", new DriverEntityValue (ShadeLabel_9));
				TraceNotify ("visible_9", new DriverEntityValue (Visible_9));
				TraceNotify ("visible_slider_9", new DriverEntityValue (VisibleSlider_9));
				TraceNotify ("visible_my_9", new DriverEntityValue (VisibleMy_9));
				break;
			}
		}

	private void SetSlotOpenPercent (int index, int value, bool notify)
		{
		switch (index)
			{
			case 0: UpdateIntProperty (() => OpenPercent_0, v => OpenPercent_0 = v, value, "openPercent_0", notify); break;
			case 1: UpdateIntProperty (() => OpenPercent_1, v => OpenPercent_1 = v, value, "openPercent_1", notify); break;
			case 2: UpdateIntProperty (() => OpenPercent_2, v => OpenPercent_2 = v, value, "openPercent_2", notify); break;
			case 3: UpdateIntProperty (() => OpenPercent_3, v => OpenPercent_3 = v, value, "openPercent_3", notify); break;
			case 4: UpdateIntProperty (() => OpenPercent_4, v => OpenPercent_4 = v, value, "openPercent_4", notify); break;
			case 5: UpdateIntProperty (() => OpenPercent_5, v => OpenPercent_5 = v, value, "openPercent_5", notify); break;
			case 6: UpdateIntProperty (() => OpenPercent_6, v => OpenPercent_6 = v, value, "openPercent_6", notify); break;
			case 7: UpdateIntProperty (() => OpenPercent_7, v => OpenPercent_7 = v, value, "openPercent_7", notify); break;
			case 8: UpdateIntProperty (() => OpenPercent_8, v => OpenPercent_8 = v, value, "openPercent_8", notify); break;
			case 9: UpdateIntProperty (() => OpenPercent_9, v => OpenPercent_9 = v, value, "openPercent_9", notify); break;
			}
		}

	private void SetSlotLabel (int index, string value, bool notify)
		{
		var label = value ?? string.Empty;
		switch (index)
			{
			case 0: UpdateStringProperty (() => ShadeLabel_0, v => ShadeLabel_0 = v, label, "shadeLabel_0", notify); break;
			case 1: UpdateStringProperty (() => ShadeLabel_1, v => ShadeLabel_1 = v, label, "shadeLabel_1", notify); break;
			case 2: UpdateStringProperty (() => ShadeLabel_2, v => ShadeLabel_2 = v, label, "shadeLabel_2", notify); break;
			case 3: UpdateStringProperty (() => ShadeLabel_3, v => ShadeLabel_3 = v, label, "shadeLabel_3", notify); break;
			case 4: UpdateStringProperty (() => ShadeLabel_4, v => ShadeLabel_4 = v, label, "shadeLabel_4", notify); break;
			case 5: UpdateStringProperty (() => ShadeLabel_5, v => ShadeLabel_5 = v, label, "shadeLabel_5", notify); break;
			case 6: UpdateStringProperty (() => ShadeLabel_6, v => ShadeLabel_6 = v, label, "shadeLabel_6", notify); break;
			case 7: UpdateStringProperty (() => ShadeLabel_7, v => ShadeLabel_7 = v, label, "shadeLabel_7", notify); break;
			case 8: UpdateStringProperty (() => ShadeLabel_8, v => ShadeLabel_8 = v, label, "shadeLabel_8", notify); break;
			case 9: UpdateStringProperty (() => ShadeLabel_9, v => ShadeLabel_9 = v, label, "shadeLabel_9", notify); break;
			}
		}

	private void SetSlotVisible (int index, bool value, bool notify)
		{
		switch (index)
			{
			case 0: UpdateBoolProperty (() => Visible_0, v => Visible_0 = v, value, "visible_0", notify); break;
			case 1: UpdateBoolProperty (() => Visible_1, v => Visible_1 = v, value, "visible_1", notify); break;
			case 2: UpdateBoolProperty (() => Visible_2, v => Visible_2 = v, value, "visible_2", notify); break;
			case 3: UpdateBoolProperty (() => Visible_3, v => Visible_3 = v, value, "visible_3", notify); break;
			case 4: UpdateBoolProperty (() => Visible_4, v => Visible_4 = v, value, "visible_4", notify); break;
			case 5: UpdateBoolProperty (() => Visible_5, v => Visible_5 = v, value, "visible_5", notify); break;
			case 6: UpdateBoolProperty (() => Visible_6, v => Visible_6 = v, value, "visible_6", notify); break;
			case 7: UpdateBoolProperty (() => Visible_7, v => Visible_7 = v, value, "visible_7", notify); break;
			case 8: UpdateBoolProperty (() => Visible_8, v => Visible_8 = v, value, "visible_8", notify); break;
			case 9: UpdateBoolProperty (() => Visible_9, v => Visible_9 = v, value, "visible_9", notify); break;
			}
		}

	private void SetSlotVisibleSlider (int index, bool value, bool notify)
		{
		switch (index)
			{
			case 0: UpdateBoolProperty (() => VisibleSlider_0, v => VisibleSlider_0 = v, value, "visible_slider_0", notify); break;
			case 1: UpdateBoolProperty (() => VisibleSlider_1, v => VisibleSlider_1 = v, value, "visible_slider_1", notify); break;
			case 2: UpdateBoolProperty (() => VisibleSlider_2, v => VisibleSlider_2 = v, value, "visible_slider_2", notify); break;
			case 3: UpdateBoolProperty (() => VisibleSlider_3, v => VisibleSlider_3 = v, value, "visible_slider_3", notify); break;
			case 4: UpdateBoolProperty (() => VisibleSlider_4, v => VisibleSlider_4 = v, value, "visible_slider_4", notify); break;
			case 5: UpdateBoolProperty (() => VisibleSlider_5, v => VisibleSlider_5 = v, value, "visible_slider_5", notify); break;
			case 6: UpdateBoolProperty (() => VisibleSlider_6, v => VisibleSlider_6 = v, value, "visible_slider_6", notify); break;
			case 7: UpdateBoolProperty (() => VisibleSlider_7, v => VisibleSlider_7 = v, value, "visible_slider_7", notify); break;
			case 8: UpdateBoolProperty (() => VisibleSlider_8, v => VisibleSlider_8 = v, value, "visible_slider_8", notify); break;
			case 9: UpdateBoolProperty (() => VisibleSlider_9, v => VisibleSlider_9 = v, value, "visible_slider_9", notify); break;
			}
		}

	private void SetSlotVisibleMy (int index, bool value, bool notify)
		{
		switch (index)
			{
			case 0: UpdateBoolProperty (() => VisibleMy_0, v => VisibleMy_0 = v, value, "visible_my_0", notify); break;
			case 1: UpdateBoolProperty (() => VisibleMy_1, v => VisibleMy_1 = v, value, "visible_my_1", notify); break;
			case 2: UpdateBoolProperty (() => VisibleMy_2, v => VisibleMy_2 = v, value, "visible_my_2", notify); break;
			case 3: UpdateBoolProperty (() => VisibleMy_3, v => VisibleMy_3 = v, value, "visible_my_3", notify); break;
			case 4: UpdateBoolProperty (() => VisibleMy_4, v => VisibleMy_4 = v, value, "visible_my_4", notify); break;
			case 5: UpdateBoolProperty (() => VisibleMy_5, v => VisibleMy_5 = v, value, "visible_my_5", notify); break;
			case 6: UpdateBoolProperty (() => VisibleMy_6, v => VisibleMy_6 = v, value, "visible_my_6", notify); break;
			case 7: UpdateBoolProperty (() => VisibleMy_7, v => VisibleMy_7 = v, value, "visible_my_7", notify); break;
			case 8: UpdateBoolProperty (() => VisibleMy_8, v => VisibleMy_8 = v, value, "visible_my_8", notify); break;
			case 9: UpdateBoolProperty (() => VisibleMy_9, v => VisibleMy_9 = v, value, "visible_my_9", notify); break;
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
	public void UpdateMembers (IReadOnlyList<RoomMember> newMembers, IReadOnlyList<RoomMemberConfig> slotConfigs = null)
		{
		_members = newMembers ?? throw new ArgumentNullException (nameof (newMembers));
		if (slotConfigs != null)
			_slotConfigs = slotConfigs;

		// Rebuild a label → member map.
		var labelToMember = new Dictionary<string, RoomMember> (StringComparer.OrdinalIgnoreCase);
		foreach (RoomMember m in _members)
			labelToMember[m.Label] = m;

		// Recompute aggregate flags and update per-slot properties.
		IsTwoWay = false;
		HasMy = false;
		for (var i = 0; i < MAX_SLOTS; i++)
			{
			var configured = i < _slotConfigs.Count;
			var apiLabel = configured ? _slotConfigs[i].ApiLabel : string.Empty;
			var dispName = configured ? _slotConfigs[i].DisplayName : string.Empty;
			RoomMember m = null;
			var present = configured && labelToMember.TryGetValue (apiLabel, out m);
			if (present)
				{
				IsTwoWay |= m.IsTwoWay;
				HasMy |= m.HasMy;
				}

			SetSlotState (
				i,
				present ? (m?.GetOpenPercent?.Invoke () ?? 0) : 0,
				dispName,
				present,
				present && m != null && m.IsTwoWay,
				present && m != null && m.HasMy,
				_frameworkReady != 0);
			}

		if (_frameworkReady != 0)
			{
			TraceNotify ("isTwoWay", new DriverEntityValue (IsTwoWay));
			TraceNotify ("hasMy", new DriverEntityValue (HasMy));
			}

		Log ("UpdateMembers: members=" + _members.Count + " slots=" + _slotConfigs.Count + " isTwoWay=" + IsTwoWay + " hasMy=" + HasMy);
		}

	}