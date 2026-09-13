// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

using NUnit.Framework;

using OverKiz.CrestronDriver;
using OverKizApi;
using OverKizApi.Models;

namespace OverkizCrestronDriver.Tests;

[TestFixture, Category ("Processor")]
public sealed class PlatformDiscoveryTests
	{
	private DriverLogger _logger;
	private OverkizPlatformDriver _driver;
	private DiscoveryTransport _transport;
	private HttpClient _http;
	private const string Url = "io://test/shade#1";
	private const string Shade = "{\"deviceURL\":\"io://test/shade#1\",\"label\":\"Office\",\"type\":\"ACTUATOR\",\"definition\":{\"uiClass\":\"RollerShutter\",\"commands\":[]}}";
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or the processor runtime.");
#endif
		_logger = new DriverLogger ("overkiz-discovery-test");
		_driver = new OverkizPlatformDriver (new DriverControllerCreationArgs ("overkiz-discovery-test", TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		_transport = new DiscoveryTransport ();
		_http = new HttpClient (_transport);
		Set ("_client", new OverkizClient ("test@example.invalid", "synthetic-password", new OverkizServer { Name = "Test", Endpoint = "https://gateway.invalid/api/", Manufacturer = "Test" }, "synthetic-token", _http));
		}
	[TearDown]
	public void TearDown ()
		{
		_transport?.Release.TrySetResult (true);
		_driver?.Dispose ();
		_http?.Dispose ();
		_logger?.Dispose ();
		}
	private void Set (string name, object value) => typeof (OverkizPlatformDriver).GetField (name, Private).SetValue (_driver, value);
	private T Field<T> (string name) => (T)typeof (OverkizPlatformDriver).GetField (name, Private).GetValue (_driver);
	private object Call (string name, params object[] args) => typeof (OverkizPlatformDriver).GetMethod (name, Private).Invoke (_driver, args);
	private Dictionary<string, IOverkizEntity> Entities => Field<Dictionary<string, IOverkizEntity>> ("_entities");
	private Task Discover (CancellationToken token = default) => (Task)Call ("DiscoverDevicesAsync", token);
	private void Clear () => Call ("ApplyConfigurationItems", DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues, null, null);
	[Test]
	public async Task DiscoveryFiltersUnsupportedDevicesAndPublishesStableControllerId ()
		{
		_transport.Devices = "[" + Shade + "," + Shade.Replace (Url, "internal://test/ignored") + "," + Shade.Replace (Url, "zigbee://test/ignored") + "," + Shade.Replace ("ACTUATOR", "SENSOR").Replace (Url, "io://test/sensor") + "]";
		await TestSupport.Complete (Discover ());
		Assert.That (Entities.Keys, Is.EquivalentTo (new[] { Url }));
		Assert.That (_driver.ManagedDevices.Keys.Single (), Is.EqualTo (Entities[Url].ControllerId));
		Assert.That (_driver.ManagedDevices.Values.Single ().Name, Is.EqualTo ("Office"));
		}
	[Test]
	public async Task RediscoveryAndReconnectPreserveExistingChildIdentity ()
		{
		_transport.Devices = "[" + Shade + "]";
		await TestSupport.Complete (Discover ());
		var original = Entities[Url];
		await TestSupport.Complete (Discover ());
		Call ("PrepareForReconnect");
		Assert.That (Entities[Url], Is.SameAs (original));
		Assert.That (_driver.ManagedDevices.Count, Is.EqualTo (1));
		}
	[Test]
	public async Task RenameAndDeleteEventsUpdatePublishedChildrenWithoutDuplicates ()
		{
		_transport.Devices = "[" + Shade + "]";
		await TestSupport.Complete (Discover ());
		var original = Entities[Url];
		Call ("HandleDeviceUpdated", new EventObject { DeviceUrl = Url, Label = "Study" });
		Assert.That (Entities[Url], Is.SameAs (original));
		Assert.That (_driver.ManagedDevices.Values.Single ().Name, Is.EqualTo ("Study"));
		Call ("HandleDeviceDeleted", new EventObject { DeviceUrl = Url });
		Call ("HandleDeviceDeleted", new EventObject { DeviceUrl = Url });
		Assert.That (Entities, Is.Empty);
		Assert.That (_driver.ManagedDevices, Is.Empty);
		}
	[Test]
	public async Task ClearRemovesChildrenAndPrivateConnectionValues ()
		{
		_transport.Devices = "[" + Shade + "]";
		await TestSupport.Complete (Discover ());
		foreach (string field in new[] { "_cloudUsername", "_cloudPassword", "_gatewayIp", "_localToken" })
			Set (field, "synthetic");
		Clear ();
		Assert.Multiple (() =>
			{
			Assert.That (Entities, Is.Empty);
			Assert.That (_driver.ManagedDevices, Is.Empty);
			foreach (string field in new[] { "_cloudUsername", "_cloudPassword", "_gatewayIp", "_localToken" })
				Assert.That (Field<string> (field), Is.Null.Or.Empty, field);
			});
		}
	[TestCase (false)]
	[TestCase (true)]
	public async Task LateDiscoveryCannotPublishAfterClearOrDispose (bool dispose)
		{
		_transport.Devices = "[" + Shade + "]";
		_transport.Hold = true;
		var discover = Discover ();
		try
			{
			await TestSupport.Complete (_transport.Entered.Task);
			if (dispose) _driver.Dispose (); else Clear ();
			}
		finally { _transport.Release.TrySetResult (true); }
		await TestSupport.Complete (discover);
		Assert.That (Entities, Is.Empty);
		Assert.That (_driver.ManagedDevices, Is.Empty);
		}
	[Test]
	public async Task ConfiguredRoomRestoresMembershipAndSurvivesRediscovery ()
		{
		Set ("_roomGroups", TestSupport.Call<Dictionary<string, RoomGroupEntry>> (typeof (OverkizPlatformDriver), "ParseRoomGroups", "Room:Lounge=Office"));
		_transport.Devices = "[" + Shade + "]";
		await TestSupport.Complete (Discover ());
		var rooms = Field<Dictionary<string, OverkizRoomEntity>> ("_roomEntities");
		var room = rooms["Room"];
		Assert.That (_driver.GetRoomShadeCount ("Room"), Is.EqualTo (1));
		Assert.That (room.DeviceLabel, Is.EqualTo ("Lounge"));
		await TestSupport.Complete (Discover ());
		Assert.That (rooms["Room"], Is.SameAs (room));
		Assert.That (_driver.ManagedDevices.Count, Is.EqualTo (2));
		Call ("HandleDeviceDeleted", new EventObject { DeviceUrl = Url });
		Assert.That (rooms, Is.Empty);
		Assert.That (_driver.ManagedDevices, Is.Empty);
		}
	[TestCase (false)]
	[TestCase (true)]
	public async Task LateLoginCannotRestoreClientAfterClearOrDispose (bool dispose)
		{
		_transport.HoldLogin = true;
		_driver.ClientFactory = () => new OverkizClient ("test@example.invalid", "synthetic-password", new OverkizServer { Name = "Test", Endpoint = "https://gateway.invalid/api/", Manufacturer = "Test" }, null, _http);
		using var cancellation = new CancellationTokenSource ();
		Set ("_connectCts", cancellation);
		var connect = (Task)Call ("ConnectClientAsync", cancellation.Token);
		try
			{
			await TestSupport.Complete (_transport.Entered.Task);
			if (dispose) _driver.Dispose (); else Clear ();
			}
		finally { _transport.Release.TrySetResult (true); }
		await Assert.ThatAsync (async () => await TestSupport.Complete (connect), Throws.InstanceOf<OperationCanceledException> ());
		Assert.That (Field<OverkizClient> ("_client"), Is.Null);
		}

	[Test]
	public async Task ReconnectAfterHttpRequestCreatesUsableTransport ()
		{
		_driver.HttpClientFactory = () => new HttpClient (_transport, false);
		Set ("_cloudUsername", "test@example.invalid");
		Set ("_cloudPassword", "synthetic-password");
		Set ("_gatewayIp", "gateway.invalid");
		Set ("_localToken", "synthetic-token");
		Call ("PrepareForReconnect");
		await TestSupport.Complete ((Task)Call ("ConnectClientAsync", CancellationToken.None));
		await TestSupport.Complete (Discover ());
		var first = Field<OverkizClient> ("_client");
		Call ("PrepareForReconnect");
		await TestSupport.Complete ((Task)Call ("ConnectClientAsync", CancellationToken.None));
		await TestSupport.Complete (Discover ());
		Assert.That (Field<OverkizClient> ("_client"), Is.Not.SameAs (first));
		}

	private sealed class DiscoveryTransport : HttpMessageHandler
		{
		internal string Devices = "[]";
		internal bool Hold;
		internal bool HoldLogin;
		internal readonly TaskCompletionSource<bool> Entered = new (TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly TaskCompletionSource<bool> Release = new (TaskCreationOptions.RunContinuationsAsynchronously);
		protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
			{
			string path = request.RequestUri.AbsolutePath;
			if (path.EndsWith ("login"))
				{
				Assert.That (request.Method, Is.EqualTo (HttpMethod.Post));
				if (HoldLogin)
					{
					Entered.TrySetResult (true);
					await Release.Task;
					}
				return new HttpResponseMessage (HttpStatusCode.OK) { Content = new StringContent ("{\"success\":true}") };
				}
			if (path.EndsWith ("events/register") || path.EndsWith ("events/test/unregister"))
				{
				Assert.That (request.Method, Is.EqualTo (HttpMethod.Post));
				return new HttpResponseMessage (HttpStatusCode.OK) { Content = new StringContent ("{\"id\":\"test\"}") };
				}
			Assert.That (request.Method, Is.EqualTo (HttpMethod.Get));
			Assert.That (request.RequestUri.AbsolutePath, Does.EndWith ("setup/devices"));
			if (Hold)
				{
				Entered.TrySetResult (true);
				await Release.Task;
				}
			return new HttpResponseMessage (HttpStatusCode.OK) { Content = new StringContent (Devices) };
			}
		}
	}