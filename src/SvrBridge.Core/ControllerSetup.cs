namespace SvrBridge.Core;

public enum BridgeLogLevel
{
    Info,
    Warning,
    Error,

    // Appended rather than ordered by severity so that Info stays the zero
    // value: anything that forgets to say what level it is should read as an
    // ordinary activity line, not as diagnostics.
    Debug
}

/// <param name="ContainsUserContent">
/// Set when the message quotes something a viewer wrote, rather than describing
/// what this app did. Everything else in this app is its own telemetry and is
/// safe to keep on disk for support; viewer chat is other people's words, and
/// retaining it for 14 days is a category of data collection this app has never
/// made and should not start making silently. The structured log refuses these
/// entries, so the flag is the retention decision rather than a hint about one.
/// </param>
public sealed record BridgeActivity(
    string EventName,
    string Message,
    BridgeLogLevel Level = BridgeLogLevel.Info,
    bool ContainsUserContent = false);

public enum BindingAvailability
{
    Unknown,
    Ready,
    NeedsSetup
}

public sealed record ControllerDevice(
    string ControllerType,
    string FriendlyName,
    string Hand,
    string Model);

public sealed record ActionBinding(
    string LogicalInput,
    string DevicePath,
    string InputPath,
    string Mode,
    string Slot);

public sealed record ControllerSetup(
    IReadOnlyList<ControllerDevice> Controllers,
    ActionBinding? SafetyInput,
    ActionBinding? ActionInput,
    BindingAvailability Availability,
    string FriendlySummary,
    bool UsesValidatedVivePreset)
{
    public static ControllerSetup Unknown { get; } = new(
        [],
        null,
        null,
        BindingAvailability.Unknown,
        "Start SteamVR and turn on both controllers to inspect their bindings.",
        false);
}
