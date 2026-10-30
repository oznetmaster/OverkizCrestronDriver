# OverkizCrestronDriver 2.3.13

This patch improves gateway availability and automatic recovery, and makes optional room-group My labels readable on narrow displays. It incorporates the product fixes developed since public 2.3.7; the intervening versions were submission-validation candidates.

- Use OverkizClient 2.1.2 to recover a stale local event listener after gateway restart and classify the cloud quota error correctly. Existing backoff remains; shade commands are not automatically retried.
- Propagate confirmed gateway/API-path loss to dependent shade and room tiles, including one-way RTS devices. A successful authenticated local connection restores local gateway-path availability. Cloud API reachability remains separate from physical gateway availability.
- Preserve remembered two-way device availability, and reject results from retired connections or a disposed driver. One-way RTS state is inferred and remains distinct from measured position feedback.
- Display My labels without competing icons in optional room and slot button groups.

## Installation

Use `Shade_Overkiz_IP_V2.pkg` from the [GitHub release](https://github.com/oznetmaster/OverkizCrestronDriver/releases/tag/v2.3.13), or NuGet distribution `CrestronHomeDriver.Overkiz.Shades` 2.3.13. The wrapper contains the same standalone `.pkg`; it is not a DLL reference package. Confirm manifest version **2.3.013.0000** after importing and updating the existing driver in Home Setup or Configure Pro. Existing driver identity, settings, children and controls remain compatible; removal and re-add is not a routine upgrade step.

Normal public package and assembly names remain `Shade_Overkiz_IP_V2`. Developer-qualified names and the additional help/checklist/annex/correspondence are specific to Crestron submission. This public release does not assert catalog publication or change the previously submitted package.

## Validation and limits

Fresh release validation is recorded in [development history](https://github.com/oznetmaster/OverkizCrestronDriver/blob/master/DEVELOPMENT-HISTORY.md). Historical gateway interruption/recovery, UI and endurance observations remain attributed to their actual candidate versions. They are not a new endurance run or a claim that every Crestron submission requirement passed. Gateway loss detection is bounded by the existing transport timeout, confirmation and backoff; no universal 15-second detection guarantee is made. Physical testing to date is limited to the available Somfy TaHoma and one-way Office Blind; group actuation, two-way hardware and other ecosystems are not newly certified by this release.

Support: [contact form](https://oznetmaster.github.io/support/).
