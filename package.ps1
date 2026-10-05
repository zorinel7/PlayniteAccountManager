$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\PlayniteAccountManager'
$bin = Join-Path $project 'bin\Release\net462'
$manifest = Join-Path $project 'extension.yaml'
$out = Join-Path $root 'PlayniteAccountManager_0.9.54.pext'

if (!(Test-Path (Join-Path $bin 'PlayniteAccountManager.dll'))) { throw 'Brak PlayniteAccountManager.dll po kompilacji.' }
if (!(Test-Path $manifest)) { throw 'Brak extension.yaml.' }
if (Test-Path $out) { Remove-Item $out -Force }

$tmp = Join-Path $env:TEMP ('PlayniteAccountManager_0.9.54_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
Copy-Item (Join-Path $bin 'PlayniteAccountManager.dll') $tmp
Copy-Item $manifest $tmp
$localization = Join-Path $project 'Localization'
if (Test-Path $localization) { Copy-Item $localization (Join-Path $tmp 'Localization') -Recurse }

Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $out -CompressionLevel Optimal
Remove-Item $tmp -Recurse -Force
Write-Host "Utworzono: $out"
