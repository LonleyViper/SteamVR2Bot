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

## Controller inputs

SVR Bridge now detects the active controller family and reads the current
SteamVR binding for its two logical inputs:

- **Safety Button** — held to prevent an accidental command.
- **Action Button** — pressed to run the selected Streamer.bot action.

Choose **Change SteamVR inputs…** to open the official binding page directly.
SteamVR keeps a separate binding for each controller family, so changing an
Index binding does not overwrite a Vive or Touch binding.

The packaged Vive preset—Left Grip plus Right Trigger—is live-validated. Other
controller families are detected and can be configured through SteamVR, but no
untested default preset is labelled as validated.

The **Gesture behavior** setting supports:

- Hold the Safety Button, then press the Action Button.
- Press both chosen inputs together within 300 ms.

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

The bridge has passed 20/20 attempts in the SteamVR shell through both the
diagnostic and tray hosts. With GERONIMO active and the dashboard closed, it
passed 20/20 through the diagnostic host and 21/21 through the tray host. See
`LIVE_TEST_RESULTS.md` for the evidence.

## Status messages

- **Stopped** — the controller shortcut is not running.
- **Starting…** — SVR Bridge is connecting to SteamVR.
- **Ready for your shortcut** — controller input is active.
- **Running your action…** — the shortcut was detected.
- **Needs attention** — the status detail explains what to check.

Recent activity shows input and delivery events without exposing credentials.

The same events are written as structured JSON lines under:

```text
%LOCALAPPDATA%\SVR Bridge\Logs
```

Use **Open logs** in the app or tray menu. Logs are kept for 14 days and never
contain the Streamer.bot password.

## Restart and network recovery

- If SteamVR is unavailable or restarts, the tray app remains open and retries
  after 1, 2, 5, 10, then 30 seconds.
- If Streamer.bot is unavailable before delivery, the bridge makes three
  bounded connection attempts. The next controller shortcut tries again.
- If the connection is lost after delivery begins, the action is not blindly
  resent. The status says that confirmation was lost; avoiding a possible
  duplicate takes priority.

## Diagnostics

The console diagnostic remains available at:

```text
artifacts\publish\diagnostics\SvrBridge.exe
```

Useful checks:

```powershell
.\artifacts\publish\diagnostics\SvrBridge.exe --self-test
.\artifacts\publish\diagnostics\SvrBridge.exe --simulate
.\artifacts\publish\diagnostics\SvrBridge.exe --inspect-bindings
.\artifacts\publish\diagnostics\SvrBridge.exe --open-bindings
.\scripts\Register-SteamVrApp.ps1
```

The normal daily-use path is the tray app; the console is retained for
troubleshooting and regression testing.
