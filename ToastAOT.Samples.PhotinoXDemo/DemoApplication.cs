using System.Text.Json;
using Microsoft.Win32;
using Photino.NET;
using ToastAOT.Builder;

namespace ToastAOT.Samples.PhotinoXDemo;

internal sealed class DemoApplication : IDisposable
{
    internal const string AppId = "ToastAOT.Samples.PhotinoXDemo535";
    internal static readonly Guid ActivatorClsid = new("2F7696F8-EC4B-4AE5-8BED-C081C18FFC8A");
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ToastAOT", "PhotinoXDemo");

    private const string NotificationTag = "photino-test";
    private const string NotificationGroup = "photino-demo";
    private readonly AppNotificationManager _notifications = new();
    private readonly PhotinoApplication _application = new();
    private readonly PhotinoWindow _window;
    private readonly Uri _pageUri;
    private readonly bool _smokeTest;
    private readonly object _stateLock = new();
    private readonly List<string> _events = [];
    private readonly TaskCompletionSource _activationRendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _pageReady;
    private volatile bool _closing;
    private bool _smokeStarted;
    private string _status = "Ready";
    private string _history = "Refresh to read Windows notification history.";
    private string _lastAction = "";
    private string _lastReply = "";
    private string _lastChoice = "";

    internal DemoApplication(bool smokeTest)
    {
        _smokeTest = smokeTest;
        _pageUri = new Uri(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
        _window = new PhotinoWindow()
            .SetTitle("ToastAOT - PhotinoX")
            .SetUseOsDefaultSize(false)
            .SetSize(900, 760)
            .SetUserDataFolder(Path.Combine(DataDirectory, "WebView2"))
            .SetDevToolsEnabled(false)
            .Load(_pageUri);

        _window.WebMessageReceived += OnWebMessageReceived;
        _window.NavigationStarting += (_, navigation) =>
        {
            navigation.Cancel = navigation.Uri != _pageUri;
        };
        _window.Closed += (_, _) =>
        {
            _closing = true;
        };

        // Subscribe before registration so cold-start arguments can be buffered
        // and displayed when the WebView completes its ready handshake.
        _notifications.NotificationInvoked += OnNotificationInvoked;
        _notifications.ActivationHandlerFailed += OnActivationHandlerFailed;
        _notifications.Register(AppId, ActivatorClsid, "ToastAOT PhotinoX Demo");
        Record("Registered notification COM server. Process " + Environment.ProcessId);
    }

    internal int Run(bool sendAndExit)
    {
        if (sendAndExit)
        {
            SendNotification(false);
            return 0;
        }

        return _application.Run(_window);
    }

    private async void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs message)
    {
        if (message.Uri != _pageUri)
        {
            return;
        }

        try
        {
            switch (message.Message)
            {
                case "ready":
                    _pageReady = true;
                    Record("WebView ready; JavaScript-to-.NET bridge verified.");
                    PublishSnapshot();
                    if (_smokeTest && !_smokeStarted)
                    {
                        _smokeStarted = true;
                        await RunSmokeTestAsync();
                    }
                    break;

                case "activation-rendered":
                    _ = _activationRendered.TrySetResult();
                    break;

                case "send":
                    SendNotification(false);
                    await RefreshHistoryAsync();
                    break;

                case "send-exit":
                    SendNotification(false);
                    _application.Shutdown();
                    break;

                case "icon-custom":
                case "icon-omitted":
                case "icon-missing":
                    await SendIconTestAsync(message.Message);
                    break;

                case "refresh":
                    await RefreshHistoryAsync();
                    break;

                case "clear":
                    await _notifications.RemoveByGroupAsync(NotificationGroup);
                    Record("Removed this demo's notification group.");
                    await RefreshHistoryAsync();
                    break;

                case "exit":
                    _application.Shutdown();
                    break;
            }
        }
        catch (Exception exception)
        {
            Record("ERROR: " + exception.Message);
            if (_smokeTest)
            {
                File.WriteAllText(SmokeResultPath, "FAIL\n" + exception);
                _application.Dispatcher.BeginInvoke(() =>
                {
                    _application.Shutdown(1);
                });
            }
        }

        PublishSnapshot();
    }

    private void SendNotification(bool suppressDisplay)
    {
        AppNotification notification = new AppNotificationBuilder()
            .AddText("PhotinoX + ToastAOT notifications")
            .AddText("Enter test text and choose Reply, or click the body to open the app.")
            .AddArgument("action", "open")
            .AddTextBox("reply", "Type a reply", "Reply")
            .AddComboBox(new AppNotificationComboBox("choice")
                .AddItem("one", "One")
                .AddItem("two", "Two")
                .SetSelectedItem("two"))
            .AddButton(new AppNotificationButton("Reply")
                .AddArgument("action", "reply")
                .SetInputId("reply"))
            .AddButton(new AppNotificationButton("Dismiss")
                .AddArgument("action", "dismiss"))
            .AddProgressBar(new AppNotificationProgressBar().BindTitle().BindValueStringOverride())
            .SetTag(NotificationTag)
            .SetGroup(NotificationGroup)
            .BuildNotification();

        notification.Progress = new AppNotificationProgressData(1)
        {
            Title = "Test progress",
            Status = "Waiting for input",
            Value = 0.25
        };
        notification.SuppressDisplay = suppressDisplay;
        notification.Expiration = DateTimeOffset.UtcNow.AddHours(1);
        _notifications.Show(notification);
        Record("Notification submitted" + (suppressDisplay ? " with popup suppressed." : ". Check the popup or Notification Center."));
    }

    private async Task SendIconTestAsync(string command)
    {
        string mode = command switch
        {
            "icon-custom" => "custom",
            "icon-omitted" => "omitted",
            "icon-missing" => "missing",
            _ => throw new ArgumentException("Unknown icon test.", nameof(command))
        };
        Uri? iconUri = mode switch
        {
            "custom" => new Uri(Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon-test.png")),
            "missing" => new Uri(Path.Combine(DataDirectory, "missing-icon-" + Guid.NewGuid().ToString("N") + ".png")),
            _ => null
        };
        if (mode == "custom" && !File.Exists(iconUri!.LocalPath))
        {
            throw new FileNotFoundException("The custom icon test asset was not published.", iconUri.LocalPath);
        }

        // This is the same identity throughout, so omitted/missing tests exercise stale icon cleanup.
        // Keep the event subscriptions and UI dispatcher alive while replacing the process registration.
        _notifications.Unregister();
        _notifications.Register(AppId, ActivatorClsid, "ToastAOT PhotinoX Demo", iconUri: iconUri);

        using (var identity = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + AppId))
        {
            string? registeredIcon = identity?.GetValue("IconUri") as string;
            string? expectedIcon = mode == "custom" ? iconUri!.LocalPath : null;
            if (identity == null || registeredIcon != expectedIcon)
            {
                throw new InvalidOperationException("Icon registry metadata did not match the selected test.");
            }
            Record("Icon metadata verified: " + mode + "; IconUri=" + (registeredIcon ?? "<absent>"));
        }

        var notification = new AppNotificationBuilder()
            .AddText("ToastAOT icon test: " + mode)
            .AddText(mode == "custom"
                ? "Look for a magenta square with a white T in the application logo."
                : "Icon metadata was cleared. Windows may retain the previous application logo.")
            .AddArgument("action", "icon-" + mode)
            .SetTag("icon-" + mode)
            .SetGroup(NotificationGroup)
            .BuildNotification();
        notification.Expiration = DateTimeOffset.UtcNow.AddMinutes(10);
        _notifications.Show(notification);
        Record("Icon test submitted: " + mode + ". Verify its application logo in Notification Center.");
        await RefreshHistoryAsync();
    }

    private void OnNotificationInvoked(object? sender, AppNotificationActivatedEventArgs activation)
    {
        lock (_stateLock)
        {
            _lastAction = activation.Arguments.GetValueOrDefault("action", "");
            _lastReply = activation.UserInput.GetValueOrDefault("reply", "");
            _lastChoice = activation.UserInput.GetValueOrDefault("choice", "");
        }

        Record("COM activation: " + activation.Argument + "; inputs: " +
            string.Join(", ", activation.UserInput.Select(input => input.Key + "=" + input.Value)));
        PublishSnapshot();
    }

    private void OnActivationHandlerFailed(Exception exception)
    {
        Record("Activation handler failed: " + exception.Message);
        PublishSnapshot();
    }

    private async Task RefreshHistoryAsync()
    {
        IReadOnlyList<AppNotification> history = await _notifications.GetAllAsync();
        lock (_stateLock)
        {
            _history = history.Count == 0
                ? "No notifications in Windows history."
                : string.Join(Environment.NewLine, history.Select(notification => notification.Tag + " / " + notification.Group));
        }

        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        if (!_pageReady || _closing)
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
            {
                return;
            }

            DemoSnapshot snapshot;
            lock (_stateLock)
            {
                snapshot = new DemoSnapshot
                {
                    ProcessId = Environment.ProcessId,
                    Status = _status,
                    Setting = _notifications.Setting.ToString(),
                    Launch = GetLaunchDescription(),
                    History = _history,
                    Events = string.Join(Environment.NewLine, _events),
                    LastAction = _lastAction,
                    LastReply = _lastReply,
                    LastChoice = _lastChoice
                };
            }

            _window.SendWebMessage(JsonSerializer.Serialize(snapshot, DemoJsonContext.Default.DemoSnapshot));
        });
    }

    private string GetLaunchDescription()
    {
        string launch = _notifications.IsComActivationLaunch ? "COM cold start" : "Normal launch";
        return launch;
    }

    private void Record(string message)
    {
        string line = DateTimeOffset.Now.ToString("O") + " " + message;
        lock (_stateLock)
        {
            _status = message;
            _events.Add(line);
            if (_events.Count > 100)
            {
                _events.RemoveAt(0);
            }

            File.AppendAllText(Path.Combine(DataDirectory, "events.log"), line + Environment.NewLine);
        }
    }

    private static string SmokeResultPath
    {
        get
        {
            return Path.Combine(DataDirectory, "smoke-result.txt");
        }
    }

    private async Task RunSmokeTestAsync()
    {
        File.WriteAllText(SmokeResultPath, "RUNNING\n");
        SendNotification(true);
        AppNotification stored = await WaitForNotificationAsync(0.25);
        Record("PASS: real Windows history contains initial progress.");

        uint sequence = checked((stored.Progress?.SequenceNumber ?? 1) + 1);
        AppNotificationProgressResult result = await _notifications.UpdateAsync(
            new AppNotificationProgressData(sequence)
            {
                Value = 0.75,
                Status = "Updated by Photino smoke test"
            },
            NotificationTag,
            NotificationGroup);

        if (result != AppNotificationProgressResult.Succeeded)
        {
            throw new InvalidOperationException("Progress update failed: " + result);
        }

        await WaitForNotificationAsync(0.75);
        Record("PASS: progress update persisted in Windows history.");
        File.AppendAllText(SmokeResultPath, "WAITING_FOR_COM\n");
        await _activationRendered.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Record("PASS: separate-process COM callback rendered in the WebView through the dispatcher.");
        await _notifications.RemoveByTagAndGroupAsync(NotificationTag, NotificationGroup);
        IReadOnlyList<AppNotification> history = await _notifications.GetAllAsync();
        if (history.Any(notification => notification.Tag == NotificationTag && notification.Group == NotificationGroup))
        {
            throw new InvalidOperationException("Test notification was not removed.");
        }

        Record("PASS: notification removal verified.");
        lock (_stateLock)
        {
            File.WriteAllText(SmokeResultPath, "PASS\n" + string.Join(Environment.NewLine, _events));
        }
        _application.Dispatcher.BeginInvoke(() =>
        {
            _application.Shutdown();
        });
    }

    private async Task<AppNotification> WaitForNotificationAsync(double value)
    {
        for (int attempt = 0; attempt < 50; ++attempt)
        {
            IReadOnlyList<AppNotification> history = await _notifications.GetAllAsync();
            AppNotification? notification = history.FirstOrDefault(item => item.Tag == NotificationTag && item.Group == NotificationGroup && item.Progress?.Value == value);

            if (notification != null)
            {
                return notification;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Expected progress value was not found in Windows history.");
    }

    public void Dispose()
    {
        _closing = true;
        _notifications.Dispose();
    }
}
