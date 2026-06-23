@echo off
REM 用 Windows 內建的 .NET Framework 編譯器編譯，不需安裝任何 SDK。
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:AfkKeeper.exe ^
  /win32manifest:app.manifest ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  AfkKeeper.cs

if %ERRORLEVEL%==0 (
  echo.
  echo [OK] 編譯完成：AfkKeeper.exe
) else (
  echo.
  echo [FAIL] 編譯失敗，錯誤碼 %ERRORLEVEL%
)
endlocal
