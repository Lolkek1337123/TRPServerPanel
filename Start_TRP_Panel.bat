@echo off
:: TRP SERVER PANEL - PORTABLE LAUNCHER
:: Указываем путь к нашему локальному .NET
set DOTNET_ROOT=C:\Users\SteveMarkins\.dotnet
set PATH=%DOTNET_ROOT%;%PATH%

:: Запускаем приложение
echo [TRP] Starting TRP Server Panel with Local .NET 10.0.5...
start "" "c:\AI_Antigravity\apps\TRPServerPanel\bin\Debug\net10.0-windows\TRPServerPanel.exe"
