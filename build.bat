@echo off
rem Builds the single-file exe into bin\publish\YoutubeToMp4.exe (runs publish.ps1).
rem Close the running app first, or the build fails on the locked exe.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)
echo.
echo Build succeeded: %~dp0bin\publish\YoutubeToMp4.exe
pause
