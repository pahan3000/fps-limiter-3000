@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0pack-extension.ps1" (
    echo ERROR: pack-extension.ps1 was not found. Put both files in the FPS Limiter source folder.
    pause
    exit /b 1
)
echo Building Playnite FPS Limiter...
rem Clean build: stale obj files can hide XAML changes (missing control names / CS0103).
if exist "%~dp0obj" rd /s /q "%~dp0obj"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-extension.ps1"
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Do not install an older file from the dist folder.
    pause
    exit /b 1
)
if not exist "%~dp0dist" (
    echo ERROR: dist folder not found.
    pause
    exit /b 1
)
echo.
echo Build succeeded. Opening dist folder...
echo Drag the newly built .pext onto Playnite and restart Playnite.
start "" explorer.exe "%~dp0dist"
exit /b 0
