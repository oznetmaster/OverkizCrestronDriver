# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project's driver package version follows the `Major.Minor.Release.Build`
scheme described in [README.md](README.md#building-from-source).

This changelog records shipped features, fixes, compatibility and runtime dependency changes. See [development and validation history](DEVELOPMENT-HISTORY.md) for tests, CI, build tooling and work not yet released.

## 2.3.6 — 2026-09-22

- Update OverkizClient to 2.0.0 and Crestron.DeviceDrivers.DevKit to 29.0.10, retaining the existing driver configuration fields, device identities and controls.
- Remove unused log4net, Polly and related merge dependencies.
- Preserve serialization-attribute references while patching the merged assembly so the updated models package correctly.

This is a compatible driver update; the client library's breaking API changes are handled internally. See [release notes](RELEASE-NOTES.md).

## 2.3.5 — 2026-09-14

[Driver release notes](RELEASE-NOTES.md). Test-only changes do not require a driver release.

- Clearing or disposing the platform now removes child controllers and rejects stale work. Reconnect creates and disposes a separate HTTP client for each connection, avoiding attempts to change BaseAddress after requests have started.

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