$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\PlayniteAccountManager\PlayniteAccountManager.csproj'

$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
}

if (-not $msbuild) {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )
    $msbuild = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $msbuild) { throw 'Nie znaleziono MSBuild. Zainstaluj Visual Studio z workload .NET desktop development.' }

Write-Host "MSBuild: $msbuild"
& $msbuild $project /restore /t:Build /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw "Budowanie PlayniteAccountManager.dll nie powiodło się." }

$helperProject = Join-Path $root 'src\PlayniteAccountManager.EAHelper\PlayniteAccountManager.EAHelper.csproj'

& $msbuild $helperProject /restore /t:Build /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw "EA helper build failed." }

Write-Host 'Build completed: plugin + EA helper'
