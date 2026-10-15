@echo off

cd C:\wamp32\www\api

php bin\console app:migration:accounts
php bin\console app:migration:products
