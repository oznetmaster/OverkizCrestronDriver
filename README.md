# OverkizCrestronDriver

See the [changelog](CHANGELOG.md) for release history and the [release notes](RELEASE-NOTES.md) for the current driver update. Driver releases are made for runtime fixes or dependency changes; adding tests alone does not require a driver release.

A **Crestron Home** platform driver that integrates Overkiz-compatible smart-home gateways
(Somfy TaHoma, Atlantic Cozytouch, Hitachi Hi Kumo, and others) as managed shade/blind devices,
supporting both cloud and local LAN connections.

Crestron and Crestron Home are trademarks or registered trademarks of Crestron Electronics, Inc. This project is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.

[![License: MIT + Commons Clause](https://img.shields.io/badge/License-MIT%20%2B%20Commons%20Clause-blue.svg)](LICENSE)

See [CHANGELOG.md](CHANGELOG.md) for release history, or the [GitHub Releases](https://github.com/oznetmaster/OverkizCrestronDriver/releases) page for downloadable assets.

---

## Driver Architecture

This driver is a **platform driver** — it connects to the Overkiz API, discovers all shades/blinds
on the gateway, and registers each as a managed child sub-controller under a single Crestron Home
device entry. Optionally, shades can be grouped into **room aggregate entities** that provide a
combined room-level UI tile alongside the individual shade sub-controllers.

The driver is implemented using the **Crestron Home SDK V2 Entity Model** — it derives directly
from `ReflectedAttributeDriverEntity` and declares all properties, commands, and events via
SDK attributes, with no dependency on any RAD base type (`ABaseDriver`, `ABasicVideoDisplay`,
etc.) or command/state controller infrastructure (`StateController`, `PollingDeviceStateBase`, etc.).

---

## Features

- Cloud connection via Somfy OAuth 2.0 (and other Overkiz-based cloud servers)
- Local LAN connection using a Somfy developer-mode bearer token
- Local mode takes precedence when either local field is supplied, and requires both **Gateway IP** and **Local API Token**
- Automatically discovers all shades/blinds on the gateway and exposes each as a managed child device
- Per-shade UI with position control (two-way devices), open/stop/close buttons, and optional **My** position button
- Optional room aggregate entities grouping multiple shades with per-slot labels, room-level open/close/stop/my commands, and room-wide position control
- Display name overrides for individual shades via `ShadeDisplayNames` config
- Dynamic rename, add, and delete detection via Overkiz event streaming
- RTS (one-way radio) shade support with motion state inferred from command execution lifecycle

Compatibility note: the driver is intended for Overkiz-compatible gateways, but the current implementation has only been validated by the author with a Somfy TaHoma gateway. Other Overkiz-compatible gateways may work but have not yet been directly tested with this Crestron Home driver.

---

## Prerequisites

| Requirement | Details |
|---|---|
| Crestron Home processor | Running a firmware version compatible with extension drivers |
| Overkiz-compatible gateway | Somfy TaHoma Switch, TaHoma Premium, Connectivity Kit, etc. |
| Cloud account **or** local token | Somfy account for cloud mode; developer-mode token for local mode |

---

## Installation

The best way to download and install this driver on a Crestron Home system is to use the [Crestron Home Driver Feed Installer](https://github.com/oznetmaster/Crestron-Home-Driver-Feed-Installer) repository and application.

If you prefer to install manually, use the attached `Shade_Overkiz_IP_V2.pkg` asset from the relevant GitHub Release. The automatic GitHub `Source code (zip)` and `Source code (tar.gz)` assets are repository snapshots, not installable Crestron driver packages.

NuGet package availability: this driver is also published as the `CrestronHomeDriver.Overkiz.Shades` NuGet package. This NuGet package conforms to the **Crestron Home Driver NuGet Publishing Standard v1**. It is a distribution wrapper for the final `Shade_Overkiz_IP_V2.pkg` artifact, includes the required `crestron-driver-package.json` manifest, and is not intended as a direct DLL reference package.

Crestron Home Driver NuGet Publishing Standard v1 is **not** an official Crestron product or specification. It is an open source packaging standard created to facilitate community distribution and discovery of Crestron Home drivers through NuGet.

1. Download `Shade_Overkiz_IP_V2.pkg` from the GitHub Release assets, or build it yourself using the instructions in [Building from Source](#building-from-source).
2. Upload the `.pkg` to your Crestron Home processor manually (for example via SFTP to `/user/ThirdPartyDrivers/Import`).
3. In the Crestron Home **Configure** application, add a new device and select the **Tahoma Gateway** driver.
4. Fill in the connection configuration:

| Field | Description |
|---|---|
| Cloud Username | Your Somfy / Overkiz account e-mail (cloud mode only) |
| Cloud Password | Your account password (cloud mode only) |
| Cloud Server | The Overkiz server your account belongs to (default: SomfyEurope) |
| Gateway IP | LAN IP address or hostname of your gateway (local mode only) |
| Local API Token | Vendor-issued bearer token for local LAN API access (local mode only) |
| Room Groups | Room grouping and member configuration (see Configuration below) |
| Shade Display Names | Display name overrides for individual shades (see Configuration below) |

If either local-mode field is supplied, the driver treats the configuration as local mode and requires both local values. Otherwise it requires the cloud username and password.

The local API token is not generated by this driver. It must be obtained from the gateway vendor's cloud/account system for the specific Overkiz ecosystem you use. The exact process varies by brand, server region, and account type, and some ecosystems may not expose local or developer API access at all. For Somfy-based systems, use Somfy's developer/local API process. For other Overkiz-based systems, consult that vendor's developer or local API documentation.

Current limitation: install and configure this driver using the Crestron Home Setup application. The beta Configure Pro workflow is not currently recommended for this driver.

---

## Configuration

### Room Groups

Groups shades into room aggregate entities. Format:

```
RoomKey:Display Title=ApiLabel1:Slot1 Name,ApiLabel2:Slot2 Name
```

Multiple rooms are separated by semicolons. Example:

```
Lounge:Lounge Blinds=Lounge Left Blind:Left,Lounge Center Blind:Centre,Lounge Right Blind:Right; Bedroom=Bedroom Blind
```

- `RoomKey` — a unique room key used internally for matching (case-insensitive)
- `Display Title` — the visible label for the room tile
- `ApiLabel` — the exact name of the shade as it appears in the Overkiz app
- `Slot Name` — the subheading shown under each slot in the room tile

Room tiles support up to 10 configured slots. Extra configured slots beyond that UI limit are not shown.

### Shade Display Names

Overrides the visible label for individual shade sub-controllers. Format:

```
ApiLabel:Display Name; ApiLabel2:Display Name2
```

Example:

```
Master Blind:Master Bedroom; En-suite:Ensuite Blind
```

The `ApiLabel` is the raw Overkiz API name used for all room-matching logic. The display name is shown in the Crestron Home UI only.

---

## Building from Source

### Dependencies

- [OverkizClient](https://www.nuget.org/packages/OverkizClient) NuGet package (restored automatically)
- [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit) NuGet package
- C# 13 language features compiled for a `.NET Framework 4.7.2` target, with compatibility shim assemblies merged into the driver package as needed
- [ILRepack](https://github.com/gluck/il-repack) (via `ILRepackMerge.ps1`) to merge dependencies into a self-contained driver DLL
- `PatchMergedAssembly.ps1` to rewrite merged `System.*` helper types that Crestron Home's Mono sandbox rejects during reflection
- `ManifestUtil.exe` from the Crestron Driver SDK to produce the final `.pkg`

### Build

```powershell
dotnet build -c Release
```

The build pipeline:
1. Compiles the driver targeting `net472`
   - The project uses `LangVersion=latest` (currently C# 13) while targeting the Crestron-required `.NET Framework 4.7.2` runtime.
   - Required compatibility shim assemblies are merged into the driver and then patched for Crestron Home's runtime constraints.
2. Bumps `DriverVersion` and `VersionDate` in `Shade_Overkiz_IP_V2.json`
	  - Every deployable build increments the 4th component.
   - Start a new public release line by running `StartReleaseCycle.ps1`, which increments the 3rd component and resets the 4th to `0000`.
3. ILRepacks runtime dependencies into a single `Shade_Overkiz_IP_V2.dll`
4. Runs `PatchMergedAssembly.ps1` against the merged assembly
5. Packages everything into `Shade_Overkiz_IP_V2.pkg` using Crestron's ManifestUtil

### GitHub Release Asset

GitHub does not receive anything from `bin/` automatically, but the repository now includes a working GitHub Actions workflow that builds and attaches the `.pkg` when a GitHub Release is published.

The `release-package.yml` workflow runs on `windows-latest`, installs `Crestron.DeviceDrivers.ManifestUtil` from NuGet, builds the solution in `Release`, locates the generated `Shade_Overkiz_IP_V2.pkg`, and uploads it as the release asset automatically.

The same release workflow also publishes the `OverkizCrestronDriver` NuGet package, which wraps the final `Shade_Overkiz_IP_V2.pkg` artifact.

Typical release flow:

1. Push the release commit and tag.
2. Publish a GitHub Release for that tag.
3. Let the workflow build and attach the `.pkg` asset.

---

## License

MIT + Commons Clause © 2026 Neil Colvin — see [LICENSE](LICENSE).

Free to use and modify. You may not sell the Software as a standalone product or sublicense it.
Commercial system integration work (e.g. a Crestron installer commissioning a customer system) is
explicitly permitted, even where a fee is charged for that service.

> **Note:** This project references [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit),
> which is subject to Crestron's SDK license agreement. That license governs the SDK libraries only;
> the source code in this repository is licensed independently under the terms above.



## Automated tests

The solution includes `OverkizCrestronDriver.Tests` (NUnit 4 with the Visual Studio NUnit adapter) and `OverkizCrestronDriver.ProcessorTests` (a standalone Crestron Home Utility test package). The 25 offline tests exercise driver logic without credentials or real device commands. The 22 processor lifecycle cases are excluded on Windows in this project; the dedicated desktop SDK harness exercises the same fixture sources.

```powershell
dotnet test OverkizCrestronDriver.Tests/OverkizCrestronDriver.Tests.csproj -c Release
```

Build the processor project in Debug in Visual Studio to build and deploy using private deployment settings. See [processor test instructions](OverkizCrestronDriver.ProcessorTests/README.md) for setup, suites, tile operation and UI separation. Processor packages are not published to NuGet. See [CHANGELOG](CHANGELOG.md) for changes.


### Expanded driver behavior tests

Cover device filtering, restored room groups, stable children across discovery, rename/removal and late login/discovery completion. Clearing or disposing the platform now removes child controllers and rejects stale work. Reconnect creates and disposes a separate HTTP client for each connection, avoiding attempts to change BaseAddress after requests have started.

Shade commands clamp and invert position correctly without inventing observed state; one-way and favourite-position capabilities are respected; partial events preserve other state; initial and recovered availability agree with SDK snapshots; removing a display override restores the latest API name.

The current package contains **25 offline tests**, **22 SDK entity/lifecycle tests** and **3 optional live gateway tests**. The processor package remains **net472 only**, appears under **Utility** in Configure, and can run independently through its own tile or the Windows NUnit runner. Unit and lifecycle fixtures use synthetic data. Live fixtures authenticate using an existing token and verify discovery, stable child identities and reconnection without moving shades or generating tokens.

`OverkizCrestronDriver.Lifecycle.Tests` runs the entity checks against the real desktop SDK on .NET 10. It compiles the relevant driver sources and shares fixture sources with the net472 processor tests. Building this project does not deploy a driver. A locally supplied `Newtonsoft.Json.Compact.dll` is needed by the SDK's manifest reader; it is supplied by the processor at runtime and must not be added to source control or bundled with the processor test package.

```powershell
dotnet test OverkizCrestronDriver.Tests/OverkizCrestronDriver.Tests.csproj --filter "TestCategory!=Processor"
dotnet test OverkizCrestronDriver.Lifecycle.Tests/OverkizCrestronDriver.Lifecycle.Tests.csproj
```

Set `CompactJsonPath` in the desktop test project's private `DesktopTest.Local.props`, excluded through `.git/info/exclude`, or pass it as an MSBuild property. Keep machine paths and credentials out of tracked files.

Desktop success does not establish Mono compatibility. Build the processor test project in Visual Studio, deploy it, and run the suites on the processor. The fixtures cover configuration, restoration, refresh/reconnect races and disposal using simulated responses. Installed-driver health remains a separate workflow check.

For live tests, copy `OverkizCrestronDriver.Tests/LiveTestSettings.example.json` to a private `LiveTestSettings.json`. The desktop SDK harness reads it from `%LOCALAPPDATA%/OverkizClient` (shared with the client console), or from the NUnit `TestDataDirectory` parameter. Set `enabled` to `true` for local testing, or explicitly supply `EnableLiveTests=true`; `EnableLiveTests=false` always disables it. On the processor, upload the file through **Test inputs** and select **Live Gateway**. See the [processor test instructions](OverkizCrestronDriver.ProcessorTests/README.md). Private tokens and settings must never be committed or packaged.


### Driver build and release versions

The driver's JSON manifest is the source of its four-component build version. Debug builds increment only the fourth component; for example, `2.0.001.0005` becomes `2.0.001.0006`. MSBuild's `Version` and default `PackageVersion` are derived from that same manifest and refreshed after the increment; their numeric form is `2.0.1.6`. Assembly binding versions remain separate. Test-only references and IDE design-time builds do not increment the production driver version.

GitHub tags and NuGet releases retain three components: `v2.0.1` and `2.0.1`. Prepare the manifest's first three components for the intended release before tagging. Release CI checks that the tag matches, resets the fourth component to zero, and verifies the generated `.pkg` version against the manifest and release version before publishing. It does not increment the selected patch again. Local Release builds preserve the manifest. A later Debug build can legitimately be newer than a published release; the processor test package has its own independent version.

Deployment validation compares the exact built `.pkg` against the imported catalogue entry and installed instance, numerically including all four components. Upload/import alone does not activate the new version. Keep the tested package and its hash: rebuilding creates a new artifact that must be validated again.

Run `pwsh -File tools/Test-DriverVersioning.ps1` to check these rules with temporary manifests; this does not change the working driver manifest or deploy anything.

See [versioning details](docs/Versioning.md) for build, release and installed-instance verification rules.
### Desktop SDK dependency in CI

The SDK's desktop manifest reader needs its `Newtonsoft.Json.Compact.dll` runtime dependency. Supply a local SDK/runtime copy through the `CompactJsonPath` MSBuild property (or private `DesktopTest.Local.props`). Maintainer CI restores the same verified copy from encrypted Actions secrets into its temporary directory; it is not committed, attached to release assets or included in processor packages. Fork pull requests do not receive these secrets and require a trusted maintainer validation run.


For automated local tests, processor tests and gated driver deployment, see the [Crestron Home NUnit CI development guide](https://github.com/oznetmaster/CrestronHomeNUnit/blob/HEAD/docs/ContinuousIntegration.md). It covers private configuration, live-test gates, install/update waits, results and optional test-package removal.

## Visual Studio processor workflow

The solution includes [OverkizCrestronDriver.WorkflowTests](OverkizCrestronDriver.WorkflowTests/README.md), using the published Crestron Home Test Adapter. It exposes the complete gated workflow in Test Explorer while the ordinary NUnit fixtures remain available for local testing. Configure its private settings before execution; hosted CI verifies discovery without accessing hardware.

## Publishing when local hardware is unavailable

The publish/release workflows support an explicit manual override when the processor or local self-hosted GitHub Actions runner is unavailable. Select `skip_hardware_checks` and provide a single-line `hardware_skip_reason`. Use the workflow's normal source and version controls. The override applies only to that invocation and is recorded with the exact source revision in its warning and job summary; it does not create a passing hardware-test result.

GitHub-hosted validation remains mandatory for the checked-out source, and the normal build, tests and packaging steps still run. Wait for the configured hosted workflows to pass, or run them on the same source revision first. None of these hosted checks needs the local runner or processor. Automatic tag/release-triggered runs retain the normal hardware checks; use a manual invocation of the updated release workflow when an offline override is needed.
