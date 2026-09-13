# OverkizCrestronDriver processor tests

This standalone Entity V2 **Utility** package runs the driver test assembly on Crestron Home's Mono runtime. It has its own identity and NUnit tile. The production driver can remain installed alongside it. The test package does not start or configure the production driver's installed instance.

## Build and deploy

Open `OverkizCrestronDriver.slnx` in Visual Studio with a current .NET SDK, the .NET Framework 4.7.2 targeting pack, and the Crestron Driver SDK installed. Clone [CrestronHomeNUnit](https://github.com/oznetmaster/CrestronHomeNUnit) beside this repository, or set `ProcessorTestSdkRoot` privately. Build **OverkizCrestronDriver.ProcessorTests**, Debug. Enable `DeployAfterBuild` in a private `.csproj.user` file with the same deployment properties as the production driver. Debug deployment is performed only by Visual Studio. Keep credentials and machine paths in files excluded through `.git/info/exclude`; never commit them.

For a command-line package build without deployment:

```powershell
dotnet build OverkizCrestronDriver.ProcessorTests/OverkizCrestronDriver.ProcessorTests.csproj -c Debug -p:BuildProcessorTestPackages=true -p:DeployAfterBuild=false
```

The package appears at `bin/Debug/net472/OverkizCrestronDriver.ProcessorTests.pkg`. Add **OverkizCrestronDriver Tests** from **Utility** in Configure. No separate NUnit host package is required.

## Suites

- **Unit Tests**: 25 offline driver cases. Run on Windows through the NUnit Visual Studio adapter or on the processor. No account credentials or physical devices are needed.
- **Processor Lifecycle**: 12 SDK lifecycle checks. Shade commands clamp and invert position correctly without inventing observed state; one-way and favourite-position capabilities are respected; partial events preserve other state; initial and recovered availability agree with SDK snapshots; removing a display override restores the latest API name. Run these separately on the processor; the shared desktop harness provides additional validation.

Use the Windows runner's **Find packages**, select this package, connect, then select a suite and **Run all**. Discovery uses a dynamically assigned port. The standalone tile exposes the same suites and results. Nothing runs automatically on deployment. Original driver assets are under `DriverTestData`; the test tile's assets retain their own root paths.

These suites do not authenticate with external services or operate physical devices. Processor lifecycle results must be verified on real hardware; desktop unit success does not establish processor lifecycle compatibility.

This project targets only `net472`. It is not packable or publishable to NuGet. See [third-party notices](THIRD-PARTY-NOTICES.md), the root LICENSE, and [runner documentation](https://github.com/oznetmaster/CrestronHomeNUnit#readme).


## Expanded coverage

Shade commands clamp and invert position correctly without inventing observed state; one-way and favourite-position capabilities are respected; partial events preserve other state; initial and recovered availability agree with SDK snapshots; removing a display override restores the latest API name.

The package contains 25 offline cases and 22 lifecycle cases. Lifecycle tests exercise newly constructed test entities, not the installed production driver. Both suites are selectable in the Windows runner and through the standalone Utility tile. Processor hardware validation remains required.
