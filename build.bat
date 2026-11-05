@echo off
rem Builds a small single-file ProfileRefresher.exe into .\publish
rem Requires the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
cd /d "%~dp0"
dotnet publish ProfileRefresher.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 (
  echo.
  echo Build failed. Make sure the .NET 10 SDK is installed.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0publish\ProfileRefresher.exe
pause
