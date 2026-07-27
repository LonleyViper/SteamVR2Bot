param(
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot "artifacts\publish"
}

$PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
$manifest = Join-Path $PublishDirectory "app.vrmanifest"
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "app.vrmanifest was not found at $manifest. Run scripts\Publish-Poc.ps1 first."
}

$bridge = Join-Path $PublishDirectory "diagnosticsSteamVR2Bot.Diagnostics.exe"
if (-not (Test-Path -LiteralPath $bridge)) {
    throw "The SteamVR2Bot diagnostic tool was not found at $bridge. Run scripts\Publish-Poc.ps1 first."
}

# vrpathreg.exe registers driver paths, not application manifests. The bridge
# invokes IVRApplications.AddApplicationManifest with VRApplication_Utility.
$actions = Join-Path $PublishDirectory "actions.json"
& $bridge `
    --register-steamvr `
    --action-manifest $actions `
    --application-manifest $manifest
if ($LASTEXITCODE -ne 0) {
    throw "SteamVR application registration failed with exit code $LASTEXITCODE."
}

Write-Host "Registered SteamVR application manifest: $manifest"
Write-Host "Start SteamVR, open SteamVR2Bot.exe, then choose Set up SteamVR."
