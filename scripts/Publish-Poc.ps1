param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SvrBridge\SvrBridge.csproj"
$output = Join-Path $repoRoot "artifacts\publish"

dotnet publish $project --configuration $Configuration --output $output --no-self-contained
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published SVR Bridge POC to $output"
Write-Host "Copy appsettings.example.json to appsettings.json and edit the Streamer.bot settings."

