@echo off
chcp 65001 >nul
set EXE=%~dp0src\ForgePad.MixerTest\bin\Release\net10.0-windows\ForgePad.MixerTest.exe
if not exist "%EXE%" (
    echo Сначала собери: dotnet build src\ForgePad.MixerTest -c Release
    pause
    exit /b 1
)
"%EXE%" %*
pause
