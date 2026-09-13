// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;

using NUnit.Framework;

using Crestron.DeviceDrivers.SDK;

using OverKiz.CrestronDriver;

using OverKizApi.Models;

namespace OverkizCrestronDriver.Tests;

[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Processor")]
public sealed class ShadeLifecycleTests
	{
	private DriverLogger _logger;
	private OverkizShadeEntity _shade;
	private readonly List<string> _commands = new ();
	private readonly List<int> _closures = new ();
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or the processor runtime.");
#endif
		_logger = new DriverLogger ("shade-lifecycle-test");
		}
	[TearDown]
	public void TearDown ()
		{
		_shade?.Dispose ();
		_logger?.Dispose ();
		}
	private OverkizShadeEntity Create (bool oneWay = false, bool hasMy = true)
		{
		return _shade = new OverkizShadeEntity ("test-shade", "io://test/shade", "Original", null, oneWay, hasMy,
			_commands.Add, (command, args) => { _commands.Add (command); _closures.Add ((int)args[0]); },
			TestSupport.DataDirectory, _logger.AppLogger, TestSupport.Resources (_logger));
		}
	[TestCase (-10, 100)]
	[TestCase (0, 100)]
	[TestCase (35, 65)]
	[TestCase (100, 0)]
	[TestCase (110, 0)]
	public void PositionCommand_ClampsAndInvertsWithoutInventingObservedState (int open, int closure)
		{
		var shade = Create ();
		shade.UpdateState (75, false);
		shade.SetOpenPercent (open);
		Assert.That (_commands, Is.EqualTo (new[] { "setClosure" }));
		Assert.That (_closures, Is.EqualTo (new[] { closure }));
		Assert.That (shade.GetState ().PropertyValues["openPercent"].GetValue<long> (), Is.EqualTo (25));
		}
	[TestCase (true)]
	[TestCase (false)]
	public void Commands_RespectFavouriteCapability (bool hasMy)
		{
		var shade = Create (hasMy: hasMy);
		shade.Open ();
		shade.Close ();
		shade.Stop ();
		shade.My ();
		Assert.That (_commands, Is.EqualTo (hasMy ? new[] { "open", "close", "stop", "my" } : new[] { "open", "close", "stop" }));
		Assert.That (shade.GetState ().PropertyValues["hasMy"].GetValue<bool> (), Is.EqualTo (hasMy));
		}
	[Test]
	public void PartialEvents_PreserveOtherStateAndIgnoreMalformedPositions ()
		{
		var shade = Create ();
		shade.UpdateState (75, true);
		shade.ApplyEventStates (new[] { new EventState { Name = "CORE:MOVINGSTATE", Value = "stopped" } });
		Assert.That (shade.OpenPercent, Is.EqualTo (25));
		Assert.That (shade.IsMoving, Is.False);
		shade.ApplyEventStates (new[] { new EventState { Name = "core:ClosureState", Value = "invalid" } });
		Assert.That (shade.OpenPercent, Is.EqualTo (25));
		shade.ApplyEventStates (new[] { new EventState { Name = "core:ClosureState", Value = 40 } });
		Assert.That (shade.OpenPercent, Is.EqualTo (60));
		Assert.That (shade.IsMoving, Is.False);
		Assert.That (shade.GetState ().PropertyValues["openPercent"].GetValue<long> (), Is.EqualTo (60));
		}
	[Test]
	public void OneWayShade_IgnoresPositionAndAvailabilityFeedbackButTracksExecution ()
		{
		var shade = Create (oneWay: true);
		shade.SetInitialOnlineState (true);
		shade.SetOpenPercent (50);
		shade.UpdateState (80, true);
		shade.UpdateAvailability (false);
		shade.ApplyEventStates (new[] { new EventState { Name = "core:ClosureState", Value = 90 } });
		Assert.That (_commands, Is.Empty);
		Assert.That (shade.OpenPercent, Is.EqualTo (100));
		Assert.That (shade.OnlineIndicatorIsOnline, Is.True);
		shade.SetMoving (true);
		Assert.That (shade.GetState ().PropertyValues["isMoving"].GetValue<bool> (), Is.True);
		shade.SetMoving (false);
		Assert.That (shade.IsMoving, Is.False);
		}
	[Test]
	public void InitialSnapshotAndAvailabilityRecovery_KeepBothIndicatorsConsistent ()
		{
		var shade = Create ();
		shade.SetInitialOnlineState (true);
		foreach (bool available in new[] { true, false, true })
			{
			shade.UpdateAvailability (available);
			var state = shade.GetState ();
			Assert.That (state.PropertyValues["onlineIndicator:isOnline"].GetValue<bool> (), Is.EqualTo (available));
			Assert.That (state.PropertyValues["readyIndicator:isReady"].GetValue<bool> (), Is.EqualTo (available));
			}
		}
	[Test]
	public void RemovingDisplayOverride_RestoresLatestApiLabel ()
		{
		var shade = Create ();
		shade.UpdateDisplayName ("Office");
		shade.UpdateLabel (" New API name ");
		Assert.That (shade.DeviceLabel, Is.EqualTo ("Office"));
		shade.UpdateDisplayName (null);
		Assert.That (shade.GetState ().PropertyValues["deviceLabel"].GetValue<string> (), Is.EqualTo ("New API name"));
		}
	}