@echo off
rem BC Fichas - deixar o Windows leve
rem Pede permissao de administrador e roda o script da pasta "scripts".
if not exist "%~dp0scripts\WindowsLeve.ps1" (
  echo Nao achei a pasta scripts ao lado deste arquivo.
  echo Tire tudo do ZIP antes: botao direito no ZIP, Extrair tudo. Depois abra o arquivo da pasta extraida.
  pause
  exit /b 1
)
fltmc >nul 2>&1
if not errorlevel 1 goto admin
if "%~1"=="/elevado" (
  echo Este arquivo precisa rodar como administrador, e o Windows nao deixou.
  echo Entre no Windows com um usuario administrador e tente de novo.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Start-Process -FilePath '%~f0' -ArgumentList '/elevado' -Verb RunAs -ErrorAction Stop } catch { exit 1 }"
if errorlevel 1 (
  echo Para continuar, toque em Sim na pergunta do Windows.
  pause
)
exit /b
:admin
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\WindowsLeve.ps1" -Acao Leve
echo.
pause
