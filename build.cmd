@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\SptLinuxInstaller.App\SptLinuxInstaller.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\windows
if errorlevel 1 (
  echo Build failed. Install the .NET 10 SDK and try again.
  pause
  exit /b 1
)
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md artifacts/windows/; Copy-Item licenses,docs artifacts/windows/ -Recurse -Force"
if errorlevel 1 (
  echo Documentation packaging failed.
  pause
  exit /b 1
)
echo.
echo Ready: artifacts\windows\SPT-Linux-Installer.exe
pause
