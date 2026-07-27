param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$trayProject = Join-Path $repoRoot "src\SvrBridge.Tray\SvrBridge.Tray.csproj"
$diagnosticProject = Join-Path $repoRoot "src\SvrBridge\SvrBridge.csproj"
$output = Join-Path $repoRoot "artifacts\publish"
$diagnosticOutput = Join-Path $output "diagnostics"

dotnet publish $trayProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --source https://api.nuget.org/v3/index.json `
    --output $output `
    -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    throw "SVR Bridge tray publish failed with exit code $LASTEXITCODE."
}

dotnet publish $diagnosticProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --source https://api.nuget.org/v3/index.json `
    --output $diagnosticOutput `
    -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    throw "SVR Bridge diagnostic publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published SVR Bridge to $output"
Write-Host "Open SvrBridge.Tray.exe to finish setup with friendly action names."
Write-Host "Console diagnostics are available in $diagnosticOutput"
