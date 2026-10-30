// Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
namespace OverkizCrestronDriver.Tests;
[TestFixture, FixtureLifeCycle (LifeCycle.InstancePerTestCase), Category ("Offline"), NonParallelizable]
public sealed class LiveTestLifetimeTests
	{
	[TestCase (0)] [TestCase (-1)] [TestCase (121)]
	public void InvalidBoundRejected (int seconds) =>
		Assert.Throws<ArgumentOutOfRangeException> (() => new LiveTestLifetime (TimeSpan.FromSeconds (seconds)));
	[Test]
	public async Task ActionReceivesDeadlineCancellationAndCleanupRemainsAvailable ()
		{
		var life = new LiveTestLifetime (TimeSpan.FromSeconds (2));
		var entered = false; CancellationToken received = default;
		// Start the action before entering NUnit's async assertion machinery on Mono.
		var action = life.InvokeAsync (async token =>
			{ entered = true; received = token; await Task.Delay (Timeout.Infinite, token); });
		await Assert.ThatAsync (async () => await action, Throws.InstanceOf<OperationCanceledException> ());
		Assert.That (entered, Is.True); Assert.That (received.IsCancellationRequested, Is.True);
		var cleaned = false;
		await life.CleanupAsync (() => { cleaned = true; return Task.CompletedTask; }, () => { });
		Assert.That (cleaned, Is.True);
		}
	[Test]
	public async Task ExternalCancellationStopsActionAndDoesNotStartNextStage ()
		{
		using var stop = new CancellationTokenSource ();
		var life = new LiveTestLifetime (TimeSpan.FromSeconds (5), stop.Token);
		var entered = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var task = life.InvokeAsync (async token => { entered.SetResult (true); await Task.Delay (Timeout.Infinite, token); });
		await entered.Task; stop.Cancel ();
		await Assert.ThatAsync (async () => await task, Throws.InstanceOf<OperationCanceledException> ());
		var next = false;
		await Assert.ThatAsync (async () => await life.InvokeAsync (_ => { next = true; return Task.CompletedTask; }), Throws.InstanceOf<OperationCanceledException> ());
		Assert.That (next, Is.False);
		await life.CleanupAsync (() => Task.CompletedTask, () => { });
		}
	[Test]
	public async Task CleanupJoinsDriverBeforeLoggerAndRunsOnce ()
		{
		var life = new LiveTestLifetime (TimeSpan.FromSeconds (5)); var calls = new List<string> ();
		var join = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		var cleanup = life.CleanupAsync (async () => { calls.Add ("driver"); await join.Task; calls.Add ("joined"); }, () => calls.Add ("logger"));
		Assert.That (calls, Is.EqualTo (new[] { "driver" }));
		join.SetResult (true); await cleanup;
		await life.CleanupAsync (() => { calls.Add ("duplicate"); return Task.CompletedTask; }, () => calls.Add ("duplicate"));
		Assert.That (calls, Is.EqualTo (new[] { "driver", "joined", "logger" }));
		}
	[Test]
	public async Task DriverCleanupFailureStillReleasesLogger ()
		{
		var life = new LiveTestLifetime (TimeSpan.FromSeconds (5)); var logger = false;
		await Assert.ThatAsync (async () => await life.CleanupAsync (() => throw new InvalidOperationException ("synthetic disposal"), () => logger = true), Throws.TypeOf<InvalidOperationException> ());
		Assert.That (logger, Is.True);
		}
	[Test]
	public async Task BothCleanupFailuresArePreservedAndLaterOperationIsRefused ()
		{
		var life = new LiveTestLifetime (TimeSpan.FromSeconds (5)); AggregateException observed = null;
		try { await life.CleanupAsync (() => throw new InvalidOperationException ("driver"), () => throw new ArgumentException ("logger")); }
		catch (AggregateException error) { observed = error; }
		Assert.That (observed, Is.Not.Null); Assert.That (observed.InnerExceptions.Count, Is.EqualTo (2));
		await Assert.ThatAsync (async () => await life.InvokeAsync (_ => Task.CompletedTask), Throws.TypeOf<ObjectDisposedException> ());
		}
	}
