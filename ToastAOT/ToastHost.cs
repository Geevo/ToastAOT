namespace ToastAOT;

/// <summary>
/// Windows toast delivery and native activation for an unpackaged Win32 application.
/// On a COM-triggered cold start, call <see cref="Register"/> before other app startup work
/// and keep this host alive to receive the activation.
/// </summary>
public sealed unsafe class ToastHost : IDisposable
{
    public const string ComActivationArgument = "--toastaot-activation";

    private readonly object _lifetimeLock = new();
    private readonly object _handlerLock = new();
    private readonly Queue<ToastActivation> _undelivered = new();
    private Action<ToastActivation>? _activated;
    private Thread? _serverThread;
    private ManualResetEventSlim? _stop;
    private volatile bool _disposed;
    private int _activationCount;
    private int _callbackObjects;
    private static readonly HashSet<Guid> ActiveServers = [];

    public string AppId { get; }

    public Guid ActivatorClsid { get; }

    /// <summary>
    /// True when Windows started this process to deliver a notification activation (cold start), as shown by the
    /// <see cref="ComActivationArgument"/> launch flag. The activation may already be waiting, or may arrive moments
    /// after the process starts, before any window or UI dispatcher exists.
    /// </summary>
    public bool IsComActivationLaunch { get; }

    /// <summary>
    /// Set only when adoption was requested and an existing unmarked shortcut was replaced: the path of the copy
    /// of that shortcut. The ToastAOT-owned registration removes the shortcut on uninstall, not this backup.
    /// </summary>
    public string? ReplacedShortcutBackupPath { get; private set; }

    /// <summary>Delivered on a COM callback thread. Buffered activations replay on the subscribing thread. Use your UI dispatcher.</summary>
    public event Action<ToastActivation>? Activated
    {
        add
        {
            if (value == null)
            {
                return;
            }

            ToastActivation[] queued;
            lock (_handlerLock)
            {
                ThrowIfDisposed();
                _activated += value;
                queued = _undelivered.ToArray();
                _undelivered.Clear();
            }
            foreach (var activation in queued)
            {
                Deliver(value, activation);
            }
        }
        remove
        {
            lock (_handlerLock)
            {
                _activated -= value;
            }
        }
    }

    /// <summary>Application callback errors are reported here rather than crossing the COM boundary.</summary>
    public event Action<Exception>? ActivationHandlerFailed;

    internal ToastHost(string appId, Guid clsid, bool comLaunch)
    {
        AppId = appId;
        ActivatorClsid = clsid;
        IsComActivationLaunch = comLaunch;
    }

    /// <summary>
    /// Register using an app-specific, stable CLSID (do not randomly generate on each launch).
    /// By default an existing shortcut, AUMID or CLSID key that this library did not create makes registration fail.
    /// Pass <paramref name="adoptExisting"/> to take over such unmarked entries (for example from an older hand-made
    /// installer); the replaced shortcut is backed up, see <see cref="ReplacedShortcutBackupPath"/>.
    /// Registrations owned by a different ToastAOT identity are never adopted.
    /// The optional icon must be an absolute local file URI. Missing files are ignored; omitting the icon
    /// or supplying a missing file clears previous icon metadata.
    /// </summary>
    public static ToastHost Register(string appId, Guid activatorClsid, string shortcutName,
        string? executablePath = null, string[]? startupArguments = null, string? displayName = null, Uri? iconUri = null,
        bool adoptExisting = false)
    {
        ToastRegistration.ValidateIdentity(appId, activatorClsid);
        if (!PlatformSupport.IsSupported())
        {
            throw new PlatformNotSupportedException("An unelevated, unpackaged application on Windows 10 build 17763 or later is required.");
        }

        string exe = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the executable path.");
        bool comLaunch = (startupArguments ?? Environment.GetCommandLineArgs()).Contains(ComActivationArgument, StringComparer.Ordinal);
        var host = new ToastHost(appId, activatorClsid, comLaunch);
        lock (ActiveServers)
        {
            if (!ActiveServers.Add(activatorClsid))
            {
                throw new InvalidOperationException("This CLSID already has an active host in this process.");
            }
        }

        try
        {
            host.ReplacedShortcutBackupPath = ToastRegistration.Install(appId, activatorClsid, shortcutName, exe, displayName, iconUri, host.StartComServer, adoptExisting);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private void StartComServer()
    {
        // A dedicated MTA thread avoids the app's STA/MTA state and retains the class
        // registration even if the caller is a console command or WinForms UI thread.
        _stop = new ManualResetEventSlim();
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _serverThread = new Thread(() =>
        {
            void* factory = null;
            uint cookie = 0;
            bool initialized = false;
            try
            {
                Interop.Check(Interop.CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */));
                initialized = true;
                factory = ToastComActivator.CreateFactory(this);
                var clsid = ActivatorClsid;
                Interop.Check(Interop.CoRegisterClassObject(&clsid, factory, Interop.CLSCTX_LOCAL_SERVER, Interop.REGCLS_MULTIPLEUSE, out cookie));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            try
            {
                if (failure == null)
                {
                    _stop!.Wait();
                }
            }
            finally
            {
                if (cookie != 0)
                {
                    _ = Interop.CoRevokeClassObject(cookie);
                }

                if (factory != null)
                {
                    ToastComActivator.ReleaseFactory(factory);
                }

                if (initialized)
                {
                    Interop.CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "ToastAOT COM server"
        };
        _serverThread.Start();
        ready.Wait();
        if (failure != null)
        {
            throw new InvalidOperationException("Unable to register COM toast activator.", failure);
        }
    }

    internal void DispatchActivation(ToastActivation activation)
    {
        // Buffer cold-start activations arriving before the caller subscribes.
        // Activation can arrive while Register() is returning.
        Action<ToastActivation>? handlers;
        lock (_handlerLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ToastHost));
            }

            if (!StringComparer.Ordinal.Equals(activation.AppId, AppId))
            {
                throw new ArgumentException("Activation identity does not match this application.");
            }

            handlers = _activated;
            if (handlers == null)
            {
                if (_undelivered.Count >= 1024)
                {
                    throw new InvalidOperationException("Activation queue is full.");
                }

                _undelivered.Enqueue(activation);
            }
        }
        if (handlers != null)
        {
            Deliver(handlers, activation);
        }
    }

    private void Deliver(Action<ToastActivation> handlers, ToastActivation activation)
    {
        try
        {
            InvokeHandlers(handlers, activation);
        }
        finally
        {
            lock (_handlerLock)
            {
                ++_activationCount;
                Monitor.PulseAll(_handlerLock);
            }
        }
    }

    internal void RetainComCallback()
    {
        lock (_handlerLock)
        {
            ThrowIfDisposed();
            ++_callbackObjects;
        }
    }

    internal void ReleaseComCallback()
    {
        lock (_handlerLock)
        {
            --_callbackObjects;
            Monitor.PulseAll(_handlerLock);
        }
    }

    private void InvokeHandlers(Action<ToastActivation> handlers, ToastActivation activation)
    {
        foreach (Action<ToastActivation> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(activation);
            }
            catch (Exception ex)
            {
                try
                {
                    ActivationHandlerFailed?.Invoke(ex);
                }
                catch
                { /* COM callbacks must never throw. */
                }
            }
        }
    }

    /// <summary>
    /// Waits for an activation handler to complete and for COM clients to release callback
    /// objects, so a console cold start can exit after the RPC reply. GUI apps should keep
    /// their normal message loop running. A client retaining its callback can cause timeout.
    /// </summary>
    public bool WaitForActivation(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        lock (_handlerLock)
        {
            while (true)
            {
                ThrowIfDisposed();
                if (_activationCount > 0 && _callbackObjects == 0)
                {
                    return true;
                }

                var remaining = timeout == Timeout.InfiniteTimeSpan ? timeout : timeout - timer.Elapsed;
                if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                _ = Monitor.Wait(_handlerLock, remaining);
            }
        }
    }

    public void Show(ToastMessage message)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        WinRtToastSender.Show(AppId, message);
    }

    public void ClearHistory()
    {
        ThrowIfDisposed();
        WinRtToastSender.ClearHistory(AppId);
    }

    public ToastSetting Setting
    {
        get
        {
            ThrowIfDisposed();
            return WinRtToastSender.GetSetting(AppId);
        }
    }

    public IReadOnlyList<ToastMessage> GetAll()
    {
        ThrowIfDisposed();
        return WinRtToastSender.GetAll(AppId);
    }

    public void RemoveByGroup(string group)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        WinRtToastSender.RemoveByGroup(AppId, group);
    }

    public ToastUpdateResult Update(ToastProgressData progress, string tag, string? group = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return WinRtToastSender.Update(AppId, progress, tag, group);
    }

    /// <summary>Installer/uninstaller cleanup. Dispose active hosts first. Persisted notifications are removed.</summary>
    public static void UnregisterAll(string appId, Guid activatorClsid)
    {
        ToastRegistration.ValidateIdentity(appId, activatorClsid);
        lock (ActiveServers)
        {
            if (ActiveServers.Contains(activatorClsid))
            {
                throw new InvalidOperationException("Dispose the active host before uninstalling.");
            }
        }
        // Validate ownership before touching notification history.
        ToastRegistration.Uninstall(appId, activatorClsid, () =>
        {
            WinRtToastSender.ClearHistory(appId);
        });
    }

    public void RemoveFromHistory(string tag, string? group = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        WinRtToastSender.RemoveFromHistory(AppId, tag, group);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ToastHost));
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        _stop?.Set();
        if (_serverThread != Thread.CurrentThread)
        {
            _serverThread?.Join();
        }

        _stop?.Dispose();
        lock (ActiveServers)
        {
            _ = ActiveServers.Remove(ActivatorClsid);
        }

        lock (_handlerLock)
        {
            _undelivered.Clear();
            _activated = null;
            Monitor.PulseAll(_handlerLock);
        }
    }
}
