using System.Security.Cryptography;
using System.Text;

namespace ToastAOT;

/// <summary>Familiar AppNotificationManager workflow for unpackaged applications. Subscribe before Register.</summary>
public sealed class AppNotificationManager : IDisposable
{
    private readonly object _gate = new();
    private ToastHost? _host;
    private string? _appId;
    private Guid _clsid;
    private bool _registering;
    private const int MaximumBufferedActivations = 1024;
    private EventHandler<AppNotificationActivatedEventArgs>? _invoked;
    private readonly Queue<AppNotificationActivatedEventArgs> _undelivered = new();

    public static AppNotificationManager Default { get; } = new();

    /// <summary>
    /// Raised on a COM thread for activations received while the process is running. Activations that arrive
    /// before any handler is subscribed (a cold start can deliver one before the UI exists) are buffered, up to 1024,
    /// and replayed on the thread that subscribes. Buffering does not wait for a UI dispatcher to become ready.
    /// If registration or subscription happens before UI startup completes, enqueue the work and retry dispatch
    /// until the UI framework accepts it.
    /// </summary>
    public event EventHandler<AppNotificationActivatedEventArgs>? NotificationInvoked
    {
        add
        {
            if (value == null)
            {
                return;
            }

            AppNotificationActivatedEventArgs[] queued;
            lock (_gate)
            {
                _invoked += value;
                queued = _undelivered.ToArray();
                _undelivered.Clear();
            }

            foreach (var args in queued)
            {
                Invoke(value, args);
            }
        }
        remove
        {
            lock (_gate)
            {
                _invoked -= value;
            }
        }
    }

    /// <summary>Number of activations received but not yet delivered because no handler is subscribed.</summary>
    public int PendingActivationCount
    {
        get
        {
            lock (_gate)
            {
                return _undelivered.Count;
            }
        }
    }

    public event Action<Exception>? ActivationHandlerFailed;

    public bool IsComActivationLaunch
    {
        get
        {
            return Environment.GetCommandLineArgs().Contains(ToastHost.ComActivationArgument, StringComparer.Ordinal);
        }
    }

    public AppNotificationSetting Setting
    {
        get
        {
            return (AppNotificationSetting)Host.Setting;
        }
    }

    public static bool IsSupported()
    {
        return PlatformSupport.IsSupported();
    }

    /// <summary>Derives a stable identity from the executable's full path. Moving the EXE changes this identity.</summary>
    /// <exception cref="InvalidOperationException">This manager is already registered or registration is in progress.</exception>
    public void Register()
    {
        var identity = DefaultIdentity();
        RegisterCore(identity.AppId, identity.Clsid, identity.Shortcut, null,
            Path.GetFileNameWithoutExtension(Environment.ProcessPath!), null);
    }

    /// <summary>Registers the default executable identity with a display name and local icon. Missing icon files are ignored.</summary>
    /// <exception cref="InvalidOperationException">This manager is already registered or registration is in progress.</exception>
    public void Register(string displayName, Uri iconUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(iconUri);
        var identity = DefaultIdentity();
        RegisterCore(identity.AppId, identity.Clsid, identity.Shortcut, null, displayName, iconUri);
    }

    private static (string AppId, Guid Clsid, string Shortcut) DefaultIdentity()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate executable.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()));

        return ("ToastAOT." + Convert.ToHexString(hash.AsSpan(0, 16)), new Guid(hash.AsSpan(0, 16)),
            "Notifications " + Path.GetFileNameWithoutExtension(exe) + " " + Convert.ToHexString(hash.AsSpan(0, 4)));
    }

    // Retain the four-argument CLR signature for binary compatibility; four-argument source calls bind here too.
    /// <summary>Registers an explicit identity without a custom icon; retained for existing compiled callers.</summary>
    /// <exception cref="InvalidOperationException">This manager is already registered or registration is in progress.</exception>
    public void Register(string appId, Guid activatorClsid, string displayName, string? executablePath)
    {
        Register(appId, activatorClsid, displayName, executablePath, iconUri: null);
    }

    /// <summary>Use an explicit stable identity when the install location may change, with an optional local icon file. Missing icon files are ignored.</summary>
    /// <exception cref="InvalidOperationException">This manager is already registered or registration is in progress.</exception>
    public void Register(string appId, Guid activatorClsid, string displayName, string? executablePath = null, Uri? iconUri = null)
    {
        RegisterCore(appId, activatorClsid, displayName, executablePath, displayName, iconUri);
    }

    // Retain the four-argument CLR signature for binary compatibility, as with Register.
    /// <summary>Adopts an explicit identity without a custom icon; retained for existing compiled callers.</summary>
    /// <exception cref="InvalidOperationException">This manager is already registered or registration is in progress.</exception>
    public void RegisterAdoptingExisting(string appId, Guid activatorClsid, string displayName, string? executablePath)
    {
        RegisterAdoptingExisting(appId, activatorClsid, displayName, executablePath, iconUri: null);
    }

    /// <summary>
    /// Like the explicit-identity <c>Register</c>, but replaces an existing Start Menu shortcut, AUMID key or CLSID key
    /// that this library did not create (an older hand-made registration). The default <c>Register</c> refuses those.
    /// A replaced shortcut is copied to <see cref="ReplacedShortcutBackupPath"/> first. Registrations that belong to a
    /// different ToastAOT identity are still refused. Use it once during upgrade or first run, then call the normal overload.
    /// An optional <paramref name="iconUri"/> supplies a local icon file for the notification identity. Missing files are ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">This manager is already registered or registration is in progress.</exception>
    public void RegisterAdoptingExisting(string appId, Guid activatorClsid, string displayName, string? executablePath = null, Uri? iconUri = null)
    {
        RegisterCore(appId, activatorClsid, displayName, executablePath, displayName, iconUri, adoptExisting: true);
    }

    /// <summary>Path of the backup made when adoption replaced a shortcut; otherwise null.</summary>
    public string? ReplacedShortcutBackupPath
    {
        get
        {
            lock (_gate)
            {
                return _host?.ReplacedShortcutBackupPath;
            }
        }
    }

    private void RegisterCore(string appId, Guid activatorClsid, string shortcutName, string? executablePath, string displayName, Uri? iconUri, bool adoptExisting = false)
    {
        lock (_gate)
        {
            if (_host != null || _registering)
            {
                throw new InvalidOperationException("Notifications are already registered.");
            }

            _registering = true;
        }
        ToastHost? host = null;
        try
        {
            host = ToastHost.Register(appId, activatorClsid, shortcutName, executablePath, displayName: displayName, iconUri: iconUri, adoptExisting: adoptExisting);
            host.ActivationHandlerFailed += ReportFailure;
            lock (_gate)
            {
                _host = host;
                _appId = appId;
                _clsid = activatorClsid;
            }
            host.Activated += ForwardActivation;
        }
        catch
        {
            host?.Dispose();
            lock (_gate)
            {
                _host = null;
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _registering = false;
            }
        }
    }

    internal void ForwardActivation(ToastActivation activation)
    {
        var args = new AppNotificationActivatedEventArgs(activation);
        EventHandler<AppNotificationActivatedEventArgs>? handlers;
        lock (_gate)
        {
            handlers = _invoked;
            if (handlers == null)
            {
                if (_undelivered.Count < MaximumBufferedActivations)
                {
                    _undelivered.Enqueue(args);
                    return;
                }
            }
        }

        // Application handlers must never run while the manager lock is held.
        if (handlers == null)
        {
            ReportFailure(new InvalidOperationException("Notification activation dropped: no handler subscribed and the buffer is full."));
            return;
        }

        Invoke(handlers, args);
    }

    private void Invoke(EventHandler<AppNotificationActivatedEventArgs> handlers, AppNotificationActivatedEventArgs args)
    {
        foreach (EventHandler<AppNotificationActivatedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            ActivationHandlerFailed?.Invoke(exception);
        }
        catch
        {
        }
    }

    private ToastHost Host
    {
        get
        {
            lock (_gate)
            {
                return _host ?? throw new InvalidOperationException("Call Register first.");
            }
        }
    }

    public void Show(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Host.Show(notification.Snapshot());
    }

    public bool WaitForActivation(TimeSpan timeout)
    {
        return Host.WaitForActivation(timeout);
    }

    public Task<IReadOnlyList<AppNotification>> GetAllAsync()
    {
        var host = Host;

        return Task.Run<IReadOnlyList<AppNotification>>(() => host.GetAll().Select(AppNotification.FromMessage).ToArray());
    }

    public Task RemoveAllAsync()
    {
        var host = Host;
        return Task.Run(host.ClearHistory);
    }

    public Task RemoveByGroupAsync(string group)
    {
        var host = Host;
        return Task.Run(() => host.RemoveByGroup(group));
    }

    public Task RemoveByTagAndGroupAsync(string tag, string group)
    {
        var host = Host;
        return Task.Run(() => host.RemoveFromHistory(tag, group));
    }

    public Task RemoveByTagAsync(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var host = Host;
        return Task.Run(() =>
        {
            var groups = host.GetAll()
                .Where(notification => notification.Tag == tag)
                .Select(notification => notification.Group)
                .Distinct();
            foreach (string? group in groups)
            {
                host.RemoveFromHistory(tag, group);
            }
        });
    }

    public Task<AppNotificationProgressResult> UpdateAsync(AppNotificationProgressData data, string tag, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var snapshot = data.Snapshot();
        var host = Host;

        return Task.Run(() => host.Update(snapshot, tag, group) switch
            {
                ToastUpdateResult.Succeeded => AppNotificationProgressResult.Succeeded,
                ToastUpdateResult.NotificationNotFound => AppNotificationProgressResult.AppNotificationNotFound,
                _ => throw new InvalidOperationException("Windows rejected the notification progress update.")
            });
    }

    public void Unregister()
    {
        UnregisterCore(removePersistentRegistration: false);
    }

    /// <summary>Removes process and persistent registration. Register is rejected until removal finishes.</summary>
    public void UnregisterAll()
    {
        UnregisterCore(removePersistentRegistration: true);
    }

    private void UnregisterCore(bool removePersistentRegistration)
    {
        ToastHost? host;
        string? appId;
        Guid clsid;
        lock (_gate)
        {
            if (_registering)
            {
                throw new InvalidOperationException("Registration is in progress.");
            }

            // Keep registration excluded until both process cleanup and any persistent removal finish.
            _registering = true;
            host = _host;
            _host = null;
            appId = _appId;
            clsid = _clsid;
        }
        try
        {
            host?.Dispose();
            if (removePersistentRegistration)
            {
                if (appId == null)
                {
                    var identity = DefaultIdentity();
                    appId = identity.AppId;
                    clsid = identity.Clsid;
                }

                ToastHost.UnregisterAll(appId, clsid);
            }
        }
        finally
        {
            lock (_gate)
            {
                _undelivered.Clear();
                _registering = false;
            }
        }
    }

    public void Dispose()
    {
        Unregister();
    }
}
