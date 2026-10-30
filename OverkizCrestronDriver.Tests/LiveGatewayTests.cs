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
using OverKizApi;
using OverKiz.CrestronDriver;

namespace OverkizCrestronDriver.Tests;

// Authenticate with the existing local token and read discovery. Never create tokens or move shades.
[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Processor"), Category ("Live"), NonParallelizable]
public sealed class LiveGatewayTests
	{
	private DriverLogger _logger;
	private LiveTestLifetime _lifetime;
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
		_lifetime = new LiveTestLifetime (TimeSpan.FromSeconds (60));
		_logger = new DriverLogger ("overkiz-live-test");
		_driver = new OverkizPlatformDriver (new DriverControllerCreationArgs ("overkiz-live-test", TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		Set ("_gatewayIp", settings.GatewayHost);
		Set ("_localToken", settings.Token);
		await Connect ();
		}
	[TearDown]
	public async Task DisposeDriver ()
		{
		if (_lifetime != null)
			await _lifetime.CleanupAsync (() => TestSupport.DisposeDriverAsync (_driver), () => _logger?.Dispose ());
		else
			{
			try { await TestSupport.DisposeDriverAsync (_driver); }
			finally { _logger?.Dispose (); }
			}
		}
	private void Set (string name, object value) => typeof (OverkizPlatformDriver).GetField (name, Private).SetValue (_driver, value);
	private Task Call (string name) => _lifetime.InvokeAsync (token =>
		(Task)typeof (OverkizPlatformDriver).GetMethod (name, Private).Invoke (_driver, new object[] { token }));
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
	public async Task RealGateway_LocalEventLoopConfirmsAuthenticatedReachability ()
		{
		await Discover ();
		Assert.That (Entities, Is.Not.Empty, "An actual supported child is required.");
		var client = (OverkizClient)typeof (OverkizPlatformDriver).GetField ("_client", Private).GetValue (_driver);
		Assert.That (client.ApiType, Is.EqualTo (OverKizApi.Enums.APIType.Local));
		var completed = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var originalDelay = _driver.EventDelay;
		var normalDelays = 0;
		var startedUtc = DateTimeOffset.UtcNow;
		_driver.EventDelay = (milliseconds, token) =>
			{
			if (milliseconds == 10000)
				completed.TrySetException (new InvalidOperationException ("The first authenticated local event iteration entered error backoff."));
			if (milliseconds == 2000 && Interlocked.Increment (ref normalDelays) == 2)
				completed.TrySetResult (true);
			return originalDelay (milliseconds, token); // Observe, never accelerate, existing traffic.
			};
		try
			{
			typeof (OverkizPlatformDriver).GetMethod ("StartEventLoop", Private).Invoke (_driver, null);
			await _lifetime.InvokeAsync (async token =>
				{
				while (!completed.Task.IsCompleted) await Task.Delay (100, token);
				await completed.Task;
				});
			lock (typeof (OverkizPlatformDriver).GetField ("_clientLock", Private).GetValue (_driver))
				{
				Assert.That (typeof (OverkizPlatformDriver).GetField ("_client", Private).GetValue (_driver), Is.SameAs (client));
				Assert.That ((bool)typeof (OverkizPlatformDriver).GetField ("_apiReachable", Private).GetValue (_driver), Is.True);
				Assert.That (client.EventListenerId, Is.Not.Null.And.Not.Empty);
				Assert.That ((Dictionary<string, bool>)typeof (OverkizPlatformDriver).GetField ("_gatewayAvailability", Private).GetValue (_driver), Is.Empty, "Direct-local mode does not require cloud Alive metadata.");
				}
			TestContext.Out.WriteLine ("Local authenticated event fetch completed; gateway path reachable; no Alive field required. StartedUtc=" + startedUtc.ToString ("O") + "; CompletedUtc=" + DateTimeOffset.UtcNow.ToString ("O") + "; no physical outage or shade command exercised.");
			}
		finally { _driver.EventDelay = originalDelay; }
		}

	[Test]
	public async Task RealGateway_StaleLocalListenerIsReplacedWithoutPowerCycle ()
		{
		var client = (OverkizClient)typeof (OverkizPlatformDriver).GetField ("_client", Private).GetValue (_driver);
		Assert.That (client.ApiType, Is.EqualTo (OverKizApi.Enums.APIType.Local));
		var originalListener = client.EventListenerId;
		Assert.That (originalListener, Is.Not.Null.And.Not.Empty);
		await _lifetime.InvokeAsync (async token =>
			{
			await client.UnregisterEventListener ();
			token.ThrowIfCancellationRequested ();
			// Model only this fixture's successfully unregistered listener in its own fresh client.
			// The installed driver's client and listener are never inspected or modified.
			typeof (OverkizClient).GetProperty ("EventListenerId").SetValue (client, originalListener);
			await Assert.ThatAsync (() => client.FetchEvents (false),
				Throws.TypeOf<OverKizApi.Exceptions.NoRegisteredEventListenerException> ());
			Assert.That (client.EventListenerId, Is.Null);
			token.ThrowIfCancellationRequested ();
			await client.FetchEvents (true);
			Assert.That (client.EventListenerId, Is.Not.Null.And.Not.Empty.And.Not.EqualTo (originalListener));
			});
		TestContext.Out.WriteLine ("Confirmed this fixture's own unregistered local listener is rejected with the typed exception and replaced by existing bounded automatic registration. No power cycle, installed-client change or shade command.");
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
