using System.Windows.Threading;

namespace SvrBridge.Tray;

/// <summary>
/// A dedicated STA thread with its own <see cref="Dispatcher"/>, for WPF work
/// that must not run on the WinForms UI thread.
/// <para>
/// <c>RenderTargetBitmap</c> needs an STA thread with a live dispatcher, and
/// the WinForms message pump is neither STA-clean for this purpose nor
/// available to block on - a repaint on that thread would stall the settings
/// window. This type owns a second, invisible message loop purely so
/// <c>Dispatcher.Invoke</c> has somewhere safe to marshal onto.
/// </para>
/// <para>
/// Ownership is explicit rather than left to finalization: <see cref="Dispose"/>
/// asks the dispatcher to shut down and joins the thread, so a caller can prove
/// the thread actually stopped rather than trusting that it eventually will.
/// </para>
/// </summary>
internal sealed class WpfRenderThread : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public WpfRenderThread(string name)
    {
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = name
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Blocks the constructing thread only until the new thread has stood
        // up its dispatcher, which is milliseconds - not until any rendering
        // happens on it.
        Dispatcher = _dispatcherReady.Task.GetAwaiter().GetResult();
    }

    public Dispatcher Dispatcher { get; }

    /// <summary>True while the underlying OS thread is still running.</summary>
    public bool IsRunning => _thread.IsAlive;

    /// <summary>Runs <paramref name="action"/> on the render thread and returns its result.</summary>
    public T Invoke<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Dispatcher.Invoke(action);
    }

    private void RunMessageLoop()
    {
        // The first access to CurrentDispatcher on a thread creates one bound
        // to that thread; Dispatcher.Run then pumps it until InvokeShutdown.
        _dispatcherReady.SetResult(Dispatcher.CurrentDispatcher);
        Dispatcher.Run();
    }

    /// <summary>
    /// Shuts the dispatcher down and waits for the thread to exit. Safe to
    /// call from any thread, including while the render thread is idle between
    /// notifications - there is nothing to deadlock against because
    /// <c>InvokeShutdown</c> is itself dispatched rather than blocking inline.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
