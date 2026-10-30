// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
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
using OverKizApi.Models;

namespace OverkizCrestronDriver.Tests;

[TestFixture, Category ("Processor")]
public sealed class DriverEventRecoveryTests
	{
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private DriverLogger _logger;
	private OverkizPlatformDriver _driver;
	private EventTransport _transport;
	private HttpClient _http;
	private OverkizClient _client;
	private SemaphoreSlim _poll;
	private TaskCompletionSource<bool> _retry;
	private static TaskCompletionSource<bool> Signal () => new (TaskCreationOptions.RunContinuationsAsynchronously);

	[SetUp]
	public void SetUp ()
		{
#if NETFRAMEWORK
		if (Type.GetType ("Mono.Runtime") == null) Assert.Ignore ("Requires the SDK desktop harness or the processor runtime.");
#endif
		_poll = new SemaphoreSlim (0);
		_retry = Signal ();
		_logger = new DriverLogger ("event-recovery-test");
		_driver = new OverkizPlatformDriver (new DriverControllerCreationArgs ("event-recovery-test", TestSupport.DataDirectory, _logger.AppLogger, null), TestSupport.Resources (_logger));
		_transport = new EventTransport ();
		_http = new HttpClient (_transport);
		_client = new OverkizClient ("test@example.invalid", "synthetic-password", new OverkizServer { Name = "Test", Manufacturer = "Test", Endpoint = "https://gateway.invalid/api/" }, httpClient: _http);
		Set ("_client", _client);
		Set ("_clientHttp", _http);
		Field<OverkizWorkQueue> ("_workQueue").SetClient (_client);
		_driver.EventDelay = (milliseconds, token) =>
			{
			if (milliseconds == 10000) _retry.TrySetResult (true);
			return _poll.WaitAsync (token);
			};
		}

	[TearDown]
	public async Task TearDown ()
		{
		_transport?.Release.TrySetResult (true);
		if (_driver != null)
			{
			_driver.Dispose ();
			await TestSupport.Complete (Field<Task> ("_clientDisposalTask"));
			}
		_http?.Dispose ();
		_logger?.Dispose ();
		_poll?.Dispose ();
		}
	private void Set (string name, object value) => typeof (OverkizPlatformDriver).GetField (name, Private).SetValue (_driver, value);
	private T Field<T> (string name) => (T)typeof (OverkizPlatformDriver).GetField (name, Private).GetValue (_driver);
	private void Start () => typeof (OverkizPlatformDriver).GetMethod ("StartEventLoop", Private).Invoke (_driver, null);
	private async Task Stop ()
		{
		_driver.Dispose ();
		await TestSupport.Complete (Field<Task> ("_clientDisposalTask"));
		Assert.That (_transport.Unexpected, Is.Empty);
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task EventLoop_UsesExistingListenerOrRegistersMissingListener (bool loginFirst)
		{
		if (loginFirst) Assert.That (await _client.Login (), Is.True);
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_transport.Fetched.Task);
		await Stop ();
		Assert.That (_transport.RegisterCount, Is.EqualTo (1));
		Assert.That (_transport.UnregisterCount, Is.EqualTo (1));
		}

	[Test]
	public async Task LocalLoop_QuotedExpiredListenerRecoversConfirmedTransportLoss ()
		{
		await _client.DisposeAsync ();
		_client = new OverkizClient (string.Empty, string.Empty,
			OverkizConst.LocalServer ("gateway.invalid"), "synthetic-local-token", _http);
		Set ("_client", _client);
		Field<OverkizWorkQueue> ("_workQueue").SetClient (_client);
		Assert.That (await _client.Login (), Is.True);
		_transport.QuotedListenerFirst = true;
		_driver.RecordEventTransport (_client, new TaskCanceledException ("first confirmed transport failure"), CancellationToken.None);
		_driver.RecordEventTransport (_client, new TaskCanceledException ("second confirmed transport failure"), CancellationToken.None);
		Assert.That (Field<bool> ("_apiReachable"), Is.False);
		var iterationCompleted = Signal ();
		var originalDelay = _driver.EventDelay;
		var normalDelays = 0;
		_driver.EventDelay = (milliseconds, token) =>
			{
			if (milliseconds == 2000 && Interlocked.Increment (ref normalDelays) == 2)
				iterationCompleted.TrySetResult (true);
			return originalDelay (milliseconds, token);
			};
		Start ();
		_poll.Release ();
		await TestSupport.Complete (iterationCompleted.Task);
		Assert.That (Field<bool> ("_apiReachable"), Is.True);
		Assert.That (_driver.OnlineIndicatorIsOnline, Is.True);
		Assert.That (_transport.RegisterCount, Is.EqualTo (2));
		Assert.That (_transport.FetchCount, Is.EqualTo (2));
		Assert.That (_transport.LoginCount, Is.Zero, "Local recovery does not submit cloud login.");
		await Stop ();
		Assert.That (_transport.UnregisterCount, Is.EqualTo (1));
		}

	[TestCase (false)]
	[TestCase (true)]
	public async Task EventLoop_RecoversRejectedListenerOrSession (bool authenticationRejected)
		{
		await _client.Login ();
		_transport.RejectFirst = true;
		_transport.RejectAuthentication = authenticationRejected;
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_transport.Fetched.Task);
		await Stop ();
		Assert.That (_transport.RegisterCount, Is.EqualTo (2));
		Assert.That (_transport.LoginCount, Is.EqualTo (authenticationRejected ? 2 : 1));
		Assert.That (_transport.FetchCount, Is.EqualTo (2));
		}

	[Test]
	public async Task Timeout_DoesNotPermanentlyStopEventMonitoring ()
		{
		_transport.TimeoutFirst = true;
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_retry.Task);
		Assert.That (Field<Task> ("_eventLoopTask").IsCompleted, Is.False);
		_poll.Release (2); // Release the backoff and the following poll interval.
		await TestSupport.Complete (_transport.Fetched.Task);
		await Stop ();
		Assert.That (_transport.FetchCount, Is.EqualTo (2));
		Assert.That (_transport.RegisterCount, Is.EqualTo (1));
		}

	[TestCase (HttpStatusCode.BadRequest, false)]
	[TestCase (HttpStatusCode.BadRequest, true)]
	[TestCase ((HttpStatusCode)429, false)]
	[TestCase ((HttpStatusCode)429, true)]
	public async Task QuotaResponse_UsesExistingBackoffWithoutReauthentication (HttpStatusCode status, bool disposeDuringBackoff)
		{
		Assert.That (await _client.Login (), Is.True);
		_transport.QuotaFirst = true;
		_transport.QuotaStatus = status;
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_retry.Task);
		Assert.That (_transport.FetchCount, Is.EqualTo (1), "No immediate library retry before the existing driver backoff.");
		Assert.That (_transport.LoginCount, Is.EqualTo (1), "A quota response must not cause reauthentication.");
		Assert.That (_transport.RegisterCount, Is.EqualTo (1));
		if (!disposeDuringBackoff)
			{
			_poll.Release (2);
			await TestSupport.Complete (_transport.Fetched.Task);
			}
		await Stop ();
		Assert.That (_transport.FetchCount, Is.EqualTo (disposeDuringBackoff ? 1 : 2));
		Assert.That (_transport.LoginCount, Is.EqualTo (1));
		Assert.That (_transport.RegisterCount, Is.EqualTo (1));
		Assert.That (_transport.UnregisterCount, Is.EqualTo (1));
		}

	[Test]
	public async Task Disposal_DuringTimeoutBackoffStopsWithoutAnotherFetch ()
		{
		_transport.TimeoutFirst = true;
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_retry.Task);
		await Stop ();
		Assert.That (Field<Task> ("_eventLoopTask").IsCompleted, Is.True);
		Assert.That (_transport.FetchCount, Is.EqualTo (1));
		Assert.That (_transport.UnregisterCount, Is.EqualTo (1));
		Assert.That (_transport.Disposed, Is.True);
		}

	[Test]
	public async Task Disposal_WaitsForFetchAndListenerCleanupBeforeClosingTransport ()
		{
		_transport.HoldFetch = true;
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_transport.Entered.Task);
		_driver.Dispose ();
		Assert.That (Field<Task> ("_clientDisposalTask").IsCompleted, Is.False);
		Assert.That (_transport.Disposed, Is.False);
		_transport.Release.TrySetResult (true);
		await Stop ();
		Assert.That (_transport.Disposed, Is.True);
		Assert.That (_transport.UnregisterCount, Is.EqualTo (1));
		}

	[Test]
	public async Task Restart_WaitsForPreviousLoopCleanupBeforeUsingReplacementListener ()
		{
		_transport.HoldFetch = true;
		Start ();
		_poll.Release ();
		await TestSupport.Complete (_transport.Entered.Task);
		Start ();
		var restarted = Field<Task> ("_eventLoopTask");
		_poll.Release ();
		_transport.Release.TrySetResult (true);
		await TestSupport.Complete (_transport.SecondFetch.Task);
		Assert.That (restarted.IsCompleted, Is.False);
		Assert.That (_transport.RegisterCount, Is.EqualTo (2));
		Assert.That (_transport.UnregisterCount, Is.EqualTo (1));
		await Stop ();
		Assert.That (_transport.UnregisterCount, Is.EqualTo (2));
		}

	[Test]
	public async Task Disposal_WaitsForActiveQueuedWorkBeforeClosingTransport ()
		{
		var entered = Signal ();
		var release = Signal ();
		var work = Field<OverkizWorkQueue> ("_workQueue").EnqueueAsync (async client =>
			{
			entered.TrySetResult (true);
			await release.Task;
			await client.GetDevices ();
			});
		try
			{
			await TestSupport.Complete (entered.Task);
			_driver.Dispose ();
			Assert.That (_transport.Disposed, Is.False);
			Assert.That (Field<Task> ("_clientDisposalTask").IsCompleted, Is.False);
			}
		finally { release.TrySetResult (true); await TestSupport.Complete (work); }
		await Stop ();
		Assert.That (_transport.Disposed, Is.True);
		}

	private sealed class EventTransport : HttpMessageHandler
		{
		internal int LoginCount, RegisterCount, FetchCount, UnregisterCount;
		internal bool RejectFirst, RejectAuthentication, TimeoutFirst, HoldFetch, Disposed, QuotaFirst, QuotedListenerFirst;
		internal HttpStatusCode QuotaStatus = HttpStatusCode.BadRequest;
		internal readonly List<string> Unexpected = new ();
		internal readonly TaskCompletionSource<bool> Entered = Signal (), Release = Signal (), Fetched = Signal (), SecondFetch = Signal ();
		protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
			{
			var path = request.RequestUri.AbsolutePath;
			if (Disposed) throw new ObjectDisposedException (nameof (EventTransport));
			if (path.EndsWith ("/login")) { LoginCount++; return Reply ("{\"success\":true}"); }
			if (path.EndsWith ("/events/register")) { RegisterCount++; return Reply ("{\"id\":\"listener" + RegisterCount + "\"}"); }
			if (path.EndsWith ("/fetch"))
				{
				FetchCount++;
				Entered.TrySetResult (true);
				if (HoldFetch) await Release.Task;
				if (RejectFirst && FetchCount == 1) return Reply (RejectAuthentication ? "{\"error\":\"Not authenticated\"}" : "{\"error\":\"Invalid event listener id listener1\"}", HttpStatusCode.BadRequest);
				if (QuotedListenerFirst && FetchCount == 1) return Reply ("""{"errorCode":"UNSPECIFIED_ERROR","error":"\"No registered event listener.\""}""", HttpStatusCode.BadRequest);
				if (QuotaFirst && FetchCount == 1) return Reply ("{\"error\":\"Too many requests, try again later\",\"errorCode\":\"AUTHENTICATION_ERROR\"}", QuotaStatus);
				if (TimeoutFirst && FetchCount == 1) throw new OperationCanceledException ("synthetic timeout");
				Fetched.TrySetResult (true);
				if (FetchCount == 2) SecondFetch.TrySetResult (true);
				return Reply ("[]");
				}
			if (path.EndsWith ("/unregister")) { UnregisterCount++; return Reply ("{}"); }
			if (path.EndsWith ("/setup/devices") || path.EndsWith ("/setup/gateways")) return Reply ("[]");
			Unexpected.Add (path);
			throw new InvalidOperationException ("Unexpected synthetic request: " + path);
			}
		private static HttpResponseMessage Reply (string body, HttpStatusCode code = HttpStatusCode.OK) => new (code) { Content = new StringContent (body) };
		protected override void Dispose (bool disposing) { Disposed = true; base.Dispose (disposing); }
		}
	}