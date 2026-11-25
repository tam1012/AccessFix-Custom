@echo off
setlocal
chcp 65001 >nul
title Bien dich AccessFix.exe

cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo [X] Khong tim thay trinh bien dich .NET Framework csc.exe tren may nay.
    pause
    exit /b 1
)

echo [~] Dang bien dich AccessFix.exe bang trinh bien dich co san cua Windows...
"%CSC%" /nologo /target:winexe /optimize+ /utf8output ^
    /out:"%~dp0AccessFix.exe" ^
    /win32icon:"%~dp0src\app.ico" ^
    /win32manifest:"%~dp0src\app.manifest" ^
    /r:System.dll ^
    /r:System.Core.dll ^
    /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll ^
    /r:System.Web.Extensions.dll ^
    /r:System.Net.Http.dll ^
    "%~dp0src\AssemblyInfo.cs" ^
    "%~dp0src\AppConfig.cs" ^
    "%~dp0src\SplitProxyEngine.cs" ^
    "%~dp0src\SystemProxyManager.cs" ^
    "%~dp0src\TrayIconHelper.cs" ^
    "%~dp0src\MainForm.cs" ^
    "%~dp0src\Program.cs"

if %errorlevel% neq 0 (
    echo [X] Bien dich that bai!
    pause
    exit /b 1
)

echo [OK] Da tao thanh cong: %~dp0AccessFix.exe
if "%~1"=="" pause
