@echo off
rem Builds a single self-contained MultiLogin.exe into .\dist (no .NET install needed to run it).
cd /d "%~dp0"
dotnet publish MultiLogin\MultiLogin.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Done: %~dp0dist\MultiLogin.exe
pause
