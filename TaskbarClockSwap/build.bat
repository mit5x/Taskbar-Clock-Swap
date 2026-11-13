@echo off
rem Builds TaskbarClockSwap.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. Install .NET Framework 4.8.
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32manifest:app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:TaskbarClockSwap.exe TaskbarClockSwap.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)
echo.
echo OK: TaskbarClockSwap.exe
pause
