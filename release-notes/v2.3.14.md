# OverkizCrestronDriver 2.3.14

This patch corrects the dependency and lifecycle issues described below.

- Use OverkizClient 2.2.0.
- Coalesce periodic shade polls and suppress results from stopped shades or polls superseded by newer state updates.
- Preserve position and motion fields missing or invalid in a partial response; accept real endpoint values of 0 and 100. Ignore late disposal errors only for retired clients.

## Installation

Use the driver package from [GitHub](https://github.com/oznetmaster/OverkizCrestronDriver/releases/tag/v2.3.14) or the matching NuGet distribution. Import the package, then update the existing driver in Home Setup or Configure Pro. Importing alone does not update the running instance. Existing pairing and configuration formats are retained; removal and re-add is not a routine update step.

## Validation

Passed 164 workstation Debug and 164 Release test executions, plus 129 native net472/Mono processor tests. Processor validation used fake transports and synthetic credentials; no new physical-device acceptance or endurance run is claimed. Existing processor configuration and all pairing files were preserved. See [development history](DEVELOPMENT-HISTORY.md) for provenance and limits.

This GitHub/NuGet release does not replace any submitted Crestron package. Submission-specific PDFs and correspondence are excluded from public artifacts.
