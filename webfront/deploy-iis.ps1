<#
  Build the SPA and publish it to the OcrFront IIS site.

  Run from anywhere:   powershell -ExecutionPolicy Bypass -File .\deploy-iis.ps1
  Custom target:       .\deploy-iis.ps1 -Target "C:\WEB\OcrFront"

  WARNING: robocopy /MIR mirrors the folder — anything sitting in the target that is
  not produced by the build is deleted. Do not put hand-edited files there; put them
  in webfront\public\ so every build carries them along.
#>
param(
  [string]$Target = 'C:\WEB\OcrFront'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

Write-Host "==> Building (tsc -b && vite build)..." -ForegroundColor Cyan
Push-Location $root
try {
  & npm run build
  if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)" }
} finally {
  Pop-Location
}

if (-not (Test-Path (Join-Path $dist 'index.html'))) {
  throw "dist\index.html not found — build produced nothing."
}
if (-not (Test-Path (Join-Path $dist 'web.config'))) {
  Write-Warning "dist\web.config missing — is public\web.config still in place?"
}

if (-not (Test-Path $Target)) {
  Write-Host "==> Creating $Target" -ForegroundColor Cyan
  New-Item -ItemType Directory -Path $Target -Force | Out-Null
}

Write-Host "==> Mirroring dist -> $Target" -ForegroundColor Cyan
& robocopy $dist $Target /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
# robocopy exit codes 0-7 are success (8+ means copy errors)
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

Write-Host "==> Done. Deployed to $Target" -ForegroundColor Green
