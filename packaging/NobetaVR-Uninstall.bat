@echo off
setlocal
rem Removes every file the NobetaVR archive added to the game folder, and nothing else.
rem Save games (LittleWitchNobeta_Data\Save) are never touched.
cd /d "%~dp0"

if not exist "LittleWitchNobeta.exe" (
    echo This file must sit in the game folder, next to LittleWitchNobeta.exe.
    pause
    exit /b 1
)

tasklist /fi "imagename eq LittleWitchNobeta.exe" | find /i "LittleWitchNobeta.exe" >nul
if not errorlevel 1 (
    echo Close the game first, then run this again.
    pause
    exit /b 1
)

echo This removes NobetaVR and BepInEx from:
echo   %CD%
echo Your save games are kept. Mod settings and any other BepInEx mods are removed.
echo.
choice /m "Uninstall NobetaVR"
if errorlevel 2 exit /b 0

for %%F in (winhttp.dll doorstop_config.ini .doorstop_version changelog.txt NobetaVR-LICENSE.txt NobetaVR-THIRD-PARTY.txt) do (
    if exist "%%F" del /f /q "%%F"
)
for %%D in (BepInEx dotnet LittleWitchNobeta_Data\UnitySubsystems) do (
    if exist "%%D\" rd /s /q "%%D"
)
rem The OpenXR provider shares this folder with the game's own libOni.dll and steam_api64.dll.
for %%F in (UnityOpenXR.dll openxr_loader.dll UnityOpenXR-LICENSE.md UnityOpenXR-THIRD-PARTY-NOTICES.md) do (
    if exist "LittleWitchNobeta_Data\Plugins\x86_64\%%F" del /f /q "LittleWitchNobeta_Data\Plugins\x86_64\%%F"
)

echo.
echo NobetaVR uninstalled. The game now starts without VR.
pause
rem Leave the batch context before deleting this file, so cmd does not read past its end.
(goto) 2>nul & del "%~f0"
