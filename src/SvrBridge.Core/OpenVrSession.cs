namespace SvrBridge.Core;

public interface IOpenVrSession : IDisposable
{
    InputSnapshot Poll();

    ControllerSetup GetControllerSetup();

    void OpenBindingUi();

    /// <summary>
    /// Stands the SteamVR dashboard page up. Takes no image path: the page is
    /// rendered inside the session that owns the overlay and uploaded as
    /// pixels, not written to disk as a PNG for SteamVR to decode - see
    /// <c>VrDashboardController</c>.
    /// </summary>
    void ShowDashboard(
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate = true) =>
        throw new NotSupportedException("This SteamVR session cannot show a dashboard.");

    IReadOnlyList<ShortcutConfig> DrainCreatedShortcuts() => [];

    IReadOnlyList<string> DrainDeletedShortcutIds() => [];

    /// <summary>Settings changes made from the VR settings page since the last drain. Empty by default.</summary>
    IReadOnlyList<VrSettingsSnapshot> DrainVrSettingsChanges() => [];

    /// <summary>
    /// True once SteamVR has asked the app to quit, as opposed to the session
    /// dropping for a reason worth reconnecting after.
    /// </summary>
    bool IsQuitRequested() => false;

    /// <summary>
    /// Turns the development test overlay on or off. A no-op by default: it is
    /// a diagnostic for the hosted worker, and a session that cannot draw one
    /// should ignore the request rather than fail.
    /// </summary>
    void SetTestOverlayEnabled(bool enabled)
    {
    }

    /// <summary>
    /// Queues a head-anchored notification on the session's overlay. A no-op
    /// by default: only the hosted worker can draw one, and a session that
    /// cannot should drop the request rather than fail the caller.
    /// </summary>
    void ShowNotification(StreamerBotEventPayload payload)
    {
    }

    /// <summary>
    /// Appends one message to the session's wrist chat window. A no-op by
    /// default, for the same reason as <see cref="ShowNotification"/>.
    /// </summary>
    void ShowChatMessage(StreamerBotEventPayload payload)
    {
    }

    /// <summary>
    /// Replaces the session's known emote name → image URL lookup. A no-op
    /// by default, for the same reason as <see cref="ShowNotification"/>.
    /// </summary>
    void SetEmoteCatalog(IReadOnlyDictionary<string, string> catalog)
    {
    }

    /// <summary>
    /// Developer-only override: puts the SteamVR dashboard back on
    /// <c>SetOverlayTexture</c>, which it does not use by default because a
    /// dashboard overlay handle accepts the call and never displays the
    /// result. A no-op by default, for the same reason as
    /// <see cref="ShowNotification"/>.
    /// </summary>
    void SetDashboardTexturePathEnabled(bool enabled)
    {
    }

    /// <summary>
    /// Developer-only override: switches the chat window, notifications and
    /// the VR test overlay between the default <c>SetOverlayRaw</c> path and
    /// the persistent-texture path, together. A no-op by default, for the
    /// same reason as <see cref="ShowNotification"/>.
    /// </summary>
    void SetOverlayTexturePathEnabled(bool enabled)
    {
    }

    /// <summary>
    /// Applies a Streamer.bot <c>control</c> payload - show/hide/clear/anchor
    /// on the session's overlay surfaces. A no-op by default, for the same
    /// reason as <see cref="ShowNotification"/>.
    /// </summary>
    void ApplyControlCommand(StreamerBotEventPayload payload)
    {
    }

    /// <summary>
    /// Applies a desktop-made appearance/anchor/enable change to the
    /// session's overlay surfaces live, without a worker restart - the
    /// opposite direction of a VR settings-page edit, applied through the
    /// same effect. A no-op by default, for the same reason as
    /// <see cref="ShowNotification"/>.
    /// </summary>
    void ApplySettingsChange(VrSettingsSnapshot settings)
    {
    }
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
