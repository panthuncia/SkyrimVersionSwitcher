@echo off
setlocal
where dotnet >nul 2>&1
if errorlevel 1 (
    echo Error: Install the .NET 10 SDK to build this project. >&2
    exit /b 1
)

dotnet build "%~dp0SkyrimVersionSwitcher.slnx" --configuration Release %*
exit /b %errorlevel%
