@echo off
setlocal
cd /d "%~dp0"
set "COMPILER=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%COMPILER%" (
    echo Compilateur introuvable.
    pause
    exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%COMPILER%" /nologo /platform:x86 /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll /out:"%~dp0bin\AddProductToOrder.exe" "%~dp0AddProductToOrder.cs"
if errorlevel 1 (
    echo Echec de compilation.
    pause
    exit /b 1
)
echo Compilation terminee : bin\AddProductToOrder.exe
pause
