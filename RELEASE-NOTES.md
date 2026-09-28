# OverkizCrestronDriver v2.3.7

This patch release improves event recovery and connection cleanup. Existing installations keep their configuration fields, device identities and shade controls; no configuration migration is required.

## Changes

- Use the published OverkizClient 2.1.0 package and its automatic event-listener recovery. Reuse the listener established during login.
- Resume event monitoring after a request timeout using the existing retry delay.
- Wait for the previous event loop and active requests before disposing a connection. Discard work queued for an old connection instead of sending it through a replacement.

## Validation

All 57 automatic driver tests passed locally and on a CP4-R processor, including ten new regression cases. Windows .NET Framework 4.7.2 runs the 27 portable cases; the 30 SDK lifecycle cases also run in the .NET 10 desktop SDK harness and on the processor. The production merge and package build passed. Temporary processor test instances and their package archives were removed after validation.

See the [test setup](README.md#testing), [processor test guide](OverkizCrestronDriver.ProcessorTests/README.md) and [development history](DEVELOPMENT-HISTORY.md) for tooling and validation details. Processor test packages have independent versions and are not included in the production NuGet package.

## Install

Use the production `.pkg` attached to this release, or NuGet package `CrestronHomeDriver.Overkiz.Shades` version `2.3.7`. The package manifest version is `2.3.007.0000`. See [installation instructions](README.md) and the [changelog](CHANGELOG.md).
