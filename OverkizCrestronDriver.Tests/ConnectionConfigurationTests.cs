// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Reflection;
using NUnit.Framework;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using OverKiz.CrestronDriver;

namespace OverkizCrestronDriver.Tests;

[TestFixture, Category ("Processor")]
public sealed class ConnectionConfigurationTests
	{
	private DriverLogger _logger;
	private OverkizPlatformDriver _driver;
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or processor runtime.");
#endif
		_logger = new DriverLogger ("configuration-validation-test");
		_driver = new OverkizPlatformDriver (new DriverControllerCreationArgs ("configuration-validation-test",
			TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		}
	[TearDown]
	public void TearDown ()
		{
		try { _driver?.Dispose (); }
		finally { _logger?.Dispose (); }
		}
	[TestCase ("gateway.invalid", "synthetic-token", null, null, true, true)]
	[TestCase ("gateway.invalid", "synthetic-token", "synthetic-user", "synthetic-password", true, true)]
	[TestCase ("gateway.invalid", null, "synthetic-user", "synthetic-password", false, false)]
	[TestCase (null, "synthetic-token", "synthetic-user", "synthetic-password", false, false)]
	[TestCase (null, null, "synthetic-user", "synthetic-password", true, false)]
	[TestCase (null, null, "synthetic-user", null, false, false)]
	[TestCase (null, null, null, "synthetic-password", false, false)]
	[TestCase (" ", " ", null, null, false, false)]
	public void ConnectionValidationRequiresCompleteSelectedModeWithoutCloudFallback (
		string host, string token, string username, string password, bool valid, bool local)
		{
		Set ("_gatewayIp", host); Set ("_localToken", token);
		Set ("_cloudUsername", username); Set ("_cloudPassword", password);
		var errors = typeof (OverkizPlatformDriver).GetMethod ("ValidateConnectionConfiguration", Private).Invoke (_driver, null);
		Assert.That (errors == null, Is.EqualTo (valid));
		Assert.That ((bool)typeof (OverkizPlatformDriver).GetProperty ("IsLocalMode", Private).GetValue (_driver), Is.EqualTo (local));
		// Validation itself must not authenticate or publish a client.
		Assert.That (typeof (OverkizPlatformDriver).GetField ("_client", Private).GetValue (_driver), Is.Null);
		}
	private void Set (string name, string value) => typeof (OverkizPlatformDriver).GetField (name, Private).SetValue (_driver, value);
	}
