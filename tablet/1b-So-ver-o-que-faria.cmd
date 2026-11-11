@echo off
rem BC Fichas - so mostra o que faria, sem mudar nada
rem Pede permissao de administrador e roda o script da pasta "scripts".
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\WindowsLeve.ps1" -Acao Leve -Simular
echo.
pause
