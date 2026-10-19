@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\SptLinuxInstaller.App\SptLinuxInstaller.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\windows
if errorlevel 1 (
  echo Build failed. Install the .NET 10 SDK and try again.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Portable.ps1
if errorlevel 1 (
  echo Packaging failed.
  pause
  exit /b 1
)
echo.
echo Ready: artifacts\windows\SPT-Linux-Installer.exe
echo ZIP: artifacts\SPT-Linux-Installer-Windows-x64.zip
pause
