# OverkizCrestronDriver

A **Crestron Home** platform driver that integrates Overkiz-compatible smart-home gateways
(Somfy TaHoma, Atlantic Cozytouch, Hitachi Hi Kumo, and others) as managed shade/blind devices,
supporting both cloud and local LAN connections.

[![License: MIT + Commons Clause](https://img.shields.io/badge/License-MIT%20%2B%20Commons%20Clause-blue.svg)](LICENSE)

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

This makes it one of the very few — and quite possibly the **only publicly available** — Crestron
Home extension driver built entirely on the V2 entity model without a RAD base class.

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

---

## Prerequisites

| Requirement | Details |
|---|---|
| Crestron Home processor | Running a firmware version compatible with extension drivers |
| Overkiz-compatible gateway | Somfy TaHoma Switch, TaHoma Premium, Connectivity Kit, etc. |
| Cloud account **or** local token | Somfy account for cloud mode; developer-mode token for local mode |

---

## Installation

1. Build the project in **Release** configuration — this produces `Shade_Overkiz_IP_V2.pkg` in the output folder.
2. Upload the `.pkg` to your Crestron Home processor manually (for example via SFTP to `/user/ThirdPartyDrivers/Import`).
3. In the Crestron Home **Configure** application, add a new device and select the **Tahoma Gateway** driver.
4. Fill in the connection configuration:

| Field | Description |
|---|---|
| Cloud Username | Your Somfy / Overkiz account e-mail (cloud mode only) |
| Cloud Password | Your account password (cloud mode only) |
| Cloud Server | The Overkiz server your account belongs to (default: SomfyEurope) |
| Gateway IP | LAN IP address or hostname of your gateway (local mode only) |
| Local API Token | Developer-mode bearer token from your Somfy cloud account (local mode only) |
| Room Groups | Room grouping and member configuration (see Configuration below) |
| Shade Display Names | Display name overrides for individual shades (see Configuration below) |

If either local-mode field is supplied, the driver treats the configuration as local mode and requires both local values. Otherwise it requires the cloud username and password.

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
- [ILRepack](https://github.com/gluck/il-repack) (via `ILRepackMerge.ps1`) to merge dependencies into a self-contained driver DLL
- `PatchMergedAssembly.ps1` to rewrite merged `System.*` helper types that Crestron Home's Mono sandbox rejects during reflection
- `ManifestUtil.exe` from the Crestron Driver SDK to produce the final `.pkg`

### Build

```powershell
dotnet build -c Release
```

The build pipeline:
1. Compiles the driver targeting `net472`
2. Bumps `DriverVersion` and `VersionDate` in `Shade_Overkiz_IP_V2.json`
   - **Release** builds increment the 3rd component and reset the 4th to `0000`
   - **Debug** builds increment only the 4th component
3. ILRepacks runtime dependencies into a single `Shade_Overkiz_IP_V2.dll`
4. Runs `PatchMergedAssembly.ps1` against the merged assembly
5. Packages everything into `Shade_Overkiz_IP_V2.pkg` using Crestron's ManifestUtil

---

## License

MIT + Commons Clause © 2026 Neil Colvin — see [LICENSE](LICENSE).

Free to use and modify. You may not sell the Software as a standalone product or sublicense it.
Commercial system integration work (e.g. a Crestron installer commissioning a customer system) is
explicitly permitted, even where a fee is charged for that service.

> **Note:** This project references [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit),
> which is subject to Crestron's SDK license agreement. That license governs the SDK libraries only;
> the source code in this repository is licensed independently under the terms above.
