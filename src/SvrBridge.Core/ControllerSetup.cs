namespace SvrBridge.Core;

public enum BridgeLogLevel
{
    Info,
    Warning,
    Error
}

public sealed record BridgeActivity(
    string EventName,
    string Message,
    BridgeLogLevel Level = BridgeLogLevel.Info);

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
