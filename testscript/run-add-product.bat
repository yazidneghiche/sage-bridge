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
set "ORDER_PIECE=BC159602"
set "ARTICLE_REF=10102-3"
set "ARTICLE_QTY=1"
set /p "ORDER_PIECE=Numero de commande Sage (exemple BC159602) : "
set /p "ARTICLE_REF=Reference article a ajouter : "
set /p "ARTICLE_QTY=Quantite a ajouter : "
if not defined ORDER_PIECE exit /b 1
if not defined ARTICLE_REF exit /b 1
if not defined ARTICLE_QTY exit /b 1
"%~dp0bin\AddProductToOrder.exe" "%~dp0config\config.json" "%ORDER_PIECE%" "%ARTICLE_REF%" "%ARTICLE_QTY%"
set "TEST_EXIT=%ERRORLEVEL%"
echo Code retour : %TEST_EXIT%
echo Chaque execution ajoute une ligne. Verifie le resultat avant de relancer.
pause
exit /b %TEST_EXIT%
