@echo off
rem Builds and runs the app from source (requires the .NET 10 SDK).
cd /d "%~dp0"
dotnet run --project MultiLogin\MultiLogin.csproj -c Release
