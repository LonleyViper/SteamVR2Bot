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
}

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
