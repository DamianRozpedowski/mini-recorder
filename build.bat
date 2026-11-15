@echo off
rem Rebuilds MiniRecorder.exe with the C# compiler that ships with Windows (.NET Framework 4).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe. .NET Framework 4.x is required.
  pause
  exit /b 1
)
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:MiniRecorder.exe src\*.cs
if errorlevel 1 (
  echo Build failed.
) else (
  echo Built MiniRecorder.exe
)
pause
