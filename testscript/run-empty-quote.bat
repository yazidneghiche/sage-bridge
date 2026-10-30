@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0bin\TestEmptyQuote.exe" (
    echo Lance d'abord build-empty-quote.bat.
    pause
    exit /b 1
)
echo Test : creation devis vide pour TEST, transformation, puis ajout des articles.
"%~dp0bin\TestEmptyQuote.exe" "%~dp0config\config.json"
set "TEST_EXIT=%ERRORLEVEL%"
echo Code retour : %TEST_EXIT%
echo Chaque lancement cree un nouvel essai. Conserve le resultat avant de relancer.
pause
exit /b %TEST_EXIT%
