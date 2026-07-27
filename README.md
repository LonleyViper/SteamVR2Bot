# SVR Bridge

SVR Bridge runs a Streamer.bot action from a safe SteamVR controller shortcut.
It uses SteamVR actions directly—there is no keyboard emulation, browser overlay,
or OpenVR2Key layer.

The validated Vive shortcut is:

> Hold **Left Grip**, then press **Right Trigger**

The left grip is a safety button, so pulling the trigger by itself does nothing.

## Everyday setup

Build the ready-to-run folder:

```powershell
.\scripts\Publish-Poc.ps1
```

The published app includes its required .NET runtime, so no separate .NET
installation is needed for normal use.

Then open:

```text
artifacts\publish\SvrBridge.Tray.exe
```

In the SVR Bridge window:

1. Enter the Streamer.bot WebSocket address.
2. Enter the connection password only if Streamer.bot requires one.
3. Choose **Find actions**.
4. Pick the Streamer.bot action by its familiar name.
5. Choose **Test Streamer.bot** to confirm the connection and action.
6. Choose **Set up SteamVR** once.
7. Choose **Save and Start**.

Closing the window keeps SVR Bridge running in the Windows notification area.
Use its tray menu to open, start, stop, test, or exit the app.

## Streamer.bot address

When Streamer.bot is on the same PC, the usual address is:

```text
ws://127.0.0.1:8080/1
```

When it is on the streaming PC, use that PC's private-network address, for
example:

```text
ws://192.168.1.50:8080/1
```

Streamer.bot's WebSocket server must allow LAN connections, and its port must be
allowed through Windows Firewall on the private network.

SVR Bridge displays action names but stores the action's stable ID after it is
selected. Renaming an action in Streamer.bot therefore does not silently point
the shortcut at a different action.

## Password safety

The Streamer.bot password is saved in:

```text
%LOCALAPPDATA%\SVR Bridge\settings.json
```

Windows protects it for the current Windows user. It is not stored as readable
text and is never written to the activity log. On first launch, SVR Bridge can
import the old gitignored `appsettings.json` and immediately protect its
password.

## SteamVR binding

Choose **Set up SteamVR** in the app. The packaged Vive binding uses:

- **Safety button:** Left Grip
- **Action button:** Right Trigger

To change it, open SteamVR Controller Bindings and select **SVR Bridge**. Its
logical controls are:

- **Safety Button (hold)**
- **Action Button (press)**

The bridge has already passed 20/20 attempts in the SteamVR shell and 20/20
attempts in GERONIMO with the dashboard closed. See
`LIVE_TEST_RESULTS.md` for the evidence.

## Status messages

- **Stopped** — the controller shortcut is not running.
- **Starting…** — SVR Bridge is connecting to SteamVR.
- **Ready for your shortcut** — controller input is active.
- **Running your action…** — the shortcut was detected.
- **Needs attention** — the status detail explains what to check.

Recent activity shows input and delivery events without exposing credentials.

## Diagnostics

The console diagnostic remains available at:

```text
artifacts\publish\diagnostics\SvrBridge.exe
```

Useful checks:

```powershell
.\artifacts\publish\diagnostics\SvrBridge.exe --self-test
.\artifacts\publish\diagnostics\SvrBridge.exe --simulate
.\scripts\Register-SteamVrApp.ps1
```

The normal daily-use path is the tray app; the console is retained for
troubleshooting and regression testing.
