// Copyright © 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;

using OverKiz.CrestronDriver;

// Tell the Crestron runtime where to start to initialize this driver.
[assembly: DriverAssemblyEntryPoint (typeof (EntryPoint))]

/// <summary>
/// SDK V2 driver entry point. Discovered by the Crestron runtime via the
/// assembly-level <see cref="DriverAssemblyEntryPointAttribute"/> above.
/// Must be a top-level (no namespace) class named "EntryPoint".
/// </summary>
public sealed class EntryPoint : DriverAssemblyEntryPoint
	{
	/// <inheritdoc/>
	public override DriverController CreateDriverControllerInstance (
		DriverControllerCreationArgs args)
		{
		var resources = DriverImplementationResources.FromCreationArgs (
			args, typeof (EntryPoint));

		var platform = new OverkizPlatformDriver (args, resources);

		var rootEntity = new ConfigurableDriverEntity (
			platform.ControllerId,
			platform,
			platform.ConfigurationController);

		return new DispatchingDeviceController (rootEntity, args, null);
		}
	}
