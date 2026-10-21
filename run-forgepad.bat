@echo off
chcp 65001 >nul
set EXE=%~dp0src\ForgePad.App\bin\Release\net10.0-windows\ForgePad.App.exe
if not exist "%EXE%" (
    echo Сначала собери: dotnet build ForgePad.slnx -c Release
    pause
    exit /b 1
)
start "" "%EXE%" %*
