# OverkizCrestronDriver

A **Crestron Home** extension driver that integrates Overkiz-compatible smart-home gateways
(Somfy TaHoma, Atlantic Cozytouch, Hitachi Hi Kumo, and others) as shade/blind devices,
supporting both cloud and local LAN connections.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Features

- Cloud connection via Somfy OAuth 2.0 (and other Overkiz-based cloud servers)
- Local LAN connection using a Somfy developer-mode bearer token
- Automatically discovers all shades/blinds on the gateway and exposes each as a managed child device
- Per-shade UI with position control (two-way devices), open/stop/close buttons, and optional **My** position button
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

1. Build the project in **Release** configuration — this produces `Shade_Overkiz_IP.pkg` in the output folder.
2. Upload the `.pkg` to your Crestron Home processor via the deployment script or manually via SFTP to `/user/ThirdPartyDrivers/Import`.
3. In the Crestron Home **Configure** application, add a new device and select the **Overkiz Gateway** driver.
4. Fill in the connection configuration:

| Field | Description |
|---|---|
| Cloud Username | Your Somfy / Overkiz account e-mail (cloud mode only) |
| Cloud Password | Your account password (cloud mode only) |
| Cloud Server | The Overkiz server your account belongs to (default: SomfyEurope) |
| Gateway IP | LAN IP address or hostname of your gateway (local mode only) |
| Local API Token | Developer-mode bearer token from your Somfy cloud account (local mode only) |

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
2. ILRepacks all runtime dependencies into a single `Shade_Overkiz_IP.dll`
3. Patches the merged assembly
4. Packages everything into `Shade_Overkiz_IP.pkg` using Crestron's ManifestUtil

---

## License

MIT © 2026 Neil Colvin — see [LICENSE](LICENSE).

> **Note:** This project references [Crestron.DeviceDrivers.DevKit](https://www.nuget.org/packages/Crestron.DeviceDrivers.DevKit),
> which is subject to Crestron's SDK license agreement. That license governs the SDK libraries only;
> the source code in this repository is MIT-licensed independently.
