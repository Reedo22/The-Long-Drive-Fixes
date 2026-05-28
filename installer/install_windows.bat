@echo off
REM TLD Public MP v2.4 - Windows installer
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "BUNDLED_VERSION=v2.4"

REM ---- self-update from github ----
REM Skips if TLDMP_SKIP_UPDATE is set, if already re-executed once, or if PowerShell isn't available.
if defined TLDMP_SKIP_UPDATE goto :postupdate
if defined TLDMP_UPDATED     goto :postupdate

echo Checking github for a newer release...
set "PS_OUT=%TEMP%\tldmp_latest.txt"
if exist "%PS_OUT%" del "%PS_OUT%" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-RestMethod -Uri 'https://api.github.com/repos/Reedo22/The-Long-Drive-Fixes/releases/latest' -TimeoutSec 8 -ErrorAction Stop; $tag = $r.tag_name; $url = ($r.assets ^| Where-Object { $_.name -like '*.zip' } ^| Select-Object -First 1).browser_download_url; if ($tag -and $url) { \"$tag|$url\" ^| Out-File -Encoding ASCII -FilePath '%PS_OUT%' } } catch { }" >nul 2>&1

if not exist "%PS_OUT%" (
    echo   github check skipped ^(offline or PowerShell not available^)
    goto :postupdate
)

set "LATEST_TAG="
set "ZIP_URL="
for /f "usebackq tokens=1,2 delims=|" %%a in ("%PS_OUT%") do (
    set "LATEST_TAG=%%a"
    set "ZIP_URL=%%b"
)
del "%PS_OUT%" >nul 2>&1

if "%LATEST_TAG%"=="" goto :postupdate
if "%LATEST_TAG%"=="%BUNDLED_VERSION%" (
    echo   bundled %BUNDLED_VERSION% is current
    goto :postupdate
)

echo   bundled = %BUNDLED_VERSION%, latest = %LATEST_TAG% -- fetching newer release
set "TMPROOT=%TEMP%\tldmp_update_%RANDOM%"
mkdir "%TMPROOT%" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri '%ZIP_URL%' -OutFile '%TMPROOT%\release.zip' -TimeoutSec 60 -ErrorAction Stop; Expand-Archive -Path '%TMPROOT%\release.zip' -DestinationPath '%TMPROOT%' -Force -ErrorAction Stop } catch { }" >nul 2>&1

if exist "%TMPROOT%\install_windows.bat" (
    echo   re-executing %LATEST_TAG% installer...
    set "TLDMP_UPDATED=1"
    call "%TMPROOT%\install_windows.bat" %*
    exit /b !errorlevel!
)
echo   update fetch failed, falling back to bundled %BUNDLED_VERSION%

:postupdate
set "SRC=%SCRIPT_DIR%files"

set "TARGET="
if not "%~1"=="" (
    set "TARGET=%~1"
) else (
    for %%P in (
        "%ProgramFiles(x86)%\Steam\steamapps\common\The Long Drive Public"
        "%ProgramFiles(x86)%\Steam\steamapps\common\The Long Drive"
        "C:\Program Files (x86)\Steam\steamapps\common\The Long Drive Public"
        "C:\Program Files (x86)\Steam\steamapps\common\The Long Drive"
        "D:\SteamLibrary\steamapps\common\The Long Drive Public"
        "D:\SteamLibrary\steamapps\common\The Long Drive"
        "E:\SteamLibrary\steamapps\common\The Long Drive Public"
        "E:\SteamLibrary\steamapps\common\The Long Drive"
    ) do (
        if exist "%%~P\TheLongDrive.exe" (
            set "TARGET=%%~P"
            goto :found
        )
    )
)
:found
if "%TARGET%"=="" goto :notfound
if not exist "%TARGET%\TheLongDrive.exe" goto :notfound

echo Installing into: %TARGET%
echo.

if exist "%TARGET%\winhttp.dll" goto :backup
if exist "%TARGET%\BepInEx"      goto :backup
goto :copyfiles

:backup
for /f "tokens=2-4 delims=/ " %%a in ('date /t') do set DSTAMP=%%c%%a%%b
for /f "tokens=1-2 delims=: " %%a in ('time /t') do set TSTAMP=%%a%%b
set "BACKUP=%TARGET%\.TLDPublicMP_backup_%DSTAMP%_%TSTAMP%"
echo Existing BepInEx detected - backing up to %BACKUP%
mkdir "%BACKUP%" 2>nul
if exist "%TARGET%\winhttp.dll"         copy /Y "%TARGET%\winhttp.dll"         "%BACKUP%\" >nul
if exist "%TARGET%\doorstop_config.ini" copy /Y "%TARGET%\doorstop_config.ini" "%BACKUP%\" >nul
if exist "%TARGET%\.doorstop_version"   copy /Y "%TARGET%\.doorstop_version"   "%BACKUP%\" >nul
if exist "%TARGET%\steam_appid.txt"     copy /Y "%TARGET%\steam_appid.txt"     "%BACKUP%\" >nul
if exist "%TARGET%\BepInEx"             xcopy /E /I /H /Y "%TARGET%\BepInEx"   "%BACKUP%\BepInEx" >nul

:copyfiles
echo Copying BepInEx framework + plugins + patcher...
copy /Y "%SRC%\winhttp.dll"          "%TARGET%\" >nul
copy /Y "%SRC%\doorstop_config.ini"  "%TARGET%\" >nul
copy /Y "%SRC%\.doorstop_version"    "%TARGET%\" >nul 2>nul
copy /Y "%SRC%\steam_appid.txt"      "%TARGET%\" >nul

if not exist "%TARGET%\BepInEx\core"     mkdir "%TARGET%\BepInEx\core"
if not exist "%TARGET%\BepInEx\plugins"  mkdir "%TARGET%\BepInEx\plugins"
if not exist "%TARGET%\BepInEx\patchers" mkdir "%TARGET%\BepInEx\patchers"
if not exist "%TARGET%\BepInEx\config"   mkdir "%TARGET%\BepInEx\config"

xcopy /E /Y "%SRC%\BepInEx\core\*"      "%TARGET%\BepInEx\core\" >nul
xcopy /Y    "%SRC%\BepInEx\plugins\*"   "%TARGET%\BepInEx\plugins\" >nul
xcopy /Y    "%SRC%\BepInEx\patchers\*"  "%TARGET%\BepInEx\patchers\" >nul

for %%C in ("%SRC%\BepInEx\config\*.cfg") do (
    if not exist "%TARGET%\BepInEx\config\%%~nxC" copy /Y "%%C" "%TARGET%\BepInEx\config\" >nul
)

echo.
echo ===============================================================
echo TLD Public MP v2.4 installed.
echo.
echo Launch The Long Drive (public branch) via Steam normally.
echo The Multiplayer button should appear on the main menu.
echo.
echo Auto-update is ON; checks github on every launch.
echo.
echo Gameplay plugins:
echo   TLDMPUnlock              - re-enables the MP button
echo   TLDPubMPPatch            - ForceReliableSends + ForceMultiFlag
echo   TLDPubMPDiag             - packet diagnostic (passive)
echo   TLDPubDevMode            - dev menu + CapsLock fly + F4/F8/F3/End
echo   TLDDirectMP              - direct-connect fallback (Mode=Off)
echo   TLDPubBodyPush           - fixes pushed-item snap-back
echo   TLDPubPlayerStable       - 5s player-destroy timeout (was 1.5s)
echo   TLDPubFluidDedupe        - kills fluid packet spam
echo   TLDPubDriverAuthority    - protects driver inputs under latency
echo   TLDPubCarSync            - 20Hz car position broadcast (NEW)
echo   TLDPubRemoteCarKinematic - kinematic remote cars (NEW, kills bounce)
echo.
echo Dev-only (OFF by default - see README):
echo   TLDPubLoopback           - local two-instance test rig
echo   TLDPubFakeId             - SteamID swap for loopback testing
echo   TLDPubMPUpdater          - github manifest updater (patcher)
echo.
echo Logs: %TARGET%\BepInEx\LogOutput.log
echo Uninstall: uninstall_windows.bat
echo ===============================================================
pause
exit /b 0

:notfound
echo ERROR: could not find TLD install.
echo Pass the install path as the first argument, e.g.:
echo   install_windows.bat "C:\Games\The Long Drive"
pause
exit /b 1
