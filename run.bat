@echo off
cd /d %~dp0

if not exist bin\BridgeWorker.exe (
    echo ERREUR: BridgeWorker.exe introuvable !
    echo Lancez build.bat d'abord.
    pause
    exit /b
)

echo ===============================
echo   Lancement BridgeWorker
echo ===============================

bin\BridgeWorker.exe

echo.
echo ===== Execution terminee =====
pause