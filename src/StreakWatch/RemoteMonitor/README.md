# StreakWatch Remote Monitor foundation

This folder is the safe foundation for future 24/7 VPS deployment.

V9.3.0 writes `%APPDATA%\StreakWatch\remote-monitor-snapshot.json` with channel detection state.
A future server component can independently detect LIVE/OFFLINE and send Discord notifications.

It intentionally does **not** emulate Twitch viewing, log into a Twitch account, farm rewards, or claim Watch Streak credit.
Actual viewing remains in the signed Firefox Bridge on the user's PC.

Recommended future architecture:
- VPS: LIVE/OFFLINE detection + Discord alerts + health monitoring.
- PC: signed Firefox Bridge + real Twitch page playback when the PC is available.
