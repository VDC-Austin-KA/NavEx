@echo off
REM ---------------------------------------------------------------------------
REM  NavEx installer
REM
REM  Copies NavEx.dll + NavEx.addin into the Plugins folder of every installed
REM  Navisworks Manage 2024 / 2025 / 2026 / 2027.
REM
REM  Only mkdir and copy are used, deliberately: no embedded payload, no
REM  certutil, nothing that looks like a self-extracting dropper to Defender.
REM
REM  Right-click this file and choose "Run as administrator".
REM ---------------------------------------------------------------------------
setlocal enabledelayedexpansion

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo   This installer writes into C:\Program Files and needs administrator rights.
    echo   Right-click Install.cmd and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo.
echo   NavEx installer
echo   ===============
echo.

tasklist /fi "imagename eq Roamer.exe" 2>nul | find /i "Roamer.exe" >nul
if not errorlevel 1 (
    echo   Navisworks is running. Close it before installing.
    echo.
    pause
    exit /b 1
)

set INSTALLED=0

call :install 2024 V24
call :install 2025 V25
call :install 2026 V26
call :install 2027 V27

echo.
if "%INSTALLED%"=="0" (
    echo   No Navisworks Manage 2024-2027 installation was found.
    echo   NavEx was not installed.
) else (
    echo   Done. Start Navisworks and look for NavEx on the Add-Ins ribbon tab.
)
echo.
pause
exit /b 0

:install
set YEAR=%~1
set SRC=%~dp0%~2
set DEST=C:\Program Files\Autodesk\Navisworks Manage %YEAR%

if not exist "%DEST%\Autodesk.Navisworks.Api.dll" (
    echo   [ skip ] Navisworks Manage %YEAR% not installed
    exit /b 0
)

if not exist "%SRC%\NavEx.dll" (
    echo   [ skip ] %~2\NavEx.dll missing from this download
    exit /b 0
)

if not exist "%DEST%\Plugins\NavEx" mkdir "%DEST%\Plugins\NavEx"

copy /y "%SRC%\NavEx.dll"   "%DEST%\Plugins\NavEx\" >nul
if errorlevel 1 (
    echo   [ FAIL ] Navisworks Manage %YEAR% - could not copy NavEx.dll
    exit /b 0
)

copy /y "%SRC%\NavEx.addin" "%DEST%\Plugins\NavEx\" >nul
if errorlevel 1 (
    echo   [ FAIL ] Navisworks Manage %YEAR% - could not copy NavEx.addin
    exit /b 0
)

echo   [  ok  ] Navisworks Manage %YEAR%
set INSTALLED=1
exit /b 0
