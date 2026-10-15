@echo off

echo ===============================
echo   Compilation BridgeWorkerAccount
echo ===============================

C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe ^
  /platform:x86 ^
  /target:exe ^
  /out:C:\wamp32\www\sage-bridge\bin\BridgeWorkerAccount.exe ^
  C:\wamp32\www\sage-bridge\src\BridgeWorkerAccount.cs ^
  /reference:System.Web.Extensions.dll

echo.
echo ===== Compilation terminee =====
pause
