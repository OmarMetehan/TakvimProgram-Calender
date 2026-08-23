@echo off
REM Takvim'i derler ve masaüstü uygulamasını başlatır.
cd /d "%~dp0"
dotnet run --project src\Takvim.Desktop --configuration Release
