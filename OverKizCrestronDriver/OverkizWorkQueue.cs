// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

using OverKizApi;

namespace OverKiz.CrestronDriver;

/// <summary>
/// Serialises all <see cref="OverkizClient"/> access for a single gateway.
/// Because every entity has its own poll timer, concurrent requests must be
/// queued rather than issued in parallel.  A single <see cref="SemaphoreSlim"/>
/// ensures only one async operation runs at a time.
/// </summary>
internal sealed class OverkizWorkQueue
	{
	private readonly SemaphoreSlim _gate = new (1, 1);
	private volatile OverkizClient _client;
	private volatile bool _stopped;

	// ── Client lifecycle ──────────────────────────────────────────────────

	/// <summary>
	/// Sets (or clears) the active client.  Pass <c>null</c> when the gateway
	/// connection is lost so that pending work items are silently dropped.
	/// </summary>
	public void SetClient (OverkizClient client) => _client = client;

	/// <summary>
	/// Permanently stops the queue (called on driver dispose).
	/// After this, <see cref="EnqueueAsync"/> silently discards all work.
	/// </summary>
	public void Stop ()
		{
		_stopped = true;
		_client = null;
		}

	// ── Work submission ───────────────────────────────────────────────────

	/// <summary>
	/// Enqueues <paramref name="work"/> to run exclusively when the semaphore
	/// is available and the client is non-null.
	/// The returned task completes when the work item has finished (or been
	/// skipped).  Callers may fire-and-forget it.
	/// </summary>
	public async Task EnqueueAsync (Func<OverkizClient, Task> work)
		{
		if (_stopped || work == null)
			return;

		await _gate.WaitAsync ().ConfigureAwait (false);
		try
			{
			OverkizClient client = _client;
			if (client == null || _stopped)
				return;

			await work (client).ConfigureAwait (false);
			}
		finally
			{
			_ = _gate.Release ();
			}
		}
	}