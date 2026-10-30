// Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
namespace OverkizCrestronDriver.Tests;

// Fixture infrastructure only; the runner handles transports that ignore cooperative cancellation.
internal sealed class LiveTestLifetime
	{
	private readonly CancellationTokenSource _source;
	private int _cleaned;
	internal LiveTestLifetime (TimeSpan timeout, CancellationToken cancellation = default)
		{
		if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes (2))
			throw new ArgumentOutOfRangeException (nameof (timeout));
		_source = CancellationTokenSource.CreateLinkedTokenSource (cancellation);
		_source.CancelAfter (timeout);
		}
	internal async Task InvokeAsync (Func<CancellationToken, Task> action)
		{
		if (Volatile.Read (ref _cleaned) != 0) throw new ObjectDisposedException (nameof (LiveTestLifetime));
		_source.Token.ThrowIfCancellationRequested ();
		await action (_source.Token);
		}
	internal async Task CleanupAsync (Func<Task> disposeDriver, Action disposeLogger)
		{
		if (Interlocked.Exchange (ref _cleaned, 1) != 0) return;
		var failures = new List<Exception> ();
		try { _source.Cancel (); } catch (Exception exception) { failures.Add (exception); }
		try { await disposeDriver (); } catch (Exception exception) { failures.Add (exception); }
		try { disposeLogger (); } catch (Exception exception) { failures.Add (exception); }
		finally { _source.Dispose (); }
		if (failures.Count > 1) throw new AggregateException (failures);
		if (failures.Count == 1) ExceptionDispatchInfo.Capture (failures[0]).Throw ();
		}
	}
