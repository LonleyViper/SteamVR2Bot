[CmdletBinding()]
param(
    [ValidateSet('general', 'input', 'dashboard', 'chat', 'notifications', 'settings', 'packaging')]
    [string]$Area = 'general'
)

$projectRoot = Split-Path -Parent $PSScriptRoot

$areaFiles = @{
    general = @('README.md', 'src/SvrBridge.Core/BridgeEngine.cs', 'src/SvrBridge.Core/StreamerBotClient.cs', 'src/SvrBridge.Tray/OpenVrWorker.cs', 'src/SvrBridge.Tray/TrayApplicationContext.cs', 'src/SvrBridge/SelfTests.cs', 'src/SvrBridge.Tray/TraySelfTests.cs', 'src/SvrBridge.Core/SvrBridge.Core.csproj', 'src/SvrBridge.Tray/SvrBridge.Tray.csproj', 'src/SvrBridge/SvrBridge.csproj')
    input = @('src/SvrBridge.Core/OpenVrInput.cs', 'src/SvrBridge.Core/ChordDetector.cs', 'src/SvrBridge.Core/BridgeEngine.cs', 'src/SvrBridge.Tray/OpenVrWorker.cs', 'GESTURE_TRIGGERS.md', 'FOCUSED_INPUT_PROBE.md')
    dashboard = @('src/SvrBridge.Tray/VrDashboardController.cs', 'src/SvrBridge.Tray/VrDashboardRenderer.cs', 'src/SvrBridge.Tray/VrDashboardLayout.cs', 'src/SvrBridge.Tray/VrActionBrowser.cs', 'HANDOFF.md')
    chat = @('src/SvrBridge.Tray/ChatOverlay.cs', 'src/SvrBridge.Tray/ChatOverlayInput.cs', 'src/SvrBridge.Core/ChatRingBuffer.cs', 'src/SvrBridge.Tray/WpfChatRenderer.cs', 'CHAT_AND_NOTIFICATIONS_PLAN.md', 'HANDOFF.md')
    notifications = @('src/SvrBridge.Tray/NotificationOverlay.cs', 'src/SvrBridge.Tray/NotificationEventPicker.cs', 'src/SvrBridge.Tray/WpfNotificationRenderer.cs', 'src/SvrBridge.Core/StreamerBotEventCatalog.cs', 'PHASE7_HANDOFF.md')
    settings = @('src/SvrBridge.Tray/UserSettings.cs', 'src/SvrBridge.Core/AppConfig.cs', 'src/SvrBridge.Core/VrSettingsSnapshot.cs', 'src/SvrBridge.Core/SurfaceOverrideState.cs', 'NEXT_PHASE_PLAN.md')
    packaging = @('src/SvrBridge.Core/SteamVrApplications.cs', 'src/SvrBridge.Core/SidecarAssets.cs', 'scripts/Register-SteamVrApp.ps1', 'scripts/Publish-Poc.ps1', 'README.md')
}

Write-Output '# SteamVR2Bot agent context manifest'
Write-Output "Area: $Area"
Write-Output "Root: $projectRoot"
Write-Output ''
Write-Output 'Read first:'
Write-Output '  AGENTS.md'
Write-Output '  docs/current-state.md'
Write-Output '  docs/project-map.md'
Write-Output '  docs/live-tests/index.md'
Write-Output ''
Write-Output "Task area ($Area):"
$areaFiles[$Area] | ForEach-Object { Write-Output "  $_" }
Write-Output ''
Write-Output 'Do not load by default:'
Write-Output '  LIVE_TEST_RESULTS.md, artifacts/, bin/, obj/, VR UI Screenshots/'
Write-Output '  Root-level phase prompts and historical handoffs'
