@echo off
REM ---------------------------------------------------------------------------
REM  NavEx uninstaller — removes the Plugins\NavEx folder from every installed
REM  Navisworks Manage 2024 / 2025 / 2026 / 2027.
REM
REM  Right-click this file and choose "Run as administrator".
REM ---------------------------------------------------------------------------
setlocal

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo   Needs administrator rights. Right-click and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo.
echo   NavEx uninstaller
echo   =================
echo.

call :remove 2024
call :remove 2025
call :remove 2026
call :remove 2027

echo.
echo   Done.
echo.
pause
exit /b 0

:remove
set DEST=C:\Program Files\Autodesk\Navisworks Manage %~1\Plugins\NavEx
if exist "%DEST%" (
    rmdir /s /q "%DEST%"
    if exist "%DEST%" (
        echo   [ FAIL ] Navisworks Manage %~1 - could not remove
    ) else (
        echo   [  ok  ] Navisworks Manage %~1 - removed
    )
) else (
    echo   [ skip ] Navisworks Manage %~1 - not installed
)
exit /b 0
