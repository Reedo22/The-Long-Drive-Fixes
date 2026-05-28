@echo off
setlocal
set "TARGET="
if not "%~1"=="" ( set "TARGET=%~1" ) else (
    for %%P in (
        "%ProgramFiles(x86)%\Steam\steamapps\common\The Long Drive Public"
        "%ProgramFiles(x86)%\Steam\steamapps\common\The Long Drive"
        "D:\SteamLibrary\steamapps\common\The Long Drive Public"
        "D:\SteamLibrary\steamapps\common\The Long Drive"
        "E:\SteamLibrary\steamapps\common\The Long Drive Public"
        "E:\SteamLibrary\steamapps\common\The Long Drive"
    ) do (
        if exist "%%~P\BepInEx" ( set "TARGET=%%~P" & goto :found )
    )
)
:found
if "%TARGET%"=="" goto :notfound
if not exist "%TARGET%\TheLongDrive.exe" goto :notfound
echo Removing TLD Public MP from: %TARGET%
del /Q "%TARGET%\winhttp.dll"         2>nul
del /Q "%TARGET%\doorstop_config.ini" 2>nul
del /Q "%TARGET%\.doorstop_version"   2>nul
del /Q "%TARGET%\steam_appid.txt"     2>nul
if exist "%TARGET%\BepInEx" rmdir /S /Q "%TARGET%\BepInEx"
echo Done. Backups in %TARGET%\.TLDPublicMP_backup_*
pause
exit /b 0
:notfound
echo ERROR: could not find TLD install.
pause
exit /b 1
