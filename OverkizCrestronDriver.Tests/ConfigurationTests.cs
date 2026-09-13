// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;

using NUnit.Framework;

using OverKiz.CrestronDriver;
namespace OverkizCrestronDriver.Tests;

[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase)]
public sealed class ConfigurationTests
	{
	private static T Call<T> (string name, params object[] args) => TestSupport.Call<T> (typeof (OverkizPlatformDriver), name, args);
	[TestCase (null, "")]
	[TestCase ("", "")]
	[TestCase (" \t\r\n", "")]
	[TestCase ("  Living\u00a0\u2003Room \t Blind  ", "Living Room Blind")]
	[TestCase ("Café Window", "Café Window")]
	public void LabelNormalization_PreservesNamesAndUnifiesWhitespace (string input, string expected) => Assert.That (Call<string> ("NormalizeLabel", input), Is.EqualTo (expected));
	[TestCase (null)]
	[TestCase ("")]
	[TestCase ("bad;=Blind;Room=, ; =Blind")]
	public void RoomGroups_EmptyOrMalformedEntriesAreIgnored (string text) => Assert.That (Call<Dictionary<string, RoomGroupEntry>> ("ParseRoomGroups", text), Is.Empty);
	[Test]
	public void RoomGroups_PreserveMemberOrderAndDisplayOverrides ()
		{
		var rooms = Call<Dictionary<string, RoomGroupEntry>> ("ParseRoomGroups", " Living\u00a0Room : Lounge = West : Sunset, East; Office=Desk ");
		Assert.That (rooms.Keys, Is.EquivalentTo (new[] { "Living Room", "Office" }));
		Assert.That (rooms["living room"].RoomDisplayName, Is.EqualTo ("Lounge"));
		Assert.That (rooms["Living Room"].Members.Select (m => m.ApiLabel), Is.EqualTo (new[] { "West", "East" }));
		Assert.That (rooms["Living Room"].Members.Select (m => m.DisplayName), Is.EqualTo (new[] { "Sunset", "East" }));
		Assert.That (rooms["Office"].RoomDisplayName, Is.EqualTo ("Office"));
		}
	[Test]
	public void RoomGroups_EmptyOverridesFallBackToApiNames ()
		{
		var room = Call<Dictionary<string, RoomGroupEntry>> ("ParseRoomGroups", "Room: = Blind: ")["Room"];
		Assert.That (room.RoomDisplayName, Is.EqualTo ("Room"));
		Assert.That (room.Members.Single ().DisplayName, Is.EqualTo ("Blind"));
		}
	[Test]
	public void RoomGroups_LastDuplicateWinsIgnoringCase ()
		{
		var rooms = Call<Dictionary<string, RoomGroupEntry>> ("ParseRoomGroups", "Room=Old;ROOM:Updated=New");
		Assert.That (rooms, Has.Count.EqualTo (1));
		Assert.That (rooms["room"].RoomDisplayName, Is.EqualTo ("Updated"));
		Assert.That (rooms["room"].Members.Single ().ApiLabel, Is.EqualTo ("New"));
		}
	[TestCase (null)]
	[TestCase ("")]
	[TestCase ("bad;:Name;Blind: ; :Display")]
	public void ShadeNames_InvalidEntriesAreIgnored (string text) => Assert.That (Call<Dictionary<string, string>> ("ParseShadeDisplayNames", text), Is.Empty);
	[Test]
	public void ShadeNames_NormalizeWhitespaceAndUseLastCaseInsensitiveOverride ()
		{
		var names = Call<Dictionary<string, string>> ("ParseShadeDisplayNames", "Blind:Old;BLIND: New\tName ;Second:Other");
		Assert.That (names, Has.Count.EqualTo (2));
		Assert.That (names["blind"], Is.EqualTo ("New Name"));
		Assert.That (names["second"], Is.EqualTo ("Other"));
		}
	[TestCase ("io://gateway/123#1", "io___gateway_123_1")]
	[TestCase ("shade-1", "shade_1")]
	[TestCase ("", "")]
	[TestCase ("Café123", "Café123")]
	public void ControllerIds_ReplacePunctuationAndRetainLettersAndDigits (string input, string expected) => Assert.That (Call<string> ("MakeSafeControllerId", input), Is.EqualTo (expected));
	}