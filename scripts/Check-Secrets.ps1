$ErrorActionPreference = 'Stop'
$patterns = @(
  '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
  'ghp_[A-Za-z0-9]{20,}',
  'github_pat_[A-Za-z0-9_]{20,}',
  'AKIA[0-9A-Z]{16}',
  'sk-[A-Za-z0-9]{20,}',
  '(?i)password\s*[:=]\s*"[^"]{6,}"',
  '(?i)api[_-]?key\s*[:=]\s*"[^"]{10,}"'
)
$files = Get-ChildItem -Recurse -File | Where-Object {
  $_.FullName -notmatch '\\.git\\' -and
  $_.FullName -notmatch '\\obj\\' -and
  $_.FullName -notmatch '\\bin\\' -and
  $_.Name -notmatch '^credentials\.dat$' -and
  $_.Extension -in @('.cs','.csproj','.ps1','.bat','.cmd','.md','.txt','.yaml','.yml','.json','.xaml','.sln')
}
$found = @()
foreach ($file in $files) {
  $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
  if ($null -eq $content) { continue }
  foreach ($pattern in $patterns) {
    if ($content -match $pattern) {
      $found += [PSCustomObject]@{ File = $file.FullName; Pattern = $pattern }
    }
  }
}
if ($found.Count -gt 0) {
  $found | Format-Table -AutoSize | Out-String | Write-Error
  throw 'Potential secret detected. Review the repository before committing.'
}
Write-Host 'Secret scan passed.'
