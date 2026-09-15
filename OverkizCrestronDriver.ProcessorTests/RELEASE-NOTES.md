# OverkizCrestronDriver Tests

## Unreleased

- Add a separate, optional Live Gateway suite with three read-only tests. The package contains 50 tests in total.
- Supply an existing gateway token through private runner inputs. Tests verify discovery, child identity and reconnection without moving shades or generating tokens.

## 1.0.0 — 2026-09-14

- 25 offline tests and 22 SDK lifecycle tests, shared between desktop validation and the net472 processor package.
- Cover device filtering, restored room groups, stable children across discovery, rename/removal and late login/discovery completion. Clearing or disposing the platform now removes child controllers and rejects stale work. Reconnect creates and disposes a separate HTTP client for each connection, avoiding attempts to change BaseAddress after requests have started.
- Install the standalone test package from Configure’s **Utility** category. Select suites using its Home tile or the Windows NUnit runner.
- Processor test packages are GitHub release assets and are not published to NuGet. Private test inputs and deployment settings are excluded.
