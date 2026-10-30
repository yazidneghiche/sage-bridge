@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0bin\AddProductToOrder.exe" (
    echo Lance d'abord build-add-product.bat.
    pause
    exit /b 1
)
if not exist "%~dp0config\config.json" (
    echo config\config.json introuvable.
    pause
    exit /b 1
)
rem Valeurs fixes pour ce test sur la commande du client TEST.
set "ORDER_PIECE=BC159602"
set "ARTICLE_REF=10102-3"
set "ARTICLE_QTY=1"
"%~dp0bin\AddProductToOrder.exe" "%~dp0config\config.json" "%ORDER_PIECE%" "%ARTICLE_REF%" "%ARTICLE_QTY%"
set "TEST_EXIT=%ERRORLEVEL%"
echo Code retour : %TEST_EXIT%
echo Chaque execution ajoute une ligne. Verifie le resultat avant de relancer.
pause
exit /b %TEST_EXIT%
