# SVR Bridge proof of concept

This proof tests whether a dedicated SteamVR overlay application can receive two
bindable controller actions while another VR application is active, detect a
controller chord, and execute a Streamer.bot action over WebSocket.

It does not emit keyboard input and it has no browser or rendered VR overlay.

## What is included

- SteamVR action-manifest input using Valve's `IVRInput` API
- Two logical, user-bindable controller buttons
- Modifier and simultaneous chord modes
- Streamer.bot `DoAction` requests over WebSocket
- Optional Streamer.bot password authentication
- One reconnect-and-retry attempt
- Raw input-change logging
- Simulation and self-test modes

## Build

The project currently targets .NET 10 because that is the SDK installed in this
workspace. On the VR PC, install the .NET 10 runtime or publish it there with an
appropriate self-contained runtime.

```powershell
.\scripts\Publish-Poc.ps1
```

The output is written to `artifacts\publish`.

## Configure Streamer.bot

On the streaming PC:

1. Create an action named `SVR POC Test`. Give it a visible test effect such as
   writing a log line or toggling a harmless OBS source.
2. Enable Streamer.bot's WebSocket server.
3. Allow LAN connections and note the port.
4. If using a password, put the same password in the bridge configuration.
5. Allow the port through Windows Firewall only on the private network.

On the VR gaming PC:

1. Copy `appsettings.example.json` to `appsettings.json` in the published folder.
2. Set `webSocketUrl` to the streaming PC, for example
   `ws://192.168.1.50:8080/`.
3. Set `actionName` or the action GUID.
4. Change `dryRun` to `false`.

Before involving SteamVR, validate the network and Streamer.bot:

```powershell
.\artifacts\publish\SvrBridge.exe --simulate
```

Press `M` to hold the simulated modifier, then `T` to trigger the chord.

## Register and bind in SteamVR

Run this on the VR gaming PC after publishing:

```powershell
.\scripts\Register-SteamVrApp.ps1
```

The script uses SteamVR's `IVRApplications.AddApplicationManifest` API. It
does not use `vrpathreg.exe`, which registers SteamVR driver paths rather than
application manifests.

Then:

1. Start SteamVR.
2. Run `artifacts\publish\SvrBridge.exe`.
3. On Vive controllers, use the packaged default: hold the **left grip** and
   press the **right index trigger**.
4. To customize it, open SteamVR controller bindings, select
   **SVR Bridge POC**, and remap **Chord Button One / Modifier** and
   **Chord Button Two / Trigger**.

The console should show:

```text
INPUT one=DOWN two=up
INPUT one=DOWN two=DOWN
CHORD detected.
Streamer.bot acknowledged 'SVR POC Test'.
```

## Decisive coexistence test

After the chord works in SteamVR Home:

1. Leave `SvrBridge.exe` running.
2. Launch a normal VR game.
3. Repeat the same chord without opening the SteamVR dashboard.
4. Confirm both the raw input transitions and the Streamer.bot acknowledgement.
5. Repeat at least 20 times and note any missed or duplicate detections.

Success means the raw SteamVR input reaches the overlay application reliably
while the scene application is active. Failure before `CHORD detected` is a
SteamVR input/coexistence issue. Failure after it is a network or Streamer.bot
issue, which keeps the experiment easy to diagnose.

## Useful commands

```powershell
dotnet run --project .\src\SvrBridge -- --self-test
dotnet run --project .\src\SvrBridge -- --simulate
dotnet run --project .\src\SvrBridge
```

Press `Ctrl+C` to stop the real SteamVR input loop.
