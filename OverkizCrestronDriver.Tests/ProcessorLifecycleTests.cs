// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Linq;

using NUnit.Framework;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;

using OverKiz.CrestronDriver;

namespace OverkizCrestronDriver.Tests;

[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Processor")]
public sealed class ProcessorLifecycleTests
	{
	[SetUp]
	public void RequireProcessorRuntime ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the Crestron processor runtime; run the Processor Lifecycle suite on the processor.");
#endif
		}
	[Test]
	public void UnconfiguredDriver_CanBeCreatedDisposedAndCreatedAgain ()
		{
		using var logger = new DriverLogger ("processor-lifecycle-test");
		for (int iteration = 0; iteration < 2; iteration++)
			{
			var args = new DriverControllerCreationArgs ("processor-lifecycle-test", TestSupport.DataDirectory, logger.AppLogger, null);
			using var driver = new OverkizPlatformDriver (args, TestSupport.Resources (logger));
			Assert.That (driver.ConfigurationController, Is.Not.Null);
			var state = driver.GetState ();
			Assert.That (state.PropertyValues["onlineIndicator:isOnline"].GetValue<bool> (), Is.False);
			Assert.That (state.PropertyValues["readyIndicator:isReady"].GetValue<bool> (), Is.False);
			Assert.That (state.Definition.Properties.Keys, Does.Contain ("onlineIndicator:isOnline"));
			Assert.That (state.Definition.Properties.Keys, Does.Contain ("readyIndicator:isReady"));
			Assert.That (driver.GetState ().Definition.Properties.Keys.ToArray (), Is.EquivalentTo (state.Definition.Properties.Keys.ToArray ()));
			}
		}
	}