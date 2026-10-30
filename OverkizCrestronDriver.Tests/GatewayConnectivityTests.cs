// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;

using NUnit.Framework;

using OverKiz.CrestronDriver;

using OverKizApi;
using OverKizApi.Exceptions;
using OverKizApi.Models;

namespace OverkizCrestronDriver.Tests;

// Actual maintained driver methods and typed client with synthetic HTTP only.
[TestFixture, Category ("Processor"), FixtureLifeCycle (LifeCycle.InstancePerTestCase)]
public sealed class GatewayConnectivityTests
	{
	const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	const string Rts = "rts://1234-5678-9012/1", Io = "io://1234-5678-9012/2", Other = "rts://9999-8888-7777/3";
	DriverLogger logger; OverkizPlatformDriver driver; Transport transport; HttpClient http; OverkizClient client;
	SemaphoreSlim polls, completed; OverkizRoomEntity room;
	static TaskCompletionSource<bool> Signal () => new (TaskCreationOptions.RunContinuationsAsynchronously);
	void Set (string name, object value) => typeof (OverkizPlatformDriver).GetField (name, Private).SetValue (driver, value);
	T Field<T> (string name) => (T)typeof (OverkizPlatformDriver).GetField (name, Private).GetValue (driver);
	Dictionary<string, IOverkizEntity> Entities => Field<Dictionary<string, IOverkizEntity>> ("_entities");
	OverkizShadeEntity Add (string url, bool oneWay)
		{
		var s = new OverkizShadeEntity ("s" + Entities.Count, url, url, null, oneWay, true,
	 _ => Assert.Fail ("No shade action permitted"), (_, __) => Assert.Fail ("No shade action permitted"), TestSupport.DataDirectory, logger.AppLogger, TestSupport.Resources (logger));
		Entities.Add (url, s);
		s.SetOnline (true);
		return s;
		}

	[SetUp]
	public void Setup ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null)
			Assert.Ignore ("Requires the SDK desktop harness or processor runtime.");
#endif
		logger = new DriverLogger ("gateway-connectivity");
		driver = new OverkizPlatformDriver (new DriverControllerCreationArgs ("gateway-connectivity", TestSupport.DataDirectory, logger.AppLogger, null), TestSupport.Resources (logger));
		transport = new Transport ();
		http = new HttpClient (transport);
		client = new OverkizClient ("synthetic", "synthetic", new OverkizServer { Name = "Test", Manufacturer = "Test", Endpoint = "https://gateway.invalid/api/" }, httpClient: http);
		Set ("_client", client);
		Set ("_clientHttp", http);
		Field<OverkizWorkQueue> ("_workQueue").SetClient (client);
		polls = new SemaphoreSlim (0);
		completed = new SemaphoreSlim (0);
		driver.EventDelay = (milliseconds, token) => { completed.Release (); return polls.WaitAsync (token); };
		}

	[TearDown]
	public async Task Cleanup ()
		{
		transport?.Release.TrySetResult (true);
		await TestSupport.DisposeDriverAsync (driver);
		room?.Dispose ();
		http?.Dispose ();
		logger?.Dispose ();
		polls?.Dispose ();
		completed?.Dispose ();
		if (transport != null) Assert.That (transport.Unexpected, Is.Empty);
		}
	Task Snapshot (params string[] ids) => driver.RefreshGatewayAvailabilityAsync (client, ids.Length == 0 ? null : ids, CancellationToken.None);
	void Failure (Exception e) => driver.RecordEventTransport (client, e, CancellationToken.None);
	void Success () => Failure (null);
	static string Alive (string id, string value) => "[{\"gatewayId\":\"" + id + "\",\"alive\":" + value + "}]";
	void AddRoom ()
		{
		room = new OverkizRoomEntity ("room", "Room", Array.Empty<RoomMemberConfig> (), Array.Empty<RoomMember> (), () => { }, () => { }, () => { }, () => { }, _ => { }, logger.GetComponentLogger ("test", "room"), logger.AppLogger, TestSupport.Resources (logger), TestSupport.DataDirectory);
		Field<Dictionary<string, OverkizRoomEntity>> ("_roomEntities").Add ("room", room);
		Field<ConcurrentDictionary<string, List<string>>> ("_roomToShades")["room"] = new () { Rts, Io };
		}


	[Test]
	public async Task InitialExplicitCloudGatewayFalseDisablesRtsAndOnlyItsDependants ()
		{
		var rts = Add (Rts, true);
		var other = Add (Other, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		Assert.That (other.OnlineIndicatorIsOnline, Is.True);
		Success ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False, "Cloud HTTP success is not physical gateway recovery.");
		transport.Gateways = Alive ("1234-5678-9012", "true");
		await Snapshot ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		}

	[TestCase ("[]")]

	[TestCase ("[{\"gatewayId\":\"1234-5678-9012\",\"alive\":null}]")]

	[TestCase ("[{\"gatewayId\":\"1234-5678-9012\",\"alive\":true},{\"gatewayId\":\"1234-5678-9012\",\"alive\":false}]")]
	public async Task MissingNullOrAmbiguousStatusCannotResurrectKnownOffline (string response)
		{
		var rts = Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		transport.Gateways = response;
		await Snapshot ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		}

	[Test]
	public async Task SnapshotForUnrelatedEventCannotChangeOtherGateway ()
		{
		var rts = Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ("9999-8888-7777");
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		}

	[Test]
	public async Task RecoveryPreservesTwoWayDeviceUnavailableAndRoomUnavailable ()
		{
		var rts = Add (Rts, true);
		var io = Add (Io, false);
		io.UpdateAvailability (false);
		AddRoom ();
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		transport.Gateways = Alive ("1234-5678-9012", "true");
		await Snapshot ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Assert.That (io.OnlineIndicatorIsOnline, Is.False);
		Assert.That (room.OnlineIndicatorIsOnline, Is.False);
		typeof (OverkizPlatformDriver).GetMethod ("HandleDeviceAvailabilityChanged", Private).Invoke (driver, new object[] { new EventObject { DeviceUrl = Io, SubType = 1 } });
		Assert.That (room.OnlineIndicatorIsOnline, Is.True);
		}

	[Test]
	public void TwoConfirmedTransportFailuresDisableAndSuccessRestoresWithoutBlindFeedback ()
		{
		var rts = Add (Rts, true);
		var io = Add (Io, false);
		io.UpdateAvailability (false);
		Failure (new HttpRequestException ("network", new IOException ("connection reset")));
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Failure (new OperationCanceledException ("HTTP timeout"));
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		Success ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Assert.That (io.OnlineIndicatorIsOnline, Is.False);
		}

	[TestCase (0)]

	[TestCase (1)]

	[TestCase (2)]
	public void ApplicationOrSecurityFailureDoesNotCountAsTransport (int kind)
		{
		var rts = Add (Rts, true);
		Failure (new OperationCanceledException ("one timeout"));
		Failure (kind == 0 ? new TooManyRequestsException ("quota") : kind == 1 ? new OverkizException ("authentication rejected") : new HttpRequestException ("TLS", new System.Security.Authentication.AuthenticationException ("certificate")));
		Failure (new OperationCanceledException ("one timeout after application failure"));
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		}

	[Test]
	public void CancelledOrStaleFailureDoesNotChangeCurrentState ()
		{
		var rts = Add (Rts, true);
		using var c = new CancellationTokenSource ();
		c.Cancel ();
		driver.RecordEventTransport (client, new OperationCanceledException (), c.Token);
		driver.RecordEventTransport (client, new OperationCanceledException (), c.Token);
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Set ("_client", null);
		Failure (new OperationCanceledException ());
		Failure (new OperationCanceledException ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Set ("_client", client);
		}

	[TestCase (false)]

	[TestCase (true)]
	public async Task LateSnapshotAfterReplacementOrDisposalCannotPublish (bool dispose)
		{
		var rts = Add (Rts, true);
		transport.Hold = true;
		transport.Gateways = Alive ("1234-5678-9012", "false");
		var task = Snapshot ();
		await TestSupport.Complete (transport.Entered.Task);
		if (dispose)
			driver.Dispose ();
		else
			Set ("_client", null);
		transport.Release.TrySetResult (true);
		await TestSupport.Complete (task);
		if (dispose)
			Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		else
			{
			Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
			Set ("_client", client);
			}
		}

	[Test]
	public async Task LiveReadOnlyCaseUsesActualInitialSnapshotAndLoop ()
		{
		await UseLocalClient ();
		Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "true");
		var fixture = new LiveGatewayTests ();
		var lifetime = new LiveTestLifetime (TimeSpan.FromSeconds (5));
		typeof (LiveGatewayTests).GetField ("_driver", Private).SetValue (fixture, driver);
		typeof (LiveGatewayTests).GetField ("_lifetime", Private).SetValue (fixture, lifetime);
		polls.Release ();
		try
			{
			await TestSupport.Complete (fixture.RealGateway_LocalEventLoopConfirmsAuthenticatedReachability ());
			Assert.That (transport.GatewayReads, Is.Zero, "Local reachability uses actual authenticated event fetch, without cloud Alive reads.");
			}
		finally { await lifetime.CleanupAsync (() => Task.CompletedTask, () => { }); }
		}

	[Test]
	public async Task LiveLocalCaseRejectsErrorBackoffInsteadOfClaimingSuccess ()
		{
		await UseLocalClient ();
		Add (Rts, true);
		transport.Failures = 1;
		var fixture = new LiveGatewayTests ();
		var lifetime = new LiveTestLifetime (TimeSpan.FromSeconds (5));
		typeof (LiveGatewayTests).GetField ("_driver", Private).SetValue (fixture, driver);
		typeof (LiveGatewayTests).GetField ("_lifetime", Private).SetValue (fixture, lifetime);
		polls.Release ();
		try
			{
			await Assert.ThatAsync (async () => await fixture.RealGateway_LocalEventLoopConfirmsAuthenticatedReachability (), Throws.InstanceOf<InvalidOperationException> ());
			Assert.That (transport.GatewayReads, Is.Zero);
			}
		finally { await lifetime.CleanupAsync (() => Task.CompletedTask, () => { }); }
		}

	async Task UseLocalClient ()
		{
		await client.DisposeAsync ();
		client = new OverkizClient ("", "", OverkizConst.LocalServer ("192.0.2.1"), "synthetic-local-token", http);
		Set ("_client", client);
		Field<OverkizWorkQueue> ("_workQueue").SetClient (client);
		}

	[Test]
	public async Task LocalSuccessIgnoresStaleAliveMetadataAndPreservesTwoWayUnavailable ()
		{
		await UseLocalClient ();
		var rts = Add (Rts, true);
		var twoWay = Add (Io, false);
		twoWay.UpdateAvailability (false);
		Field<Dictionary<string, bool>> ("_gatewayAvailability")["1234-5678-9012"] = false;
		Set ("_gatewayIp", ""); Set ("_localToken", ""); // Mutable future settings do not change this client's local mode.
		await Snapshot ();
		Failure (new OperationCanceledException ()); Failure (new OperationCanceledException ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		Success ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Assert.That (twoWay.OnlineIndicatorIsOnline, Is.False);
		Assert.That (transport.GatewayReads, Is.Zero);
		}

	[Test]
	public async Task CloudClientStillHonorsAliveWhenFutureSettingsSelectLocal ()
		{
		var rts = Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		Set ("_gatewayIp", "192.0.2.1"); Set ("_localToken", "future-local-token");
		Failure (new OperationCanceledException ()); Failure (new OperationCanceledException ()); Success ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False, "Cloud success cannot override explicit physical-gateway false.");
		}

	[Test]
	public async Task LateCloudSnapshotCannotDisableReplacementLocalClient ()
		{
		var rts = Add (Rts, true);
		transport.Hold = true; transport.Gateways = Alive ("1234-5678-9012", "false");
		var pending = Snapshot ();
		await TestSupport.Complete (transport.Entered.Task);
		var previous = client;
		var previousHttp = http;
		http = new HttpClient (transport, false);
		Set ("_clientHttp", http);
		client = new OverkizClient ("", "", OverkizConst.LocalServer ("192.0.2.1"), "synthetic-local-token", http);
		Set ("_client", client); Field<OverkizWorkQueue> ("_workQueue").SetClient (client);
		transport.Release.TrySetResult (true);
		await TestSupport.Complete (pending);
		Assert.That (Field<Dictionary<string, bool>> ("_gatewayAvailability"), Is.Empty);
		Success ();
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		await previous.DisposeAsync ();
		previousHttp.Dispose ();
		}

	[Test]
	public async Task ActualLoopInitialSnapshotAndGatewayEventUseOrderedTypedReads ()
		{
		var rts = Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		typeof (OverkizPlatformDriver).GetMethod ("StartEventLoop", Private).Invoke (driver, null);
		await TestSupport.Complete (completed.WaitAsync ());
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		Assert.That (transport.GatewayReads, Is.EqualTo (1));
		transport.Fetch = "[{\"name\":\"GatewayAliveChangedEvent\",\"gatewayId\":\"1234-5678-9012\"}]";
		transport.Gateways = Alive ("1234-5678-9012", "true");
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		Assert.That (transport.GatewayReads, Is.EqualTo (2));
		transport.Fetch = "[]";
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (transport.GatewayReads, Is.EqualTo (2), "No added periodic gateway snapshot polling.");
		}

	[Test]
	public async Task CancelledSnapshotDoesNotIssueGet ()
		{
		using var c = new CancellationTokenSource ();
		c.Cancel ();
		await driver.RefreshGatewayAvailabilityAsync (client, null, c.Token);
		Assert.That (transport.GatewayReads, Is.Zero);
		}

	[Test]
	public async Task RtsDeviceAvailabilityCannotOverrideLostGateway ()
		{
		var rts = Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		rts.UpdateAvailability (true);
		rts.UpdateAvailability (false);
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		}


	[TestCase (false)]

	[TestCase (true)]
	public async Task ActualLoopConfirmedTransportLossRecoversAndRefreshesMissedGatewayEvent (bool local)
		{
		if (local)
			{
			await client.DisposeAsync ();
			client = new OverkizClient ("", "", OverkizConst.LocalServer ("192.0.2.1"), "synthetic-local-token", http);
			Set ("_client", client);
			Field<OverkizWorkQueue> ("_workQueue").SetClient (client);
			}
		var rts = Add (Rts, true);
		var other = Add (Other, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		typeof (OverkizPlatformDriver).GetMethod ("StartEventLoop", Private).Invoke (driver, null);
		await TestSupport.Complete (completed.WaitAsync ());
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.EqualTo (local));
		Assert.That (other.OnlineIndicatorIsOnline, Is.True);
		transport.Failures = 2;
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (other.OnlineIndicatorIsOnline, Is.True, "Single timeout is not confirmed loss.");
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (Field<bool> ("_apiReachable"), Is.False);
		Assert.That (other.OnlineIndicatorIsOnline, Is.False);
		transport.Gateways = Alive ("1234-5678-9012", "true");
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True, "Recovery snapshot repairs an AliveChanged event missed during outage.");
		Assert.That (other.OnlineIndicatorIsOnline, Is.True);
		Assert.That (transport.GatewayReads, Is.EqualTo (local ? 0 : 2));
		}

	[Test]
	public async Task RestartPollingAndRoomRemappingReapplyRetainedGatewayState ()
		{
		var rts = Add (Rts, true);
		Add (Io, false);
		var other = Add (Other, true);
		AddRoom ();
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		typeof (OverkizPlatformDriver).GetMethod ("StartAllChildPolling", Private).Invoke (driver, null);
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		Assert.That (room.OnlineIndicatorIsOnline, Is.False);
		Field<ConcurrentDictionary<string, List<string>>> ("_roomToShades")["room"] = new () { Other };
		typeof (OverkizPlatformDriver).GetMethod ("ReapplyGatewayConnectivity", Private).Invoke (driver, null);
		Assert.That (room.OnlineIndicatorIsOnline, Is.True);
		Field<ConcurrentDictionary<string, List<string>>> ("_roomToShades")["room"] = new () { Other, Rts };
		typeof (OverkizPlatformDriver).GetMethod ("ReapplyGatewayConnectivity", Private).Invoke (driver, null);
		Assert.That (room.OnlineIndicatorIsOnline, Is.False);
		}

	[Test]
	public async Task DiscoveryAndDeviceCreatedApplyAlreadyKnownGatewayFalse ()
		{
		transport.Gateways = Alive ("1234-5678-9012", "false");
		await Snapshot ();
		transport.Devices = "[{\"deviceURL\":\"" + Rts + "\",\"label\":\"Office\",\"type\":\"ACTUATOR\",\"definition\":{\"uiClass\":\"Screen\",\"commands\":[]}}]";
		await TestSupport.Complete ((Task)typeof (OverkizPlatformDriver).GetMethod ("DiscoverDevicesAsync", Private).Invoke (driver, new object[] { CancellationToken.None }));
		Assert.That (((OverkizShadeEntity)Entities[Rts]).OnlineIndicatorIsOnline, Is.False);
		var next = "rts://1234-5678-9012/4";
		transport.Devices = transport.Devices.Replace (Rts, next);
		await TestSupport.Complete ((Task)typeof (OverkizPlatformDriver).GetMethod ("HandleDeviceCreatedAsync", Private).Invoke (driver, new object[] { new EventObject { DeviceUrl = next } }));
		Assert.That (((OverkizShadeEntity)Entities[next]).OnlineIndicatorIsOnline, Is.False);
		}

	[Test]
	public void StableSuccessDoesNotRepublishEachPoll ()
		{
		var counting = new CountingEntity ();
		Entities.Add (Rts, counting);
		Success ();
		Success ();
		Assert.That (counting.Publications, Is.Zero);
		Failure (new OperationCanceledException ());
		Assert.That (counting.Publications, Is.Zero);
		Failure (new OperationCanceledException ());
		Assert.That (counting.Publications, Is.EqualTo (1));
		Success ();
		Success ();
		Assert.That (counting.Publications, Is.EqualTo (2));
		}

	[Test]
	public async Task StopWhilePublisherWaitsForEntityLockCannotResurrectChild ()
		{
		var rts = Add (Rts, true);
		Failure (new OperationCanceledException ());
		Failure (new OperationCanceledException ());
		Task publish;
		var entityLock = Field<object> ("_entitiesLock");
		var clientLock = Field<object> ("_clientLock");
		lock (entityLock)
			{
			publish = Task.Run (() => Success ());
			Assert.That (SpinWait.SpinUntil (() => { if (Monitor.TryEnter (clientLock)) { Monitor.Exit (clientLock); return false; } return true; }, 5000), Is.True, "Publisher did not reach client-to-entity boundary.");
			typeof (OverkizPlatformDriver).GetMethod ("StopEventLoop", Private).Invoke (driver, null);
			rts.StopPolling ();
			}
		await TestSupport.Complete (publish);
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False, "Stopped lifecycle must win over an already-validated publisher.");
		}

	[Test]
	public async Task TransparentListenerRolloverRefreshesGatewayWithoutAliveEvent ()
		{
		var rts = Add (Rts, true);
		transport.Gateways = Alive ("1234-5678-9012", "false");
		typeof (OverkizPlatformDriver).GetMethod ("StartEventLoop", Private).Invoke (driver, null);
		await TestSupport.Complete (completed.WaitAsync ());
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (rts.OnlineIndicatorIsOnline, Is.False);
		transport.Gateways = Alive ("1234-5678-9012", "true");
		transport.RejectListenerOnce = true;
		polls.Release ();
		await TestSupport.Complete (completed.WaitAsync ());
		Assert.That (transport.Registers, Is.EqualTo (2));
		Assert.That (transport.Fetches, Is.EqualTo (3));
		Assert.That (transport.GatewayReads, Is.EqualTo (2));
		Assert.That (rts.OnlineIndicatorIsOnline, Is.True);
		}

	[Test]
	public async Task StopSerializesWithDelayedEnableAndRejectsOldGeneration ()
		{
		var source = new CancellationTokenSource ();
		Set ("_eventCts", source);
		var gate = Field<object> ("_eventLock");
		Task<bool> enable;
		lock (gate)
			{
			enable = Task.Run (() => driver.EnableEventConnectivity (source));
			typeof (OverkizPlatformDriver).GetMethod ("StopEventLoop", Private).Invoke (driver, null);
			}
		await TestSupport.Complete (enable);
		Assert.That (enable.Result, Is.False);
		Assert.That (Field<bool> ("_connectivityStopped"), Is.True);
		using var replacement = new CancellationTokenSource ();
		Set ("_eventCts", replacement);
		Assert.That (driver.EnableEventConnectivity (source), Is.False);
		Assert.That (driver.EnableEventConnectivity (replacement), Is.True);
		Set ("_eventCts", null);
		}
	sealed class CountingEntity : IOverkizEntity
		{
		internal int Publications; public string ControllerId => "count"; public DeviceUxCategory UxCategory => DeviceUxCategory.Shade; public void SetOnline (bool value)
			{
			Publications++;
			}
		public void StartPolling (OverkizWorkQueue q)
			{
			}
		public void StopPolling ()
			{
			}
		public void UpdateAvailability (bool v)
			{
			}
		public void ApplyEventStates (IReadOnlyList<EventState> s)
			{
			}
		public void SetMoving (bool v)
			{
			}
		public void UpdateLabel (string v)
			{
			}
		}
	sealed class Transport : HttpMessageHandler
		{
		internal string Gateways = "[]", Fetch = "[]", Devices = "[]"; internal int GatewayReads, Failures, Registers, Fetches; internal bool Hold, RejectListenerOnce; internal readonly List<string> Unexpected = new (); internal readonly TaskCompletionSource<bool> Entered = Signal (), Release = Signal ();
		protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage r, CancellationToken token)
			{
			var p = r.RequestUri.AbsolutePath;
			string body;
			if (p.EndsWith ("/setup/gateways"))
				{
				GatewayReads++;
				Entered.TrySetResult (true);
				if (Hold)
					await Release.Task;
				body = Gateways;
				}
			else if (p.EndsWith ("/setup/devices"))
				body = Devices;
			else if (p.EndsWith ("/events/register"))
				body = "{\"id\":\"listener" + (++Registers) + "\"}";
			else if (p.EndsWith ("/fetch"))
				{
				Fetches++;
				if (RejectListenerOnce)
					{
					RejectListenerOnce = false;
					return new HttpResponseMessage (HttpStatusCode.BadRequest) { Content = new StringContent ("{\"error\":\"Invalid event listener id listener1\"}") };
					}
				if (Failures > 0)
					{
					Failures--;
					throw new OperationCanceledException ("synthetic transport timeout");
					}
				body = Fetch;
				}
			else if (p.EndsWith ("/unregister"))
				body = "{}";
			else
				{
				Unexpected.Add (p);
				throw new InvalidOperationException (p);
				}
			return new HttpResponseMessage (HttpStatusCode.OK) { Content = new StringContent (body) };
			}
		}
	}
