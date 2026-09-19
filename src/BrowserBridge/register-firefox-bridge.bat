@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APPDIR=%~dp0.."
for %%I in ("%APPDIR%") do set "APPDIR=%%~fI"

set "EXE=%APPDIR%\StreakWatch.exe"
if not exist "%EXE%" (
  echo ERROR: StreakWatch.exe was not found here:
  echo %EXE%
  echo.
  echo Copy this FirefoxExtension folder next to the published StreakWatch.exe first.
  pause
  exit /b 1
)

set "HOSTDIR=%APPDATA%\StreakWatch\NativeMessaging"
if not exist "%HOSTDIR%" mkdir "%HOSTDIR%"

set "HOSTJSON=%HOSTDIR%\com.streakwatch.bridge.json"
set "HOSTCMD=%HOSTDIR%\streakwatch-native-host.cmd"

(
  echo @echo off
  echo "%EXE%" --native-host
) > "%HOSTCMD%"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$o=[ordered]@{name='com.streakwatch.bridge';description='StreakWatch Firefox native messaging bridge';path='%HOSTCMD%';type='stdio';allowed_extensions=@('streakwatch-bridge@local')};" ^
  "$o | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 '%HOSTJSON%'"


reg add "HKCU\Software\Mozilla\NativeMessagingHosts\com.streakwatch.bridge" /ve /t REG_SZ /d "%HOSTJSON%" /f >nul

echo.
echo Firefox native bridge registered successfully.
echo Host manifest:
echo %HOSTJSON%
echo.
echo Now load manifest.json from about:debugging in Firefox.
pause
