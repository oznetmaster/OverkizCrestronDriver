// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Crestron.DeviceDrivers.SDK;
using OverKiz.CrestronDriver;

namespace OverkizCrestronDriver.Tests;

// Synthetic callbacks only. These tests never connect to a gateway or move a blind.
[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Processor")]
public sealed class RoomLifecycleTests
	{
	private DriverLogger _logger;
	private OverkizRoomEntity _room;
	private readonly List<string> _calls = new ();
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or processor runtime.");
#endif
		_calls.Clear ();
		_room = null;
		_logger = new DriverLogger ("room-lifecycle-test");
		}
	[TearDown]
	public void TearDown ()
		{
		try { _room?.Dispose (); }
		finally { _logger?.Dispose (); }
		}
	private RoomMember Member (string label, bool twoWay = true, bool my = true, int position = 35) =>
		new (label, label, twoWay, my,
			() => _calls.Add (label + ":open"), () => _calls.Add (label + ":close"),
			() => _calls.Add (label + ":stop"), () => _calls.Add (label + ":my"),
			value => _calls.Add (label + ":position:" + value), () => position);
	private OverkizRoomEntity Create (IReadOnlyList<RoomMemberConfig> slots, IReadOnlyList<RoomMember> members) =>
		_room = new OverkizRoomEntity ("synthetic-room", "Synthetic room", slots, members,
			() => _calls.Add ("all:open"), () => _calls.Add ("all:close"),
			() => _calls.Add ("all:stop"), () => _calls.Add ("all:my"),
			value => _calls.Add ("all:position:" + value),
			_logger.GetComponentLogger ("test", "room"), _logger.AppLogger,
			TestSupport.Resources (_logger), TestSupport.DataDirectory);
	[TestCase (0)] [TestCase (1)] [TestCase (2)] [TestCase (3)] [TestCase (4)]
	[TestCase (5)] [TestCase (6)] [TestCase (7)] [TestCase (8)] [TestCase (9)]
	public void EverySlotCommandTargetsOnlyItsConfiguredMember (int slot)
		{
		var members = Enumerable.Range (0, 10).Select (i => Member ("Blind" + i)).ToArray ();
		// Reverse the configured order so accidental list-index routing cannot pass.
		var slots = Enumerable.Range (0, 10).Select (i => new RoomMemberConfig ("Blind" + (9 - i), "Slot" + i)).ToArray ();
		var room = Create (slots, members);
		foreach (string command in new[] { "Open", "Close", "Stop", "My" })
			typeof (OverkizRoomEntity).GetMethod (command + "_" + slot).Invoke (room, null);
		typeof (OverkizRoomEntity).GetMethod ("SetOpenPercent_" + slot).Invoke (room, new object[] { 42 });
		string target = "Blind" + (9 - slot);
		Assert.That (_calls, Is.EqualTo (new[] { target + ":open", target + ":close",
			target + ":stop", target + ":my", target + ":position:42" }));
		}
	[Test]
	public void MissingMemberIsHiddenAndCommandsDoNotFallThroughToAnotherBlind ()
		{
		var room = Create (new[] { new RoomMemberConfig ("Missing", "Missing display"),
			new RoomMemberConfig ("Present", "Present display") }, new[] { Member ("Present") });
		room.Open_0 (); room.Close_0 (); room.Stop_0 (); room.My_0 (); room.SetOpenPercent_0 (42);
		Assert.That (_calls, Is.Empty);
		Assert.That (room.Visible_0, Is.False);
		Assert.That (room.Visible_1, Is.True);
		Assert.That (room.ShadeLabel_1, Is.EqualTo ("Present display"));
		}
	[Test]
	public void MemberReplacementUpdatesVisibilityCapabilitiesAndRouting ()
		{
		var room = Create (new[] { new RoomMemberConfig ("First", "One"), new RoomMemberConfig ("Second", "Two") },
			new[] { Member ("First", position: 20) });
		Assert.That (room.VisibleSlider_0, Is.True);
		Assert.That (room.VisibleMy_0, Is.True);
		room.UpdateMembers (new[] { Member ("Second", twoWay: false, my: false) });
		Assert.That (room.Visible_0, Is.False);
		Assert.That (room.Visible_1, Is.True);
		Assert.That (room.VisibleSlider_1, Is.False);
		Assert.That (room.VisibleMy_1, Is.False);
		Assert.That (room.IsTwoWay, Is.False);
		Assert.That (room.HasMy, Is.False);
		room.Open_0 (); room.Open_1 ();
		Assert.That (_calls, Is.EqualTo (new[] { "Second:open" }));
		}
	[Test]
	public void AggregateCommandsUseAggregateDelegatesWithoutDuplicateSlotDispatch ()
		{
		var room = Create (new[] { new RoomMemberConfig ("First", "One") }, new[] { Member ("First") });
		room.Open (); room.Close (); room.Stop (); room.My (); room.SetOpenPercentAll (40);
		Assert.That (_calls, Is.EqualTo (new[] { "all:open", "all:close", "all:stop", "all:my", "all:position:40" }));
		}
	[Test]
	public void SeparateRoomInstancesKeepCommandsAndStateIndependent ()
		{
		var first = Create (new[] { new RoomMemberConfig ("First", "One") }, new[] { Member ("First") });
		using var other = new OverkizRoomEntity ("other-room", "Other",
			new[] { new RoomMemberConfig ("Other", "Two") }, new[] { Member ("Other", position: 70) },
			() => _calls.Add ("other:all"), () => { }, () => { }, () => { }, value => { },
			_logger.GetComponentLogger ("test", "other"), _logger.AppLogger, TestSupport.Resources (_logger), TestSupport.DataDirectory);
		first.Open_0 ();
		Assert.That (_calls, Is.EqualTo (new[] { "First:open" }));
		Assert.That (other.OpenPercent_0, Is.EqualTo (70));
		Assert.That (first.OpenPercent_0, Is.EqualTo (35));
		}
	}
