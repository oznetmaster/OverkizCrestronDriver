// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

using Crestron.DeviceDrivers.SDK.EntityModel;

namespace OverKiz.CrestronDriver;

/// <summary>
/// Common contract implemented by every Overkiz child entity type
/// (shades, lights, thermostats, …).  The platform driver operates
/// exclusively through this interface so that adding a new device
/// type requires only a new implementing class and a single factory
/// case in <see cref="OverkizPlatformDriver"/>.
/// </summary>
internal interface IOverkizEntity
	{
	/// <summary>
	/// The Crestron SDK controller ID assigned to this entity.
	/// </summary>
	string ControllerId
		{
		get;
		}

	/// <summary>
	/// The <see cref="DeviceUxCategory"/> that describes this entity
	/// to Crestron Home (e.g. Shade, Light, Thermostat).
	/// </summary>
	DeviceUxCategory UxCategory
		{
		get;
		}

	/// <summary>
	/// Called by the platform when the gateway connection is established.
	/// The entity starts its own poll timer (if applicable), submitting work
	/// via <paramref name="queue"/> so that all client access is serialised.
	/// Implementations must also mark themselves online.
	/// </summary>
	void StartPolling (OverkizWorkQueue queue);

	/// <summary>
	/// Called by the platform when the gateway connection is lost or the
	/// driver is disposed.  The entity stops its poll timer and marks itself
	/// offline.
	/// </summary>
	void StopPolling ();

	/// <summary>
	/// Explicitly set the child entity's online / offline indicator.
	/// Called by the platform to propagate gateway connectivity changes
	/// to every child.
	/// </summary>
	void SetOnline (bool online);

	/// <summary>
	/// Called by the platform when a <c>DeviceAvailabilityChanged</c> event
	/// is received for this entity's device URL.
	/// </summary>
	void UpdateAvailability (bool available);

	/// <summary>
	/// Called by the platform when a <c>DeviceStateChanged</c> event is
	/// received for this entity's device URL.  The entity applies the
	/// relevant state values from the event payload.
	/// </summary>
	void ApplyEventStates (IReadOnlyList<OverKizApi.Models.EventState> states);

	/// <summary>
	/// Called by the platform to set the moving indicator directly,
	/// driven by execution lifecycle events (<c>ExecutionRegistered</c> /
	/// <c>ExecutionStateChanged</c>).  Used for one-way (RTS) devices that
	/// have no state-feedback channel.
	/// </summary>
	void SetMoving (bool moving);

	/// <summary>
	/// Called by the platform when the device's user-assigned label has
	/// changed (either via a <c>DeviceUpdated</c> event or periodic label
	/// polling).  The entity updates its <c>deviceLabel</c> extension-UI
	/// property so the layout title reflects the new name.
	/// </summary>
	void UpdateLabel (string newLabel);
	}