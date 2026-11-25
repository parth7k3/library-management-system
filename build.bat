@echo off
echo Compiling LibraryManagementSystem.exe ...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /r:System.dll /r:System.Core.dll /r:System.Data.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:"%~dp0LibraryManagementSystem.exe" "%~dp0src\LibraryApp.cs"
if %ERRORLEVEL% EQU 0 (
    echo Build succeeded: LibraryManagementSystem.exe
) else (
    echo Build failed!
)
