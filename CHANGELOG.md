# Changelog

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
