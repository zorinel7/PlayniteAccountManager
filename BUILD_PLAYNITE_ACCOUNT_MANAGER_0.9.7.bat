@echo off
setlocal
cd /d "%~dp0"
echo ==================================================
echo   Playnite Account Manager - BUILD 0.9.49
echo ==================================================
set "PROJECT=%~dp0src\PlayniteAccountManager\PlayniteAccountManager.csproj"
if not exist "%PROJECT%" (
  echo [BLAD] Nie znaleziono: %PROJECT%
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
echo [1/3] Clean plugin
echo.
"%MSBUILD%" "%PROJECT%" /t:Clean /p:Configuration=Release
if errorlevel 1 goto :fail
echo.
echo [2/3] Restore + Build plugin
echo.
"%MSBUILD%" "%PROJECT%" /restore /t:Build /p:Configuration=Release
if errorlevel 1 goto :fail
echo.
echo [3/3] Package + install
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0package.ps1"
if errorlevel 1 goto :fail
set "BIN=%~dp0src\PlayniteAccountManager\bin\Release\net462"
set "EXT=%APPDATA%\Playnite\Extensions\PlayniteAccountManager"
if exist "%EXT%" rmdir /s /q "%EXT%"
mkdir "%EXT%"
copy /y "%BIN%\PlayniteAccountManager.dll" "%EXT%\PlayniteAccountManager.dll" >nul
copy /y "%~dp0src\PlayniteAccountManager\extension.yaml" "%EXT%\extension.yaml" >nul
echo.
echo ==================================================
echo   BUILD 0.9.49 ZAKONCZONY
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
