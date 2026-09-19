# StreakWatch

StreakWatch is a Windows utility that monitors selected Twitch channels and coordinates a Firefox Browser Bridge to manage real Twitch tabs for channels that are LIVE.

## Current versions

- Windows app: **v10.1.3**
- Browser Bridge source: **v1.0.4**
- Platform: Windows x64
- Browser: Firefox

## What it does

- Detects configured Twitch channels going LIVE/OFFLINE.
- Opens real Twitch channel pages through the Firefox Browser Bridge.
- Keeps StreakWatch-managed tabs deduplicated without touching unrelated manual Twitch tabs.
- Uses **Firefox tab-level mute only**; it does not set Twitch player `video.muted` or `video.volume`.
- Reports playback state and selected stream quality back to the Windows app.
- Includes network recovery, Firefox/Bridge health checks, Discord webhook notifications, recovery reminders, logs and diagnostics.
- Low Data Mode can limit simultaneous managed streams and queue additional LIVE channels.
- Stores per-user configuration under `%APPDATA%\StreakWatch`.

## v10.1.3 highlights

v10.1.3 fixes a Low Data Mode scheduling bug that could cause a LIVE channel waiting for a playback slot to repeatedly open and close a Firefox tab.

- Tab creation now uses only the channels admitted by the Low Data scheduler.
- Only one activation/prime operation may run per channel at a time.
- Failed playback-start verification enters a 90-second cooldown instead of repeatedly retrying every ~25 seconds.
- Low Data completion clears pending activation state for that stream.
- Manual Twitch tabs remain untouched.

See `docs/V10.1.3-LOW-DATA-SCHEDULER-FIX.txt` for technical details.

## Important limitations

StreakWatch does **not** fake views, manipulate rewards, or emulate Twitch Watch Streak credit. It opens and monitors real Twitch pages. Twitch ultimately decides whether viewing or a Watch Streak is counted.

LIVE detection currently uses Twitch web GraphQL/HLS signals that are not a documented public API and may change.

## Build

Requirements:

- Windows 10/11 x64
- .NET 10 SDK

From `src/StreakWatch`:

```powershell
dotnet publish StreakWatchV10.1.3.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Or run `BUILD-RELEASE.bat` from the repository root to create a self-contained Windows release package.

## Firefox Browser Bridge

Source is under `src/BrowserBridge`.

The Native Messaging host name is `com.streakwatch.bridge` and the Firefox extension ID is:

```text
streakwatch-bridge@local
```

For normal Firefox Stable distribution, use a Mozilla-signed build. Do not modify a signed XPI after Mozilla signs it.

## User data and secrets

Do not commit `%APPDATA%\StreakWatch` or local Discord webhook secrets. Discord webhook URLs are stored locally in:

```text
%APPDATA%\StreakWatch\discord-secret.json
```

## Releases

End users should use the prepared Windows package from GitHub Releases rather than the repository source ZIP.

## License

StreakWatch is released under the MIT License. See `LICENSE`.
