@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0bin\TestOrderPrices.exe" (
    echo Lance d'abord build-order-prices.bat.
    pause
    exit /b 1
)
echo Test prix : BC159605, 10017-1 a 10.50 HT, 10102-3 a 25.00 HT.
"%~dp0bin\TestOrderPrices.exe" "%~dp0config\config.json"
set "TEST_EXIT=%ERRORLEVEL%"
echo Code retour : %TEST_EXIT%
echo Ce test modifie les prix existants, sans ajouter de ligne ni creer de document.
pause
exit /b %TEST_EXIT%
