@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0bin\TestFullQuote.exe" (
    echo Lance d'abord build-full-quote.bat.
    pause
    exit /b 1
)
echo Test : creation devis avec deux articles pour TEST, puis transformation uniquement.
"%~dp0bin\TestFullQuote.exe" "%~dp0config\config.json"
set "TEST_EXIT=%ERRORLEVEL%"
echo Code retour : %TEST_EXIT%
echo Chaque lancement cree un nouvel essai. Conserve le resultat avant de relancer.
pause
exit /b %TEST_EXIT%
