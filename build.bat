@echo off
rem Builds MultiLogin.exe into .\dist
rem   build.bat        -> small exe; the PC running it needs the .NET 10 Desktop Runtime
rem   build.bat full   -> self-contained exe that runs anywhere (downloads ~70 MB the first time)
cd /d "%~dp0"
set SELF=false
if /i "%1"=="full" set SELF=true
dotnet publish MultiLogin\MultiLogin.csproj -c Release -r win-x64 --self-contained %SELF% ^
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
