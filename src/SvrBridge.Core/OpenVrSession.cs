namespace SvrBridge.Core;

public interface IOpenVrSession : IDisposable
{
    InputSnapshot Poll();

    ControllerSetup GetControllerSetup();

    void OpenBindingUi();

    void ShowDashboard(string imagePath) =>
        throw new NotSupportedException("This SteamVR session cannot show a dashboard.");

    void ShowDashboard(
        string imagePath,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate = true) =>
        ShowDashboard(imagePath);

    IReadOnlyList<ShortcutConfig> DrainCreatedShortcuts() => [];

    IReadOnlyList<string> DrainDeletedShortcutIds() => [];

    /// <summary>
    /// True once SteamVR has asked the app to quit, as opposed to the session
    /// dropping for a reason worth reconnecting after.
    /// </summary>
    bool IsQuitRequested() => false;
}

/// <summary>
/// SteamVR asked the app to close. Distinct from a dropped session so the
/// engine retries the latter and exits for this one.
/// </summary>
public sealed class SteamVrShutdownException()
    : Exception("SteamVR is shutting down.");

public interface IOpenVrSessionFactory
{
    Task<IOpenVrSession> ConnectAsync(
        AppConfig config,
        string actionManifest,
        Action<string> log,
        CancellationToken cancellationToken);
}

public sealed class InProcessOpenVrSessionFactory : IOpenVrSessionFactory
{
    public Task<IOpenVrSession> ConnectAsync(
        AppConfig config,
        string actionManifest,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IOpenVrSession>(
            new OpenVrInput(config.OpenVrDllPath, actionManifest, log));
    }
}
