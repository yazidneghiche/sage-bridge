@echo off
cd /d "%~dp0"

if not exist "bin\BridgeWorkerAccount.exe" (
    echo ERREUR : BridgeWorkerAccount.exe introuvable.
    echo Lancez build-account.bat d'abord.
    pause
    exit /b 1
)

echo ===============================
echo   Lancement BridgeWorkerAccount
echo ===============================

"bin\BridgeWorkerAccount.exe" --config "config\config.accounts.json"

echo.
echo ===== Execution terminee =====
pause