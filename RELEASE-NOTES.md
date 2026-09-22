# OverkizCrestronDriver v2.3.6

This patch release updates the client and SDK dependencies while preserving the driver's configuration fields, device identities and shade controls. Existing installations do not need a configuration migration. The driver's version is independent of the client library's major version.

## Changes

- Use the released OverkizClient 2.0.0, with attribute-controlled models, typed events and actions, CLR device values and stronger response validation.
- Update Crestron.DeviceDrivers.DevKit from 27.0.24 to 29.0.10.
- Remove unused log4net, Polly and related entries from the merged driver dependencies.
- Preserve serialization-attribute enum and type references when patching the merged assembly, preventing packaging failures after the model changes.

Logging continues through the Crestron SDK's `DriverControllerLogger`. The production driver does not bundle log4net or Newtonsoft.Json. The SDK's private `Newtonsoft.Json.Compact.dll` remains a desktop test requirement and is supplied by the processor at runtime.

## Validation

The updated dependencies passed 47 offline/lifecycle tests and three read-only live gateway tests on both desktop and processor. The production merge, patch and package build completed without warnings or errors. These tests do not certify every gateway service or processor firmware version.

See the [test setup](README.md#testing), [processor test guide](OverkizCrestronDriver.ProcessorTests/README.md) and [development history](DEVELOPMENT-HISTORY.md) for tooling and validation details. Processor test packages have independent versions and are not included in the production NuGet package.

## Install

Use the production `.pkg` attached to this release, or NuGet package `CrestronHomeDriver.Overkiz.Shades` version `2.3.6`. The package manifest version is `2.3.006.0000`. See [installation instructions](README.md) and the [changelog](CHANGELOG.md).
