// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using NUnit.Framework;
using OverKiz.CrestronDriver;

namespace OverkizCrestronDriver.Tests;

// Authenticate with the existing local token and read discovery. Never create tokens or move shades.
[TestFixture, Category ("Processor"), Category ("Live"), NonParallelizable]
public sealed class LiveGatewayTests
	{
	private DriverLogger _logger;
	private OverkizPlatformDriver _driver;
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	[SetUp]
	public async Task ConnectDriver ()
		{
		var parameter = TestContext.Parameters.Get ("EnableLiveTests", "");
		if (parameter.Length != 0 && !bool.TryParse (parameter, out _)) throw new InvalidDataException ("EnableLiveTests must be true or false.");
		if (parameter.Equals ("false", StringComparison.OrdinalIgnoreCase)) Assert.Ignore ("Live tests are disabled for this run.");
		var directory = TestContext.Parameters.Get ("TestDataDirectory", "");
		var path = Path.Combine (string.IsNullOrWhiteSpace (directory) ? Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "OverkizClient") : directory, "LiveTestSettings.json");
		LiveSettings settings = null;
		if (File.Exists (path))
			{
			using var stream = File.OpenRead (path);
			settings = (LiveSettings)new DataContractJsonSerializer (typeof (LiveSettings)).ReadObject (stream);
			}
		if (!parameter.Equals ("true", StringComparison.OrdinalIgnoreCase) && settings?.Enabled != true)
			Assert.Ignore ("Live gateway tests require private settings and explicit enablement.");
		if (string.IsNullOrWhiteSpace (settings?.GatewayHost) || string.IsNullOrWhiteSpace (settings.Token))
			throw new InvalidDataException ("Enabled live tests require gatewayHost and an existing local token in LiveTestSettings.json.");
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null) Assert.Ignore ("Run live driver fixtures through the desktop SDK harness or processor runtime.");
#endif
		_logger = new DriverLogger ("overkiz-live-test");
		_driver = new OverkizPlatformDriver (new DriverControllerCreationArgs ("overkiz-live-test", TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		Set ("_gatewayIp", settings.GatewayHost);
		Set ("_localToken", settings.Token);
		await Connect ();
		}
	[TearDown]
	public void DisposeDriver () { _driver?.Dispose (); _logger?.Dispose (); }
	private void Set (string name, object value) => typeof (OverkizPlatformDriver).GetField (name, Private).SetValue (_driver, value);
	private Task Call (string name) => (Task)typeof (OverkizPlatformDriver).GetMethod (name, Private).Invoke (_driver, new object[] { CancellationToken.None });
	private Task Connect () => Call ("ConnectClientAsync");
	private Task Discover () => Call ("DiscoverDevicesAsync");
	private Dictionary<string, IOverkizEntity> Entities => (Dictionary<string, IOverkizEntity>)typeof (OverkizPlatformDriver).GetField ("_entities", Private).GetValue (_driver);
	[DataContract]
	private sealed class LiveSettings
		{
		[DataMember (Name = "enabled")] public bool Enabled { get; set; }
		[DataMember (Name = "gatewayHost")] public string GatewayHost { get; set; }
		[DataMember (Name = "token")] public string Token { get; set; }
		}

	[Test]
	public async Task RealGateway_DiscoveryPublishesSupportedDeviceEntities ()
		{
		await Discover ();
		Assert.That (Entities, Is.Not.Empty, "The selected gateway must contain at least one supported actuator for driver discovery tests.");
		foreach (var entity in Entities.Values)
			Assert.That (_driver.ManagedDevices.ContainsKey (entity.ControllerId), Is.True);
		Assert.That (_driver.ManagedDevices.Values.All (device => !string.IsNullOrWhiteSpace (device.Name)), Is.True);
		}

	[Test]
	public async Task RealGateway_RediscoveryRetainsPublishedControllerIdentities ()
		{
		await Discover ();
		var original = Entities.ToDictionary (pair => pair.Key, pair => pair.Value);
		Assert.That (original, Is.Not.Empty);
		await Discover ();
		Assert.That (Entities.Keys, Is.EquivalentTo (original.Keys));
		foreach (var pair in original) Assert.That (Entities[pair.Key], Is.SameAs (pair.Value));
		}

	[Test]
	public async Task RealGateway_ReconnectWithExistingTokenKeepsDiscoveredChildren ()
		{
		await Discover ();
		var original = Entities.Values.Select (entity => entity.ControllerId).ToArray ();
		Assert.That (original, Is.Not.Empty);
		typeof (OverkizPlatformDriver).GetMethod ("PrepareForReconnect", Private).Invoke (_driver, null);
		await Connect ();
		await Discover ();
		Assert.That (Entities.Values.Select (entity => entity.ControllerId), Is.EquivalentTo (original));
		}
	}
