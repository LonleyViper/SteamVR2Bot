# SVR Bridge

SVR Bridge runs Streamer.bot actions from safe SteamVR controller shortcuts.
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
3. On **Connection & setup**, choose **Refresh Streamer.bot actions**.
4. On **Shortcuts**, choose **Add shortcut**.
5. Give it a clear name, choose its Streamer.bot action, then choose
   **Record controller inputs**.
6. In VR, release the buttons, hold the safety input, then press the action
   input.
7. Save the shortcut and use **Test action**.

The Shortcuts page lists every gesture and the action it runs. Select a row to
edit, test, enable or disable, or remove it. Existing single-action settings
are migrated automatically as the first shortcut. Every change saves
automatically and is applied to the running bridge.

SVR Bridge registers itself with SteamVR and starts automatically whenever the
app is open. There is no separate Save or Start step.

Closing the window keeps SVR Bridge running in the Windows notification area.
Use its tray menu to open, start, stop, test, or exit the app.

## In-VR setup

While SVR Bridge is open, its tab is always available in the SteamVR dashboard.
Choose the tab in SteamVR, or use **Open SteamVR dashboard** on the Connection &
setup page. It shows the currently saved shortcuts and what each one runs.

To create one without leaving VR:

1. Select **Record a new shortcut**.
2. Choose the Streamer.bot action by its familiar name. Use **Next** to page
   through a long action list.
3. Release all controller buttons.
4. Hold the safety input, then press the action input.

The shortcut is saved immediately, appears in the desktop list, and activates
automatically. Streamer.bot actions are loaded automatically at launch; the
desktop refresh button remains available if actions change later.

## Controller inputs

The recorder reads the connected controller directly and stores friendly
physical input names, such as **Left Grip** and **Right Trigger**. This enables
several different gestures at the same time.

The recorder is validated first for Vive controllers. Controller button layouts
vary by family. For Index, Touch, WMR, Cosmos, or another controller, verify the
recorded names and run the live test matrix before treating it as a packaged
default.

The original SteamVR logical-input route remains available as a compatibility
fallback:

- **Safety Button** — held to prevent an accidental command.
- **Action Button** — pressed to run the selected Streamer.bot action.

Choose **SteamVR input bindings** to open the official binding page directly.
SteamVR keeps a separate binding for each controller family, so changing an
Index binding does not overwrite a Vive or Touch binding.

The packaged Vive preset—Left Grip plus Right Trigger—is live-validated. Other
controller families are detected and can be configured through SteamVR, but no
untested default preset is labelled as validated.

Each shortcut's gesture behavior supports:

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

SteamVR setup is registered automatically when the app opens. Use **Repair
SteamVR setup** only if the dashboard entry or controller binding was removed.
The packaged Vive binding uses:

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

- SteamVR input runs in a small disposable worker. If SteamVR shuts that worker
  down during a restart, the tray app remains open and creates a new worker
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
