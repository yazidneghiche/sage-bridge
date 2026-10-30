@echo off
setlocal
cd /d "%~dp0"
set "SAGE_CONFIG=%~dp0config\config.json"
if not exist "%SAGE_CONFIG%" set "SAGE_CONFIG=%~dp0..\config\config.json"
if not exist "%SAGE_CONFIG%" (
    echo config.json introuvable dans config ou ..\config.
    pause
    exit /b 1
)
if not exist "%~dp0TransformDevis.exe" (
    echo Lance d'abord build-test.bat.
    pause
    exit /b 1
)
echo Transformation du devis existant DE157709 en commande.
"%~dp0TransformDevis.exe" "%SAGE_CONFIG%" DE157709
set "TEST_EXIT=%ERRORLEVEL%"
echo.
echo Code retour : %TEST_EXIT%
echo Controle les documents produits avant de relancer ce test.
pause
exit /b %TEST_EXIT%
