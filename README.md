# OverkizCrestronDriver

A **Crestron Home** extension driver that integrates Overkiz-compatible smart-home gateways
(Somfy TaHoma, Atlantic Cozytouch, Hitachi Hi Kumo, and others) as shade/blind devices,
supporting both cloud and local LAN connections.

[![License: MIT + Commons Clause](https://img.shields.io/badge/License-MIT%20%2B%20Commons%20Clause-blue.svg)](LICENSE)

---

## What is New in V2

- **Room aggregate entities** — group multiple shades into a single room tile with per-slot individual controls and shared room-level open/close/stop/my commands
- **Display name overrides** — map raw Overkiz API labels to human-friendly names via the `ShadeDisplayNames` config field, without affecting room-matching logic
- **Config-driven room grouping** — define rooms and member assignments via the `RoomGroups` config field; no code changes required when adding or reorganising blinds
- **Rebuilt on Crestron DeviceDrivers SDK V2** — uses `ReflectedAttributeDriverEntity` and the extension UI mechanism directly, with no RAD base class dependency

> The V1 driver source is preserved in `archive/v1` and tagged `v1.0`.

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
- Automatically discovers all shades/blinds on the gateway and exposes each as a managed child device
- Per-shade UI with position control (two-way devices), open/stop/close buttons, and optional **My** position button
- Optional room aggregate entities grouping multiple shades with per-slot subheadings and room-level commands
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
2. Upload the `.pkg` to your Crestron Home processor via the deployment script or manually via SFTP to `/user/ThirdPartyDrivers/Import`.
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

---

## Configuration

### Room Groups

Groups shades into room aggregate entities. Format:

```
RoomName:Display Title | ApiLabel1:Slot1 Name | ApiLabel2:Slot2 Name
```

Multiple rooms are separated by semicolons. Example:

```
Lounge:Lounge Blinds | Lounge Left Blind:Left | Lounge Center Blind:Centre | Lounge Right Blind:Right
```

- `RoomName` — a unique key used internally for matching (case-insensitive)
- `Display Title` — the visible label for the room tile
- `ApiLabel` — the exact name of the shade as it appears in the Overkiz app
- `Slot Name` — the subheading shown under each slot in the room tile

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

## Deployment Script

`Deploy.ps1` automates uploading to the processor:

```powershell
.\Deploy.ps1 -ProcessorIP 192.168.x.x -User admin -Password yourpassword
.\Deploy.ps1 -ProcessorIP 192.168.x.x -User admin -Password yourpassword -Clean
```

`-Clean` removes the old driver entry from the processor's internal manifest and `UsedThirdPartyDrivers` folder before importing the new version — useful during development.

---

## Building from Source

### Dependencies

- [OverkizClient](https://www.nuget.org/packages/OverkizClient) NuGet package (restored automatically)
- [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit) NuGet package
- [ILRepack](https://github.com/gluck/il-repack) (via `ILRepackMerge.ps1`) to merge dependencies into a self-contained driver DLL

### Build

```powershell
dotnet build -c Release
```

The build pipeline:
1. Compiles the driver targeting `net472`
2. ILRepacks all runtime dependencies into a single `Shade_Overkiz_IP_V2.dll`
3. Patches the merged assembly
4. Packages everything into `Shade_Overkiz_IP_V2.pkg` using Crestron's ManifestUtil

---

## License

MIT + Commons Clause © 2026 Neil Colvin — see [LICENSE](LICENSE).

Free to use and modify. You may not sell the Software as a standalone product or sublicense it.
Commercial system integration work (e.g. a Crestron installer commissioning a customer system) is
explicitly permitted, even where a fee is charged for that service.

> **Note:** This project references [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit),
> which is subject to Crestron's SDK license agreement. That license governs the SDK libraries only;
> the source code in this repository is licensed independently under the terms above.
