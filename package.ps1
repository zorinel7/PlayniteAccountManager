$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\PlayniteAccountManager'
$bin = Join-Path $project 'bin\Release\net462'
$manifest = Join-Path $project 'extension.yaml'
$out = Join-Path $root 'PlayniteAccountManager_0.10.0.pext'

if (!(Test-Path (Join-Path $bin 'PlayniteAccountManager.dll'))) { throw 'Brak PlayniteAccountManager.dll po kompilacji.' }
if (!(Test-Path $manifest)) { throw 'Brak extension.yaml.' }
if (Test-Path $out) { Remove-Item $out -Force }

$tmp = Join-Path $env:TEMP ('PlayniteAccountManager_0.10.0_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
Copy-Item (Join-Path $bin 'PlayniteAccountManager.dll') $tmp
Copy-Item $manifest $tmp
$icon = Join-Path $project 'icon.png'
if (!(Test-Path $icon)) { throw 'Brak icon.png.' }
Copy-Item $icon $tmp
$localization = Join-Path $project 'Localization'
if (Test-Path $localization) { Copy-Item $localization (Join-Path $tmp 'Localization') -Recurse }

# Windows PowerShell 5.1 Compress-Archive accepts only .zip, so pack to .zip and rename.
$zip = [IO.Path]::ChangeExtension($out, '.zip')
if (Test-Path $zip) { Remove-Item $zip -Force }
try {
  Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zip -CompressionLevel Optimal
  Move-Item $zip $out
}
finally {
  Remove-Item $tmp -Recurse -Force
}
Write-Host "Utworzono: $out"
