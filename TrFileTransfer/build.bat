@echo off
cd /d "%~dp0"
set FRAMEWORK=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FRAMEWORK%\csc.exe

if not exist "%CSC%" (
    set FRAMEWORK=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319
    set CSC=%FRAMEWORK%\csc.exe
)
if not exist "%CSC%" (
    echo Error: C# compiler not found. Install .NET Framework 4.x SDK.
    exit /b 1
)

echo Building TrFileTransfer...
echo.

"%CSC%" ^
    /nologo ^
    /target:winexe ^
    /out:TrFileTransfer.exe ^
    /reference:"%FRAMEWORK%\System.dll" ^
    /reference:"%FRAMEWORK%\System.Core.dll" ^
    /reference:"%FRAMEWORK%\System.Windows.Forms.dll" ^
    /reference:"%FRAMEWORK%\System.Drawing.dll" ^
    /optimize+ ^
    Shared.cs ^
    L10N.cs ^
    UdpProtocol.cs ^
    TransferServer.cs ^
    TransferUdpServer.cs ^
    TransferClient.cs ^
    TransferUdpClient.cs ^
    MainForm.cs ^
    Program.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================
    echo   Build successful: TrFileTransfer.exe
    echo ========================================
) else (
    echo.
    echo Build FAILED.
    exit /b %ERRORLEVEL%
)
