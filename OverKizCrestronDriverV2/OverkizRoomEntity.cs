// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
internal sealed class RoomMember
	{
	public string Label           { get; }
	public bool   IsTwoWay        { get; }
	public bool   HasMy           { get; }
	public Action Open            { get; }
	public Action Close           { get; }
	public Action Stop            { get; }
	public Action My              { get; }
	public Action<int> SetOpenPercent { get; }

	/// <summary>Returns the current open-percent (0–100) for this shade, used for the position slider.</summary>
	public Func<int> GetOpenPercent { get; }

	public RoomMember (
		string label,
		bool isTwoWay,
		bool hasMy,
		Action open,
		Action close,
		Action stop,
		Action my,
		Action<int> setOpenPercent,
		Func<int> getOpenPercent)
		{
		Label         = label ?? string.Empty;
		IsTwoWay      = isTwoWay;
		HasMy         = hasMy;
		Open          = open;
		Close         = close;
		Stop          = stop;
		My            = my;
		SetOpenPercent = setOpenPercent;
		GetOpenPercent = getOpenPercent;
		}
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

	// ── Dynamic per-member property values ───────────────────────────────
	// "openPercent_N" → current open percent (int)
	// "shadeLabel_N"  → shade label string
	private readonly Dictionary<string, DriverEntityValue> _memberProps = new ();

	// Set once the framework has commissioned the entity.
	private int _frameworkReady;

	private UiDefinitionProperty _uiDefinition;
	private string _uiDefinitionXml;

	private void Log (string msg) =>
		_logger?.Log (ControllerId, LogEntryLevel.Info, msg);

	// ── IOverkizEntity ────────────────────────────────────────────────────

	public DeviceUxCategory UxCategory => DeviceUxCategory.Room;

	// ── Online / ready ────────────────────────────────────────────────────

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline { get; private set; }

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady { get; private set; }

	public void SetOnline (bool online)
		{
		if (OnlineIndicatorIsOnline == online)
			return;
		OnlineIndicatorIsOnline = online;
		NotifyPropertyChanged ("onlineIndicator:isOnline", new DriverEntityValue (online));
		}

	public void UpdateAvailability (bool available) => SetOnline (available);

	// ── IOverkizEntity (no-op overrides for shade-specific members) ───────

	public void SetMoving (bool moving) { }

	public void ApplyEventStates (IReadOnlyList<OverKizApi.Models.EventState> states) { }

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
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public string DeviceLabel { get; private set; }

	[EntityProperty (Id = "isTwoWay", FriendlyName = "Two-Way")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool IsTwoWay { get; private set; }

	[EntityProperty (Id = "hasMy", FriendlyName = "Has My")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasMy { get; private set; }

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
		SetOnline (true);
		}

	public void StopPolling () => SetOnline (false);

	// ── Constructor ───────────────────────────────────────────────────────

	public OverkizRoomEntity (
		string controllerId,
		string roomLabel,
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
		_members      = members ?? throw new ArgumentNullException (nameof (members));
		_openAll      = openAll ?? throw new ArgumentNullException (nameof (openAll));
		_closeAll     = closeAll ?? throw new ArgumentNullException (nameof (closeAll));
		_stopAll      = stopAll ?? throw new ArgumentNullException (nameof (stopAll));
		_myAll        = myAll ?? throw new ArgumentNullException (nameof (myAll));
		_setOpenPercentAll       = setOpenPercentAll ?? throw new ArgumentNullException (nameof (setOpenPercentAll));
		_logger                  = logger;
		_driverDataDirectoryPath = driverDataDirectoryPath;

		DeviceLabel = roomLabel ?? string.Empty;

		// Aggregate flags from members.
		foreach (var m in _members)
			{
			IsTwoWay |= m.IsTwoWay;
			HasMy    |= m.HasMy;
			}

		// Initialise per-member property cache.
		for (int i = 0; i < _members.Count; i++)
			{
			_memberProps["openPercent_" + i] = new DriverEntityValue (_members[i].GetOpenPercent?.Invoke () ?? 0);
			_memberProps["shadeLabel_"   + i] = new DriverEntityValue (_members[i].Label);
			}

		Log ("Constructed: label=" + DeviceLabel + " members=" + _members.Count + " isTwoWay=" + IsTwoWay + " hasMy=" + HasMy);

		string uiXml = BuildUiDefinitionXml ();
		_uiDefinitionXml = uiXml;
		Log ("UiDefinition XML length=" + uiXml.Length);
		try
			{
			string baseDir  = driverDataDirectoryPath ?? Path.GetTempPath ();
			string roomRoot = Path.Combine (baseDir, ControllerId);
			string uiDir    = Path.Combine (roomRoot, "uidefinitions");
			Directory.CreateDirectory (uiDir);
			File.WriteAllText (Path.Combine (uiDir, "UiDefinition.xml"), uiXml, new System.Text.UTF8Encoding (false));
			Log ("UiDefinition: looking in " + uiDir + " exists=" + Directory.Exists (uiDir));
			_uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists (roomRoot, resources.InitLogger, LogEntryLevel.Error);
			Log ("UiDefinition loaded=" + (_uiDefinition != null));
			}
		catch (Exception ex)
			{
			Log ("UiDefinition write/load failed: " + ex.Message);
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
		ValuesChanged += OnFirstValuesChanged;
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
		var noDef  = new DriverEntityCommandDefinition (null, null, null, noName);
		var noMeta = new DriverEntityCommandMetadata (false, false);

		// Type definitions for int (0–100) and string.
		var intRange = new DriverEntityValueRange (0.0, 100.0, null);
		var intType  = new DriverEntityTypeDefinition (DriverEntityValueType.Integer, DriverEntityValueType.Uninitialized, null, intRange, null, null, null);
		var strType  = new DriverEntityTypeDefinition (DriverEntityValueType.String,  DriverEntityValueType.Uninitialized, null, null,     null, null, null);

		// Success result (not failed, no return value).
		var ok = new DriverEntityCommandResult (false, null);

		var propMeta    = new DriverEntityPropertyMetadata (false, true,  false);
		var propMetaPgm = new DriverEntityPropertyMetadata (true,  true,  false);

		for (int i = 0; i < _members.Count; i++)
			{
			var m           = _members[i];
			int capturedIdx = i;

			// ── Commands ─────────────────────────────────────────────────

			AddCommand (this, "open_" + i, new DelegateCommandInstance ("open_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
					m.Open ();
					cb?.Invoke (ok);
					}, null));

			AddCommand (this, "close_" + i, new DelegateCommandInstance ("close_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
					m.Close ();
					cb?.Invoke (ok);
					}, null));

			AddCommand (this, "stop_" + i, new DelegateCommandInstance ("stop_" + i, noDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
					m.Stop ();
					cb?.Invoke (ok);
					}, null));

			if (m.HasMy)
				{
				AddCommand (this, "my_" + i, new DelegateCommandInstance ("my_" + i, noDef, noMeta,
					(id, inst, args, lookup, cb) =>
						{
						m.My ();
						cb?.Invoke (ok);
						}, null));
				}

			var pctParamDef = new DriverEntityParameterDefinition (noName, null, intType, null, null, null, null, false, false, null);
			var pctParams   = new Dictionary<string, DriverEntityParameterDefinition> { ["value"] = pctParamDef };
			var pctCmdDef   = new DriverEntityCommandDefinition (pctParams, intType, null, noName);
			AddCommand (this, "setOpenPercent_" + i, new DelegateCommandInstance ("setOpenPercent_" + i, pctCmdDef, noMeta,
				(id, inst, args, lookup, cb) =>
					{
					if (args != null && args.TryGetValue ("value", out DriverEntityValue pv))
						{
						int pct = 0;
						pv.TryGetValue (out pct);
						m.SetOpenPercent (pct);
						}

					cb?.Invoke (ok);
					}, new [] { "value" }));

			// ── Properties ───────────────────────────────────────────────

			var intPropDef = new DriverEntityPropertyDefinition (noName, null, intType, null, null, null, null);
			var strPropDef = new DriverEntityPropertyDefinition (noName, null, strType, null, null, null, null);

			AddProperty (this, "openPercent_" + i, new DelegatePropertyInstance (intPropDef, propMetaPgm,
				(inst, lookup) =>
					{
					_memberProps.TryGetValue ("openPercent_" + capturedIdx, out DriverEntityValue v);
					return v;
					}));

			AddProperty (this, "shadeLabel_" + i, new DelegatePropertyInstance (strPropDef, propMeta,
				(inst, lookup) =>
					{
					_memberProps.TryGetValue ("shadeLabel_" + capturedIdx, out DriverEntityValue v);
					return v;
					}));
			}
		}

	/// <summary>
	/// Updates the room's member list in place without destroying and recreating the entity.
	/// Removes old per-member commands/properties, re-registers new ones, regenerates the UI
	/// XML, and calls <see cref="RaiseDefinitionChangedEvent"/> to notify the application.
	/// </summary>
	public void UpdateMembers (IReadOnlyList<RoomMember> newMembers)
		{
		// Remove all old per-member commands and properties.
		for (int i = 0; i < _members.Count; i++)
			{
			RemoveCommand ("open_"           + i);
			RemoveCommand ("close_"          + i);
			RemoveCommand ("stop_"           + i);
			RemoveCommand ("my_"             + i);
			RemoveCommand ("setOpenPercent_" + i);
			RemoveProperty ("openPercent_"   + i);
			RemoveProperty ("shadeLabel_"    + i);
			}

		// Replace members and recompute aggregate flags.
		_members = newMembers ?? throw new ArgumentNullException (nameof (newMembers));
		IsTwoWay = false;
		HasMy    = false;
		foreach (var m in _members)
			{
			IsTwoWay |= m.IsTwoWay;
			HasMy    |= m.HasMy;
			}

		// Rebuild per-member property cache.
		_memberProps.Clear ();
		for (int i = 0; i < _members.Count; i++)
			{
			_memberProps["openPercent_" + i] = new DriverEntityValue (_members[i].GetOpenPercent?.Invoke () ?? 0);
			_memberProps["shadeLabel_"  + i] = new DriverEntityValue (_members[i].Label);
			}

		// Re-register new per-member commands and properties.
		RegisterMemberCommandsAndProperties ();

		// Regenerate and re-notify UI XML, and update the disk file for consistency.
		string uiXml = BuildUiDefinitionXml ();
		_uiDefinitionXml = uiXml;
		try
			{
			string baseDir  = _driverDataDirectoryPath ?? Path.GetTempPath ();
			string roomRoot = Path.Combine (baseDir, ControllerId);
			string uiDir    = Path.Combine (roomRoot, "uidefinitions");
			Directory.CreateDirectory (uiDir);
			File.WriteAllText (Path.Combine (uiDir, "UiDefinition.xml"), uiXml, new System.Text.UTF8Encoding (false));
			}
		catch (Exception ex)
			{
			Log ("UpdateMembers: UiDefinition write failed: " + ex.Message);
			}

		RaiseDefinitionChangedEvent ();

		if (_frameworkReady != 0)
			{
			NotifyPropertyChanged (UiDefinitionProperty.Name, new DriverEntityValue (_uiDefinitionXml));
			NotifyPropertyChanged ("isTwoWay", new DriverEntityValue (IsTwoWay));
			NotifyPropertyChanged ("hasMy",    new DriverEntityValue (HasMy));
			for (int i = 0; i < _members.Count; i++)
				{
				NotifyPropertyChanged ("openPercent_" + i, _memberProps["openPercent_" + i]);
				NotifyPropertyChanged ("shadeLabel_"  + i, _memberProps["shadeLabel_"  + i]);
				}
			}

		Log ("UpdateMembers: members=" + _members.Count + " isTwoWay=" + IsTwoWay + " hasMy=" + HasMy);
		}

	private string BuildUiDefinitionXml ()
		{
		var sb = new StringBuilder ();
		sb.Append ("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
		sb.Append ("<uidefinition xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"https://prd-use-rad-assets.azurewebsites.net/ExtensionsSchemaDefinition.xsd\">");
		sb.Append ("<version ver=\"2.0\" />");
		sb.Append ("<tile icon=\"#icShadesOpen\" navigation=\"show:MainPage\" showinhomepage=\"#false\" showinroompage=\"#true\" />");
		sb.Append ("<layouts>");
		sb.Append ("<layout id=\"MainPage\" title=\"{deviceLabel}\" isdefaultlayout=\"#true\">");
		sb.Append ("<controls>");

		// ── Room-level aggregate controls ────────────────────────────────
		sb.Append ("<subheader id=\"RoomHdr\" label=\"^AllLabel\" />");
		if (IsTwoWay) sb.Append ("<controlgroup>");
		sb.Append ("<buttongroup>");
		sb.Append ("<button id=\"RoomOpen\"  icon=\"#icShadesOpen\"     action=\"command:open\" />");
		sb.Append ("<button id=\"RoomStop\"  icon=\"#icShadesSemiOpen\" action=\"command:stop\" />");
		sb.Append ("<button id=\"RoomClose\" icon=\"#icShadesClosed\"   action=\"command:close\" />");
		if (HasMy)
			sb.Append ("<button id=\"RoomMy\" label=\"^MyLabel\" icon=\"#icShadesSemiOpen\" action=\"command:my\" />");
		sb.Append ("</buttongroup>");
		if (IsTwoWay)
			{
			sb.Append ("<segmentedslider id=\"RoomSlider\" label=\"^PositionLabel\" value=\"{openPercent_all}\" />");
			sb.Append ("</controlgroup>");
			}

		// ── Per-shade individual controls ────────────────────────────────
		for (int i = 0; i < _members.Count; i++)
			{
			var m = _members[i];
			sb.Append ("<subheader id=\"Shade" + i + "Hdr\" label=\"{shadeLabel_" + i + "}\" />");
			if (m.IsTwoWay) sb.Append ("<controlgroup>");
			sb.Append ("<buttongroup>");
			sb.Append ("<button id=\"Shade" + i + "Open\"  icon=\"#icShadesOpen\"     action=\"command:open_"  + i + "\" />");
			sb.Append ("<button id=\"Shade" + i + "Stop\"  icon=\"#icShadesSemiOpen\" action=\"command:stop_"  + i + "\" />");
			sb.Append ("<button id=\"Shade" + i + "Close\" icon=\"#icShadesClosed\"   action=\"command:close_" + i + "\" />");
			if (m.HasMy)
				sb.Append ("<button id=\"Shade" + i + "My\" label=\"^MyLabel\" icon=\"#icShadesSemiOpen\" action=\"command:my_" + i + "\" />");
			sb.Append ("</buttongroup>");
			if (m.IsTwoWay)
				{
				sb.Append ("<segmentedslider id=\"Shade" + i + "Slider\" label=\"^PositionLabel\" value=\"{openPercent_" + i + "}\" />");
				sb.Append ("</controlgroup>");
				}
			}

		sb.Append ("</controls>");
		sb.Append ("</layout>");
		sb.Append ("</layouts>");
		sb.Append ("<alerts />");
		sb.Append ("</uidefinition>");
		return sb.ToString ();
		}

	// ── State ─────────────────────────────────────────────────────────────

	private void OnFirstValuesChanged (object sender, DevicePropertyChangeEventArgs args)
		{
		if (Interlocked.CompareExchange (ref _frameworkReady, 1, 0) != 0)
			return;

		ValuesChanged -= OnFirstValuesChanged;
		Log ("OnFirstValuesChanged: pushing notifications");

		if (_uiDefinitionXml != null)
			{
			NotifyPropertyChanged (UiDefinitionProperty.Name, new DriverEntityValue (_uiDefinitionXml));
			Log ("OnFirstValuesChanged: UiDefinition notified, length=" + _uiDefinitionXml.Length);
			}
		else
			{
			Log ("OnFirstValuesChanged: _uiDefinitionXml is null");
			}

		NotifyPropertyChanged ("readyIndicator:isReady",   new DriverEntityValue (ReadyIndicatorIsReady));
		NotifyPropertyChanged ("onlineIndicator:isOnline", new DriverEntityValue (OnlineIndicatorIsOnline));
		NotifyPropertyChanged ("deviceLabel",              new DriverEntityValue (DeviceLabel));
		NotifyPropertyChanged ("isTwoWay",                 new DriverEntityValue (IsTwoWay));
		NotifyPropertyChanged ("hasMy",                    new DriverEntityValue (HasMy));
		for (int i = 0; i < _members.Count; i++)
			{
			NotifyPropertyChanged ("openPercent_" + i, _memberProps["openPercent_" + i]);
			NotifyPropertyChanged ("shadeLabel_"   + i, _memberProps["shadeLabel_"   + i]);
			}

		Log ("OnFirstValuesChanged: done, members=" + _members.Count);
		}
	}
