@echo off
cd /d C:\wamp32\www\ton-projet

php bin\console sage:export:accounts --limit=1
if errorlevel 1 exit /b 1

php bin\console app:sync:sage-accounts
if errorlevel 1 exit /b 1

php bin\console sage:export:orders
if errorlevel 1 exit /b 1

php bin\console app:sync:sage-orders
if errorlevel 1 exit /b 1

exit /b 0