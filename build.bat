@echo off
setlocal
cd /d "%~dp0"

echo Compilando GameSirBatteryHub.exe (Portatil / Single-File)...
set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

%CSC% /nologo /target:winexe /out:"GameSirBatteryHub.exe" ^
  /win32icon:"public\img\app.ico" ^
  /resource:"public\index.html",index.html ^
  /resource:"public\img\p1.png",p1.png ^
  /resource:"public\img\p2.png",p2.png ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "src\GameSirMonitor.cs"

if %ERRORLEVEL% EQU 0 (
    echo [OK] GameSirBatteryHub.exe compilado com sucesso!
) else (
    echo [ERRO] Falha na compilacao.
)
