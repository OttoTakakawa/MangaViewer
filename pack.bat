@echo off
chcp 65001 >nul
title MangaReader Packager

echo === MangaReader Packager ===
echo.
echo  1) Standalone (self-contained, ~60MB, no .NET needed)
echo  2) Runtime-dep (lightweight, requires .NET 8 Runtime)
echo.
set /p mode=Choice [1/2], default 1:
echo.
if "%mode%"=="2" (
    echo Packing runtime-dep mode...
    powershell -ExecutionPolicy Bypass -File "%~dp0pack.ps1" -Mode runtime-dep
) else (
    echo Packing standalone mode...
    powershell -ExecutionPolicy Bypass -File "%~dp0pack.ps1" -Mode standalone
)
echo.
echo Output: _release\
pause
