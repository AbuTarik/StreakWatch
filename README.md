# StreakWatch

StreakWatch is a Windows utility that monitors selected Twitch channels and coordinates a signed Firefox Browser Bridge to keep managed LIVE tabs open, muted at the tab level, deduplicated, and monitored for playback problems.

## Current versions

- Windows app: **v9.3.5**
- Browser Bridge source: **v0.3.0**
- Platform: Windows x64
- Browser: Firefox

## What it does

- Detects configured Twitch channels going LIVE/OFFLINE.
- Opens real Twitch channel pages through the Firefox Browser Bridge.
- Keeps one managed tab per LIVE channel and removes duplicates.
- Uses **tab-level mute only**; it does not modify Twitch player volume.
- Reports playback status back to StreakWatch.
- Includes network recovery, Firefox/Bridge health checks, Discord webhook notifications, recovery reminders, Safety timer, logs, diagnostics, and restart support.
- Stores each user's configuration under `%APPDATA%\StreakWatch`.

## Important limitations

StreakWatch does **not** fake views, manipulate rewards, or emulate Twitch Watch Streak credit. It opens and monitors real Twitch pages. Twitch ultimately decides whether viewing or a Watch Streak is counted.

LIVE detection currently uses Twitch web GraphQL/HLS signals that are not a documented public API and may change.

## Build

Requirements for building from source:

- Windows 10/11 x64
- .NET 10 SDK

From `src/StreakWatch`:

```powershell
dotnet publish StreakWatchV9.3.5.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The resulting `StreakWatch.exe` is self-contained, so end users do not need the .NET runtime.

## Firefox Browser Bridge

Source is under `src/BrowserBridge`.

For normal Firefox Stable installation, the extension must be Mozilla-signed. Do not ask end users to use `about:debugging` temporary installation.

The Native Messaging host uses extension ID:

```text
streakwatch-bridge@local
```

## User data and secrets

The repository and release package must never include a developer's `%APPDATA%\StreakWatch` folder.

Discord webhook URLs are stored locally in:

```text
%APPDATA%\StreakWatch\discord-secret.json
```

They are not part of source control.

## Releases

End users should download the prepared Windows package from **GitHub Releases**, not the source ZIP.

The developer can run `BUILD-RELEASE.bat` from this repository after cloning it on Windows to create a self-contained release package.

## License

No open-source license has been selected yet. Until a license is added, normal copyright rules apply.
