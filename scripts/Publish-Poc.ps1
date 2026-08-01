param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$trayProject = Join-Path $repoRoot "src\SvrBridge.Tray\SvrBridge.Tray.csproj"
$diagnosticProject = Join-Path $repoRoot "src\SvrBridge\SvrBridge.csproj"
$output = Join-Path $repoRoot "artifacts\publish"
$diagnosticOutput = Join-Path $output "diagnostics"

# Deliberately not PublishSingleFile. This app was never a single-file app -
# it already requires app.vrmanifest, actions.json,
# bindings_vive_controller.json and SteamVR2Bot.png beside the exe to
# function. What PublishSingleFile actually does with UseWPF=true is leave
# WPF's native dependencies (PresentationNative_cor3.dll, wpfgfx_cor3.dll,
# D3DCompiler_47_cor3.dll, vcruntime140_cor3.dll) as loose files beside the
# exe rather than bundling them, which works only by accident on a machine
# that still has the rest of this same publish folder sitting next to a
# copied-out exe - and crashes with a bare DllNotFoundException for anyone
# who copies the exe out on its own, which a single file invites. Both
# publishes land their whole dependency set - managed and native - in the
# output folder together, and the folder is the deliverable; see the zip
# step below.
dotnet publish $trayProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --source https://api.nuget.org/v3/index.json `
    --output $output
if ($LASTEXITCODE -ne 0) {
    throw "SteamVR2Bot tray publish failed with exit code $LASTEXITCODE."
}

# The diagnostic console has no WPF and no native package dependency of its
# own today, so this fix is not strictly load-bearing for it - but
# consistency between the two publishes here is worth more than one fewer
# file, and it removes any need to re-check that fact every time this
# project's dependencies change.
dotnet publish $diagnosticProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --source https://api.nuget.org/v3/index.json `
    --output $diagnosticOutput
if ($LASTEXITCODE -ne 0) {
    throw "SteamVR2Bot diagnostic publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published SteamVR2Bot to $output"
Write-Host "Open SteamVR2Bot.exe to finish setup with friendly action names."
Write-Host "Console diagnostics are available in $diagnosticOutput"

# Named with the version so a tester can tell builds apart. Falls back to a
# timestamp if this checkout has no git history to describe (e.g. a source
# zip rather than a clone) - never fails the publish over it.
try {
    $version = (git -C $repoRoot describe --tags --always 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
        throw "git describe failed"
    }
    $version = $version.Trim()
} catch {
    $version = "build-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}

$zipPath = Join-Path $repoRoot "artifacts\SteamVR2Bot-$version-windows-x64.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# $output already contains the diagnostics subfolder, so this one archive is
# the whole deliverable - the folder is what a tester extracts and runs the
# exe from, not the exe on its own.
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zipPath
Write-Host "Zipped the publish folder to $zipPath"
Write-Host "Send testers the zip (or the folder directly) - extract it and run SteamVR2Bot.exe from inside it. The exe will not work moved out on its own."
