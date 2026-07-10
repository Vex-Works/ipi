@echo off
cd /d "%~dp0"
dotnet run --project apps\windows\Ipi.Desktop\Ipi.Desktop.csproj
pause
