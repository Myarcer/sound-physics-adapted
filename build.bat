@echo off
REM Build script for Sound Physics Adapted mod
REM Builds the main project directly (solution file has broken references)
if "%1"=="" (
    dotnet build soundphysicsadapted.csproj --configuration Release
) else (
    dotnet build soundphysicsadapted.csproj --configuration %1
)
