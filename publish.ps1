<#
.SYNOPSIS
  Build ClaudeHook and deploy it to release\, stamped with the commit it was built from.
.DESCRIPTION
  release\ is gitignored build output, so without a stamp there is no way to tell which
  source produced the running hook. The build records the short sha, plus `.dirty` when
  the working tree didn't match that commit, and logs it on every invocation.

  The previous binaries are kept in release\backup-prev\ so a rollback is a copy.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1
#>
$ErrorActionPreference = 'Stop'

$csproj  = Join-Path $PSScriptRoot 'src\ClaudeHook.csproj'
$release = Join-Path $PSScriptRoot 'release'
$backup  = Join-Path $release 'backup-prev'
$stage   = Join-Path ([System.IO.Path]::GetTempPath()) "claudehook-publish-$PID"

$sha = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or -not $sha) { $sha = 'nogit' }
$dirty = if (& git -C $PSScriptRoot status --porcelain 2>$null) { '.dirty' } else { '' }
$stamp = "$sha$dirty"
if ($dirty) { Write-Warning "working tree is dirty - the deployed hook matches no commit ($stamp)" }

& dotnet publish $csproj -c Release -o $stage --nologo -p:InformationalVersion=$stamp
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

$files = 'ClaudeHook.exe','ClaudeHook.dll','ClaudeHook.pdb','ClaudeHook.deps.json','ClaudeHook.runtimeconfig.json'

if (-not (Test-Path $backup)) { New-Item -ItemType Directory -Path $backup | Out-Null }
foreach ($f in $files) {
    $prev = Join-Path $release $f
    if (Test-Path $prev) { Copy-Item $prev (Join-Path $backup $f) -Force }
}

# ClaudeHook.exe is spawned per hook and exits in ~150ms, so a copy can collide with a
# live invocation. Retry rather than leave the deploy half-applied.
foreach ($f in $files) {
    $src = Join-Path $stage $f
    if (-not (Test-Path $src)) { Write-Warning "missing in build output: $f"; continue }
    $ok = $false
    for ($i = 1; $i -le 12; $i++) {
        try { Copy-Item $src (Join-Path $release $f) -Force; $ok = $true; break }
        catch { Start-Sleep -Milliseconds 400 }
    }
    if (-not $ok) { throw "could not replace $f after 12 attempts (file locked)" }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Deployed ClaudeHook $stamp to $release" -ForegroundColor Green
