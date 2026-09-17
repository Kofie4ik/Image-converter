@echo off
rem Builds the app with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Nothing to install. Output: bin\ImageConverter.exe
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
    echo csc.exe from .NET Framework 4.x was not found
    exit /b 1
)

if not exist bin mkdir bin

"%FW%\csc.exe" /nologo /target:winexe /codepage:65001 /optimize+ ^
    /out:bin\ImageConverter.exe /win32icon:src\ImageConverter.ico ^
    /lib:"%FW%\WPF" ^
    /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:PresentationCore.dll ^
    /r:WindowsBase.dll /r:System.Xaml.dll /r:System.Core.dll ^
    src\ImageConverter.cs src\ImageConverter.Ui.cs

if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)
echo Done: bin\ImageConverter.exe
