@echo off
setlocal
cd /d "%~dp0"
echo ==================================================
echo   Playnite Account Manager - BUILD 0.9.45
echo ==================================================
set "PROJECT=%~dp0src\PlayniteAccountManager\PlayniteAccountManager.csproj"
set "HELPER_PROJECT=%~dp0src\PlayniteAccountManager.EAHelper\PlayniteAccountManager.EAHelper.csproj"
if not exist "%PROJECT%" (
  echo [BLAD] Nie znaleziono: %PROJECT%
  pause
  exit /b 1
)
if not exist "%HELPER_PROJECT%" (
  echo [BLAD] Nie znaleziono: %HELPER_PROJECT%
  pause
  exit /b 1
)
set "MSBUILD="
if exist "%ProgramFiles%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" (
  echo [BLAD] Nie znaleziono MSBuild.
  pause
  exit /b 1
)
echo.
echo [1/4] Clean plugin
echo.
"%MSBUILD%" "%PROJECT%" /t:Clean /p:Configuration=Release
if errorlevel 1 goto :fail
echo.
echo [2/4] Restore + Build plugin
echo.
"%MSBUILD%" "%PROJECT%" /restore /t:Build /p:Configuration=Release
if errorlevel 1 goto :fail
echo.
echo [3/4] Build EA helper
echo.
"%MSBUILD%" "%HELPER_PROJECT%" /restore /t:Build /p:Configuration=Release
if errorlevel 1 goto :fail
echo.
echo [4/4] Package + install
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0package.ps1"
if errorlevel 1 goto :fail
set "BIN=%~dp0src\PlayniteAccountManager\bin\Release\net462"
set "HELPER_BIN=%~dp0src\PlayniteAccountManager.EAHelper\bin\Release\net462"
set "EXT=%APPDATA%\Playnite\Extensions\PlayniteAccountManager"
if exist "%EXT%" rmdir /s /q "%EXT%"
mkdir "%EXT%"
copy /y "%BIN%\PlayniteAccountManager.dll" "%EXT%\PlayniteAccountManager.dll" >nul
copy /y "%HELPER_BIN%\PlayniteAccountManager.EAHelper.exe" "%EXT%\PlayniteAccountManager.EAHelper.exe" >nul
copy /y "%~dp0src\PlayniteAccountManager\extension.yaml" "%EXT%\extension.yaml" >nul
echo.
echo ==================================================
echo   BUILD 0.9.45 ZAKONCZONY
echo ==================================================
echo   Zainstalowano do:
echo   %EXT%
echo.
pause
exit /b 0
:fail
echo.
echo ==================================================
echo   KOMPILACJA NIEUDANA
echo ==================================================
pause
exit /b 1
