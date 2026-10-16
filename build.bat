@echo off
cd /d "%~dp0"
rem Builds KinetikFocus.exe with the .NET Framework compiler that ships with Windows. No SDK needed.
rem "build.bat package" also makes KinetikFocus.zip (the exe, README and licence) for a release.
if not exist icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 || exit /b 1
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /utf8output /codepage:65001 /platform:x64 /target:winexe /out:KinetikFocus.new.exe /win32icon:icon.ico /resource:icon.ico,icon.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  KinetikFocus.cs || exit /b 1
rem Built under a new name first, so this works while the app is running. The exe it replaces goes to ..\Old Versions
rem (when that folder is there), stamped with the date and time, to go back to if a build misbehaves.
if exist KinetikFocus.exe if exist "..\Old Versions\" (
  for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd-HHmmss"') do move /y KinetikFocus.exe "..\Old Versions\KinetikFocus-%%t.exe" >nul
)
if exist KinetikFocus.exe move /y KinetikFocus.exe KinetikFocus.old.exe >nul
move /y KinetikFocus.new.exe KinetikFocus.exe >nul || exit /b 1
if exist KinetikFocus.old.exe del KinetikFocus.old.exe 2>nul
echo Built KinetikFocus.exe
if /i not "%~1"=="package" exit /b 0
if exist dist rmdir /s /q dist
mkdir dist\KinetikFocus
copy /y KinetikFocus.exe dist\KinetikFocus\ >nul
copy /y README.md dist\KinetikFocus\ >nul
copy /y LICENSE dist\KinetikFocus\ >nul
if exist KinetikFocus.zip del KinetikFocus.zip
powershell -NoProfile -Command "Compress-Archive -Path dist\KinetikFocus\* -DestinationPath KinetikFocus.zip"
rmdir /s /q dist
echo Made KinetikFocus.zip
