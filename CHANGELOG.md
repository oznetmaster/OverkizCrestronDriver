# Changelog

## 2026-09-15 - Test and development tooling (no driver release)

- Add the published Test Explorer workflow adapter, offline discovery CI and independent GitHub processor-test releases. Private workflow plans control optional live tests, actual-driver updates and temporary-instance cleanup.


- Add three optional live driver tests for authenticated gateway discovery, stable child identities and reconnection with an existing token. Share the private client-console settings; never generate tokens or operate shades. The processor package now contains 50 tests across unit, lifecycle and live suites.

## 2.3.5 — 2026-09-14

[Driver release notes](RELEASE-NOTES.md). Test-only changes do not require a driver release.

- Cover device filtering, restored room groups, stable children across discovery, rename/removal and late login/discovery completion. Clearing or disposing the platform now removes child controllers and rejects stale work. Reconnect creates and disposes a separate HTTP client for each connection, avoiding attempts to change BaseAddress after requests have started.

- Normalize the working manifest from `2.3.4.0` to `2.3.004.0001`; this retains the last already-generated Debug build number so the next build does not reuse it. No historical tags or packages are changed.

- Standardize driver versioning: Debug project/package metadata follows the manifest including its build increment; local Release builds preserve it; three-part release tags select the exact CI release without another patch increment. Verify source and built package versions before publication.


- Expand driver coverage to 25 offline tests and 22 SDK entity/lifecycle tests, with a desktop SDK harness and the same lifecycle fixtures in the net472 processor package.

- Add 25 NUnit driver unit tests and a processor lifecycle suite in the existing solution.
- Add a standalone Utility processor test package with private Debug deployment settings.

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project's driver package version follows the `Major.Minor.Release.Build`
scheme described in [README.md](README.md#building-from-source).

## [2.3.4.0] - 2026-09-12

### Changed

- Updated the `OverkizClient` NuGet package dependency from 1.1.3 to 1.2.0.
  - OverkizClient 1.2.0 introduces typed internal response models and stricter
	 response validation (invalid or incompatible API responses now fail fast
	 with a clear exception instead of being silently accepted). Public method
	 signatures and driver-facing behavior are unchanged for well-formed
	 gateway/cloud responses.

## [2.3.3.0] - 2026-06-18

### Fixed

- Newly added shade entities now publish their initial online/ready state
  immediately after controller registration, so they come online right away
  instead of requiring a driver reload.

## [2.3.2.1] - 2026-06-18

### Fixed

- Newly added room group entities are now reconciled, registered, and started
  in the correct order so they come online immediately after being added,
  instead of only appearing after a restart or reload.

## [2.3.2.0] - 2026-06-12

### Changed

- Updated the `OverkizClient` NuGet package dependency from 1.1.2 to 1.1.3.

## [2.3.1.0] and earlier

- See the [GitHub Releases](https://github.com/oznetmaster/OverkizCrestronDriver/releases)
  page for release notes prior to this changelog's introduction.