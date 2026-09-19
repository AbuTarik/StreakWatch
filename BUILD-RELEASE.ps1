$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir = Join-Path $root "src\StreakWatch"
$project = Join-Path $appDir "StreakWatchV10.1.3.csproj"
$publish = Join-Path $root "dist\publish"
$package = Join-Path $root "dist\StreakWatch-v10.1.3-win-x64"
$zip = Join-Path $root "dist\StreakWatch-v10.1.3-win-x64.zip"

Remove-Item (Join-Path $root "dist") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish | Out-Null
New-Item -ItemType Directory -Force -Path $package | Out-Null

Write-Host "Publishing StreakWatch v10.1.3..."
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item (Join-Path $publish "StreakWatch.exe") $package -Force

@'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-prebuilt.ps1"
pause
'@ | Set-Content (Join-Path $package "INSTALL.bat") -Encoding ASCII

@'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
'@ | Set-Content (Join-Path $package "UNINSTALL.bat") -Encoding ASCII

@'
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceExe = Join-Path $root "StreakWatch.exe"
$installDir = Join-Path $env:LOCALAPPDATA "StreakWatch"
$exe = Join-Path $installDir "StreakWatch.exe"

Get-Process StreakWatch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $sourceExe $exe -Force

$hostDir = Join-Path $env:APPDATA "StreakWatch\NativeMessaging"
New-Item -ItemType Directory -Force -Path $hostDir | Out-Null
$cmdPath = Join-Path $hostDir "streakwatch-native-host.cmd"
"@echo off`r`n`"$exe`" --native-host" | Set-Content -Path $cmdPath -Encoding ASCII

$manifestPath = Join-Path $hostDir "com.streakwatch.bridge.json"
$escapedCmd = $cmdPath.Replace("\", "\\")
@"
{
  "name": "com.streakwatch.bridge",
  "description": "StreakWatch Firefox native messaging bridge",
  "path": "$escapedCmd",
  "type": "stdio",
  "allowed_extensions": ["streakwatch-bridge@local"]
}
"@ | Set-Content -Path $manifestPath -Encoding UTF8

$reg = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.streakwatch.bridge"
New-Item -Force -Path $reg | Out-Null
Set-Item -Path $reg -Value $manifestPath

$ws = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\StreakWatch.lnk"
$sc = $ws.CreateShortcut($startMenu)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $installDir
$sc.IconLocation = "$exe,0"
$sc.Save()

$desktop = [Environment]::GetFolderPath("Desktop")
$desktopShortcut = Join-Path $desktop "StreakWatch.lnk"
$dsc = $ws.CreateShortcut($desktopShortcut)
$dsc.TargetPath = $exe
$dsc.WorkingDirectory = $installDir
$dsc.IconLocation = "$exe,0"
$dsc.Save()

Write-Host "Installed StreakWatch:"
Write-Host $exe
Write-Host ""
Write-Host "Opening the signed Browser Bridge..."
$bridgeUrl = "https://github.com/AbuTarik/StreakWatch/releases/latest/download/StreakWatch-Browser-Bridge-1.0.4.xpi"
try { Start-Process $bridgeUrl } catch { Start-Process "https://github.com/AbuTarik/StreakWatch/releases/latest" }
Start-Process $exe
'@ | Set-Content (Join-Path $package "install-prebuilt.ps1") -Encoding UTF8

@'
$ErrorActionPreference = "SilentlyContinue"
Get-Process StreakWatch | Stop-Process -Force
Remove-Item "HKCU:\Software\Mozilla\NativeMessagingHosts\com.streakwatch.bridge" -Recurse -Force
Remove-Item (Join-Path $env:APPDATA "StreakWatch\NativeMessaging") -Recurse -Force
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\StreakWatch.lnk") -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath("Desktop")) "StreakWatch.lnk") -Force
Remove-Item (Join-Path $env:LOCALAPPDATA "StreakWatch") -Recurse -Force
Write-Host "StreakWatch application files removed."
Write-Host "User settings under %APPDATA%\StreakWatch were kept except NativeMessaging."
'@ | Set-Content (Join-Path $package "uninstall.ps1") -Encoding UTF8

@'
StreakWatch v10.1.3 - Windows x64 Test Release

INSTALL
1. Extract this ZIP.
2. Double-click INSTALL.bat.
3. Install the Mozilla-signed StreakWatch Browser Bridge XPI from the same GitHub Release.
4. Open Firefox.
5. StreakWatch should show: Bridge: Connected.

NOTES
- No .NET installation is required.
- Your settings/logs are stored separately in %APPDATA%\StreakWatch.
- Closing X hides StreakWatch to the tray. Use tray -> Exit to stop it.
- StreakWatch does not emulate Twitch views or guarantee Watch Streak credit.
'@ | Set-Content (Join-Path $package "README.txt") -Encoding UTF8

$hash = Get-FileHash (Join-Path $package "StreakWatch.exe") -Algorithm SHA256
"$($hash.Hash.ToLower())  StreakWatch.exe" | Set-Content (Join-Path $package "SHA256SUMS.txt") -Encoding ASCII

Compress-Archive -Path (Join-Path $package "*") -DestinationPath $zip -Force
Write-Host ""
Write-Host "Release created:"
Write-Host $zip
Write-Host ""
Get-FileHash $zip -Algorithm SHA256
