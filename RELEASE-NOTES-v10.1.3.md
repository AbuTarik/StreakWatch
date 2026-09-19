# StreakWatch v10.1.3

## Low Data Mode scheduler fix

This release fixes a tab lifecycle issue seen when multiple monitored channels were LIVE while `MaxSimultaneousStreams` was set to 1.

The scheduler correctly selected one channel, but tab creation could still iterate over all LIVE channels. A waiting channel could therefore be opened, closed by the next reconcile pass because it was outside the active slot, and then opened again.

### Fixed

- Firefox tab creation now follows only the Low Data scheduler's selected channel set.
- Waiting LIVE channels stay in `Low Data queue - waiting` without opening a managed tab.
- Only one playback activation operation can run for a channel at a time.
- A failed playback-start verification now uses a 90-second cooldown to prevent timeout storms.
- Pending activation state is cleared when the Low Data target is completed.
- Existing managed-tab deduplication and per-stream completion protection remain enabled.
- Per-channel mute remains Firefox tab-level only.

## Browser Bridge

Source version: **1.0.4**.

The Bridge contains the matching Low Data scheduling and activation hardening used by v10.1.3.
