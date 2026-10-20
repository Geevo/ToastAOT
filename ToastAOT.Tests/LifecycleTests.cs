using System.Diagnostics;
using Microsoft.Win32;
using ToastAOT.Builder;

namespace ToastAOT.Tests;

/// <summary>
/// Regression, adoption, install/upgrade/uninstall and crash-recovery checks. Registry and shell-link work uses
/// dedicated identities so the shared integration identity is never disturbed.
/// </summary>
internal static class LifecycleTests
{
    private const string AdoptApp = "ToastAOT.Tests.Adopt";
    private const string AdoptShortcut = "ToastAOT Adoption Tests";
    private static readonly Guid AdoptClsid = new("6E1C7B0A-3D7E-4F1B-9C55-2A4B8E0F1D21");

    private const string OtherApp = "ToastAOT.Tests.AdoptOther";
    private static readonly Guid OtherClsid = new("0B8B7A5E-5B24-4B52-8E0D-5E3E7E6F4A10");

    private const string UpgradeApp = "ToastAOT.Tests.Upgrade";
    private const string UpgradeShortcut = "ToastAOT Upgrade Tests";
    private static readonly Guid UpgradeClsid = new("A94F2B5C-0E52-4A8E-B1B6-3B2A1D2C9E77");

    internal const string CrashApp = "ToastAOT.Tests.Crash";
    internal const string CrashShortcut = "ToastAOT Crash Tests";
    internal static readonly Guid CrashClsid = new("C2D0E7A1-7B1F-4D3A-9E55-6F0A8B1C2D33");

    private const string SentinelApp = "ToastAOT.Tests.Sentinel";

    private static int _checks;

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new Exception(message);
        }

        ++_checks;
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            ++_checks;
            return;
        }

        throw new Exception($"Expected {typeof(T).Name}");
    }

    // ----- Portable: cold-start activation buffering in the familiar manager ---------------------------

    internal static void RunPortable()
    {
        var activation = new ToastActivation("app", "action=open;who=a%3Bb", new Dictionary<string, string> { ["reply"] = "hi" });
        using var manager = new AppNotificationManager();
        manager.ForwardActivation(activation);
        manager.ForwardActivation(activation);
        Check(manager.PendingActivationCount == 2, "Activations before subscription were not buffered");

        // The first subscriber receives the backlog on its own thread, as a UI thread would.
        int subscriberThread = -1;
        int replayThread = -2;
        int received = 0;
        string? who = null;
        var subscriber = Task.Run(() =>
        {
            subscriberThread = Environment.CurrentManagedThreadId;
            manager.NotificationInvoked += (_, args) =>
            {
                replayThread = Environment.CurrentManagedThreadId;
                who = args.Arguments["who"];
                ++received;
            };
        });
        subscriber.GetAwaiter().GetResult();
        Check(received == 2 && who == "a;b" && replayThread == subscriberThread, "Buffered activations were not replayed on the subscribing thread");
        Check(manager.PendingActivationCount == 0, "Replay left activations buffered");

        manager.ForwardActivation(activation);
        Check(received == 3, "Live activation not delivered to the subscriber");

        // A throwing handler (for example a UI marshal before the dispatcher exists) must not crash or lose isolation.
        using var failing = new AppNotificationManager();
        int failures = 0;
        failing.ActivationHandlerFailed += _ =>
        {
            ++failures;
        };
        failing.ForwardActivation(activation);
        failing.NotificationInvoked += (_, _) =>
        {
            throw new InvalidOperationException("UI dispatcher does not exist yet");
        };
        Check(failures == 1, "A handler failure during replay was not reported");
        failing.ForwardActivation(activation);
        Check(failures == 2, "A handler failure during live delivery was not reported");

        using var full = new AppNotificationManager();
        int overflow = 0;
        full.ActivationHandlerFailed += _ =>
        {
            ++overflow;
        };
        for (int i = 0; i < 1030; ++i)
        {
            full.ForwardActivation(activation);
        }

        Check(full.PendingActivationCount == 1024 && overflow == 6, "Activation buffer bound not enforced");

        // An error handler may synchronously dispatch to a different thread that reads the manager.
        int observedPending = -1;
        bool completedDuringErrorHandler = false;
        var reader = new Thread(() =>
        {
            observedPending = full.PendingActivationCount;
        })
        {
            IsBackground = true
        };
        full.ActivationHandlerFailed += _ =>
        {
            reader.Start();
            completedDuringErrorHandler = reader.Join(TimeSpan.FromSeconds(3));
        };
        full.ForwardActivation(activation);
        Check(reader.Join(TimeSpan.FromSeconds(3)), "Overflow error handler left a blocked manager reader");
        Check(completedDuringErrorHandler && observedPending == 1024,
            "Overflow error handler ran while the manager lock was held");

        using var removed = new AppNotificationManager();
        int removedCalls = 0;
        EventHandler<AppNotificationActivatedEventArgs> handler = (_, _) =>
        {
            ++removedCalls;
        };
        removed.NotificationInvoked += handler;
        removed.NotificationInvoked -= handler;
        removed.ForwardActivation(activation);
        Check(removedCalls == 0 && removed.PendingActivationCount == 1, "An unsubscribed handler still received activations");

        // The cold-launch flag is read from the command line without needing registration.
        using var coldHost = new ToastHost("app", Guid.NewGuid(), true);
        using var warmHost = new ToastHost("app", Guid.NewGuid(), false);
        Check(coldHost.IsComActivationLaunch && !warmHost.IsComActivationLaunch, "IsComActivationLaunch not reported");
        Check(ColdStartProbe.REGDB_E_CLASSNOTREG == unchecked((int)0x80040154), "Probe HRESULT constant");

        // Empty identifiers are legitimate (builder-built notifications default to ""), and must validate.
        new ToastMessage("<toast/>")
        {
            Tag = "",
            Group = ""
        }.Validate();
        var plain = new AppNotificationBuilder().AddText("no identity").BuildNotification();
        Check(plain.Tag == "" && plain.Group == "", "Builder default tag/group are not empty");
        var snapshot = plain.Snapshot();
        Check(string.IsNullOrEmpty(snapshot.Tag) && string.IsNullOrEmpty(snapshot.Group), "Snapshot invented a tag or group");
        Console.WriteLine($"PASS: {_checks} lifecycle/buffering portable checks");
        _checks = 0;
    }

    // ----- Windows: empty tag/group regression -----------------------------------------------------------

    internal static void ShowWithoutTagOrGroup(ToastHost host)
    {
        host.ClearHistory();
        // null (library default), empty string (AppNotification default), and tag-only / group-only.
        host.Show(new ToastBuilder().AddText("no tag no group (null)").SetSuppressPopup().Build());
        host.Show(new ToastMessage(new ToastBuilder().AddText("no tag no group (empty)").Build().Xml)
        {
            Tag = "",
            Group = "",
            SuppressPopup = true
        });
        host.Show(new ToastMessage(new ToastBuilder().AddText("tag only").Build().Xml)
        {
            Tag = "only-tag",
            Group = "",
            SuppressPopup = true
        });
        host.Show(new ToastMessage(new ToastBuilder().AddText("group only").Build().Xml)
        {
            Tag = "",
            Group = "only-group",
            SuppressPopup = true
        });
        Check(SpinWait.SpinUntil(() =>
        {
            return host.GetAll().Count >= 3;
        }, TimeSpan.FromSeconds(10)), "Notifications without tag/group did not enter history");
        var all = host.GetAll();
        Check(all.Count(m => m.Tag == "" && m.Group == "") >= 1, "Untagged notifications missing from history");
        Check(all.Any(m => m.Tag == "only-tag" && m.Group == "") && all.Any(m => m.Tag == "" && m.Group == "only-group"), "Partially identified notifications mismatched");
        host.ClearHistory();
        Check(host.GetAll().Count == 0, "History not cleared after untagged tests");
    }

    internal static void BuilderNotificationWithoutTagOrGroup(AppNotificationManager manager)
    {
        // The exact scenario from a real integration: builder-built, no SetTag/SetGroup.
        var notification = new AppNotificationBuilder().AddText("builder default identity").BuildNotification();
        notification.SuppressDisplay = true;
        Check(notification.Tag == "" && notification.Group == "", "Precondition: builder default tag/group");
        manager.Show(notification);
        Check(SpinWait.SpinUntil(() =>
        {
            return manager.GetAllAsync().GetAwaiter().GetResult().Any(n => n.Payload.Contains("builder default identity"));
        }, TimeSpan.FromSeconds(10)), "Builder-built notification without tag/group was not stored");
        manager.RemoveAllAsync().GetAwaiter().GetResult();
        Check(SpinWait.SpinUntil(() =>
        {
            return manager.GetAllAsync().GetAwaiter().GetResult().Count == 0;
        }, TimeSpan.FromSeconds(5)), "RemoveAll did not clear untagged notification");
    }

    // ----- Windows: expiration / priority / reboot flag readback and real expiry --------------------------

    /// <summary>Returns a one-line description of what Windows reported, for the validation log.</summary>
    internal static string ReadbackAndExpiry(ToastHost host)
    {
        host.ClearHistory();
        DateTimeOffset requested = DateTimeOffset.UtcNow.AddMinutes(30);
        host.Show(new ToastBuilder().AddText("readback").SetIdentity("readback", "lifecycle").SetSuppressPopup()
            .SetPriority(ToastPriority.High).SetExpiration(requested).Build() with
        {
            ExpiresOnReboot = true
        });
        Check(SpinWait.SpinUntil(() =>
        {
            return host.GetAll().Any(m => m.Tag == "readback");
        }, TimeSpan.FromSeconds(10)), "Readback notification not stored");
        var message = host.GetAll().Single(m => m.Tag == "readback");
        Check(message.Expiration.HasValue && Math.Abs((message.Expiration.Value - requested).TotalSeconds) < 2, "Expiration did not round-trip");
        Check(Enum.IsDefined(message.Priority), "Priority readback invalid");
        string description = $"expiration round-trip OK; priority requested High, read back {message.Priority}; ExpiresOnReboot requested true, read back {message.ExpiresOnReboot}";
        host.ClearHistory();

        // Real expiry: Windows must remove the notification from history after the expiration time.
        host.Show(new ToastBuilder().AddText("expires soon").SetIdentity("expiry", "lifecycle").SetSuppressPopup()
            .SetExpiration(DateTimeOffset.UtcNow.AddSeconds(8)).Build());
        Check(SpinWait.SpinUntil(() =>
        {
            return host.GetAll().Any(m => m.Tag == "expiry");
        }, TimeSpan.FromSeconds(5)), "Short-lived notification not stored");
        var timer = Stopwatch.StartNew();
        bool gone = SpinWait.SpinUntil(() =>
        {
            Thread.Sleep(500);
            return host.GetAll().All(m => m.Tag != "expiry");
        }, TimeSpan.FromSeconds(90));
        description += gone ? $"; 8 s expiration removed it from history after {timer.Elapsed.TotalSeconds:F1} s" : "; 8 s expiration NOT removed from history within 90 s";
        host.ClearHistory();
        Check(gone, "Expired notification stayed in history for 90 s (" + description + ")");
        return description;
    }

    // ----- Windows: manager buffering with a real cross-process COM activation ----------------------------

    internal static void ManagerBuffersRealActivation(Action<string> runClient, string appId, Guid clsid, string shortcut)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        using var manager = new AppNotificationManager();
        manager.Register(appId, clsid, shortcut);
        // The activation arrives while the process has no subscriber yet: the situation of a cold start before the UI exists.
        runClient("SDK format callback before subscription");
        Check(SpinWait.SpinUntil(() =>
        {
            return manager.PendingActivationCount == 1;
        }, TimeSpan.FromSeconds(5)), "Real COM activation was lost before any subscriber existed");
        int subscriber = Environment.CurrentManagedThreadId;
        int onThread = -1;
        string? action = null;
        string? reply = null;
        manager.NotificationInvoked += (_, args) =>
        {
            onThread = Environment.CurrentManagedThreadId;
            action = args.Arguments["action"];
            reply = args.UserInput["reply"];
        };
        Check(onThread == subscriber && action == "reply" && reply == "Hello & + \u4E16\u754C \U0001F600", "Real buffered activation not replayed intact on the subscribing thread");
        Check(manager.PendingActivationCount == 0, "Pending activation count not cleared");
    }

    // ----- Windows: adoption of hand-made registrations -------------------------------------------------

    private static string RegistrationPath(Guid clsid)
    {
        return $@"Software\Classes\CLSID\{{{clsid:D}}}";
    }

    private static string IdentityPath(string appId)
    {
        return $@"Software\Classes\AppUserModelId\{appId}";
    }

    private static void CreateLegacy(string shortcutName, Guid clsid, string appId, byte[] shortcutBytes, bool shortcut, bool identity, bool clsidKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string shortcutPath = ToastRegistration.GetShortcutPath(shortcutName);
        if (shortcut)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllBytes(shortcutPath, shortcutBytes);
        }

        if (identity)
        {
            using var key = Registry.CurrentUser.CreateSubKey(IdentityPath(appId));
            key.SetValue("DisplayName", "Legacy Hand Made");
            key.SetValue("CustomActivator", "{" + clsid.ToString("D") + "}");
            key.SetValue("LegacyOnly", "keep-me-not");
        }

        if (clsidKey)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrationPath(clsid) + @"\LocalServer32");
            key.SetValue("", @"C:\Legacy\Old.exe");
        }
    }

    private static void RemoveEverything(string shortcutName, Guid clsid, string appId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string shortcutPath = ToastRegistration.GetShortcutPath(shortcutName);
        File.Delete(shortcutPath);
        foreach (string backup in Directory.EnumerateFiles(Path.GetDirectoryName(shortcutPath)!, Path.GetFileName(shortcutPath) + ".replaced-*.bak"))
        {
            File.Delete(backup);
        }

        Registry.CurrentUser.DeleteSubKeyTree(RegistrationPath(clsid), throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(IdentityPath(appId), throwOnMissingSubKey: false);
    }

    internal static void TestAdoption()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string shortcutPath = ToastRegistration.GetShortcutPath(AdoptShortcut);
        byte[] legacyBytes = System.Text.Encoding.UTF8.GetBytes("legacy hand-made shortcut");
        string exe = Environment.ProcessPath!;
        string iconPath = Path.Combine(Path.GetTempPath(), "toastaot-icon-" + Guid.NewGuid().ToString("N") + ".png");
        RemoveEverything(AdoptShortcut, AdoptClsid, AdoptApp);
        RemoveEverything("ToastAOT Adoption Other", OtherClsid, OtherApp);
        try
        {
            File.WriteAllBytes(iconPath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII="));
            var iconUri = new Uri(iconPath);

            // 1. Default strictness: each kind of foreign leftover is refused and left untouched.
            CreateLegacy(AdoptShortcut, AdoptClsid, AdoptApp, legacyBytes, shortcut: true, identity: false, clsidKey: false);
            Reject<InvalidOperationException>(() =>
            {
                _ = ToastRegistration.Install(AdoptApp, AdoptClsid, AdoptShortcut, exe);
            });
            Check(File.ReadAllBytes(shortcutPath).SequenceEqual(legacyBytes), "Strict install changed a foreign shortcut");
            File.Delete(shortcutPath);

            CreateLegacy(AdoptShortcut, AdoptClsid, AdoptApp, legacyBytes, shortcut: true, identity: true, clsidKey: true);
            Reject<InvalidOperationException>(() =>
            {
                _ = ToastRegistration.Install(AdoptApp, AdoptClsid, AdoptShortcut, exe);
            });
            using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
            {
                Check(identity?.GetValue("DisplayName") as string == "Legacy Hand Made" && identity.GetValue("ToastAOT.Owner") == null, "Strict install changed a foreign AUMID");
            }

            // 2. Adoption with an injected failure rolls back to the exact legacy state.
            Reject<InvalidOperationException>(() =>
            {
                _ = ToastRegistration.Install(AdoptApp, AdoptClsid, AdoptShortcut, exe, registerComServer: () =>
                {
                    throw new InvalidOperationException("Injected failure during adoption");
                }, adoptExisting: true);
            });
            Check(File.ReadAllBytes(shortcutPath).SequenceEqual(legacyBytes), "Failed adoption changed the legacy shortcut");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(shortcutPath)!, Path.GetFileName(shortcutPath) + ".replaced-*.bak").Any(), "Failed adoption leaked a backup");
            using (var server = Registry.CurrentUser.OpenSubKey(RegistrationPath(AdoptClsid) + @"\LocalServer32"))
            {
                Check(server?.GetValue(null) as string == @"C:\Legacy\Old.exe", "Failed adoption did not restore the legacy command");
            }

            using (var clsidKey = Registry.CurrentUser.OpenSubKey(RegistrationPath(AdoptClsid)))
            using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
            {
                Check(clsidKey?.GetValue("ToastAOT.AppId") == null && identity?.GetValue("ToastAOT.Owner") == null && identity?.GetValue("CustomActivator") as string == "{" + AdoptClsid.ToString("D") + "}", "Failed adoption left markers behind");
            }

            // 3. Another ToastAOT identity's registrations are never adoptable.
            _ = ToastRegistration.Install(OtherApp, OtherClsid, "ToastAOT Adoption Other", exe);
            Reject<InvalidOperationException>(() =>
            {
                _ = ToastRegistration.Install(OtherApp, Guid.NewGuid(), "ToastAOT Adoption Other 2", exe, adoptExisting: true);
            });
            Reject<InvalidOperationException>(() =>
            {
                _ = ToastRegistration.Install(AdoptApp + ".Two", Guid.NewGuid(), "ToastAOT Adoption Other", exe, adoptExisting: true);
            });
            Reject<InvalidOperationException>(() =>
            {
                _ = ToastRegistration.Install(OtherApp, AdoptClsid, "ToastAOT Adoption Other 3", exe, adoptExisting: true);
            });
            Check(!File.Exists(ToastRegistration.GetShortcutPath("ToastAOT Adoption Other 2")), "Refused adoption created a shortcut");

            // 4. Opt-in adoption through the public manager API replaces the legacy entries and keeps a backup.
            using (var manager = new AppNotificationManager())
            {
                Reject<InvalidOperationException>(() =>
                {
                    manager.Register(AdoptApp, AdoptClsid, AdoptShortcut);
                });
                Check(manager.ReplacedShortcutBackupPath == null, "Backup path set without adoption");
                Reject<ArgumentException>(() =>
                {
                    manager.RegisterAdoptingExisting(AdoptApp, AdoptClsid, AdoptShortcut,
                        iconUri: new Uri("https://example.com/icon.png"));
                });
                Check(File.ReadAllBytes(shortcutPath).SequenceEqual(legacyBytes), "Invalid icon changed the legacy shortcut");
                manager.RegisterAdoptingExisting(AdoptApp, AdoptClsid, AdoptShortcut, iconUri: iconUri);
                string? backup = manager.ReplacedShortcutBackupPath;
                Check(backup != null && File.ReadAllBytes(backup).SequenceEqual(legacyBytes), "Adoption did not preserve the replaced shortcut");
                Check(!File.ReadAllBytes(shortcutPath).SequenceEqual(legacyBytes), "Adoption did not replace the shortcut");
                using (var server = Registry.CurrentUser.OpenSubKey(RegistrationPath(AdoptClsid) + @"\LocalServer32"))
                {
                    Check(server?.GetValue(null) as string == "\"" + exe + "\" " + ToastHost.ComActivationArgument, "Adoption did not install the launch command");
                }

                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
                {
                    Check(identity?.GetValue("ToastAOT.Owner") as string == AdoptClsid.ToString("B") && identity.GetValue("DisplayName") as string == AdoptShortcut, "Adoption did not claim the AUMID");
                    Check(identity?.GetValue("IconUri") as string == iconPath, "Adoption did not register the explicit identity icon");
                }

                manager.Unregister();
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp), writable: true))
                {
                    identity!.DeleteValue("IconUri");
                }

                // Once adopted, the default strict path accepts it, and no further backup is made.
                manager.Register(AdoptApp, AdoptClsid, AdoptShortcut, iconUri: iconUri);
                Check(manager.ReplacedShortcutBackupPath == null, "Second registration made another backup");
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
                {
                    Check(identity?.GetValue("IconUri") as string == iconPath, "Normal explicit registration did not register the icon");
                }
                Reject<InvalidOperationException>(() =>
                {
                    manager.Register(AdoptApp, AdoptClsid, AdoptShortcut);
                });
                manager.Unregister();

                // Clearing stale metadata participates in rollback, including the original registry value kind.
                const string previousIcon = @"%TEMP%\previous-icon.png";
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp), writable: true))
                {
                    identity!.SetValue("IconUri", previousIcon, RegistryValueKind.ExpandString);
                }
                Reject<InvalidOperationException>(() =>
                {
                    _ = ToastRegistration.Install(AdoptApp, AdoptClsid, AdoptShortcut, exe, registerComServer: () =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException();
                        }

                        using var duringInstall = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp));
                        Check(duringInstall?.GetValue("IconUri") == null, "Stale icon was not cleared before COM registration");
                        throw new InvalidOperationException("Injected failure after clearing the icon");
                    });
                });
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
                {
                    Check(identity?.GetValue("IconUri", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string == previousIcon &&
                        identity.GetValueKind("IconUri") == RegistryValueKind.ExpandString,
                        "Failed registration did not restore the previous icon value and kind");
                }

                manager.Register(AdoptApp, AdoptClsid, AdoptShortcut);
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
                {
                    Check(identity?.GetValue("IconUri") == null, "Register without an icon retained stale metadata");
                }
                manager.Unregister();

                // A file deleted since the last registration must not disable notifications or retain its old path.
                manager.Register(AdoptApp, AdoptClsid, AdoptShortcut, iconUri: iconUri);
                manager.Unregister();
                File.Delete(iconPath);
                manager.RegisterAdoptingExisting(AdoptApp, AdoptClsid, AdoptShortcut, iconUri: iconUri);
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
                {
                    Check(identity?.GetValue("IconUri") == null, "Missing icon file retained stale metadata");
                    Check(identity?.GetValue("CustomActivator") as string == AdoptClsid.ToString("B"),
                        "Missing icon file prevented activator registration");
                }
                manager.UnregisterAll();

                manager.Register(AdoptApp, AdoptClsid, AdoptShortcut, iconUri: iconUri);
                using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(AdoptApp)))
                {
                    Check(identity != null && identity.GetValue("IconUri") == null,
                        "Fresh registration with a missing icon did not succeed without icon metadata");
                }
                manager.UnregisterAll();
            }

            Check(!File.Exists(shortcutPath), "Uninstall after adoption left the shortcut");
            using (var gone = Registry.CurrentUser.OpenSubKey(RegistrationPath(AdoptClsid)))
            {
                Check(gone == null, "Uninstall after adoption left the CLSID");
            }
        }
        finally
        {
            File.Delete(iconPath);
            ToastRegistration.Uninstall(OtherApp, OtherClsid);
            RemoveEverything(AdoptShortcut, AdoptClsid, AdoptApp);
            RemoveEverything("ToastAOT Adoption Other", OtherClsid, OtherApp);
            RemoveEverything("ToastAOT Adoption Other 2", Guid.NewGuid(), AdoptApp + ".none");
        }
    }

    // ----- Windows: install, upgrade, repeated install/uninstall and unrelated-state safety ------------

    internal static void TestManagerUnregisterAllSerialization()
    {
        const string appId = "ToastAOT.Tests.ManagerLifecycle";
        const string shortcut = "ToastAOT Manager Lifecycle Tests";
        Guid clsid = new("913C671E-B0BD-488C-A155-CE17804820A6");
        using var manager = new AppNotificationManager();
        ToastRegistration.Uninstall(appId, clsid);
        int initialNativeCount = ToastComActivator.LiveInstances;
        manager.Register(appId, clsid, shortcut);
        using var registrationMutex = new Mutex(false, @"Local\ToastAOT.Registration");
        if (!registrationMutex.WaitOne(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Could not hold the registration mutex for the lifecycle test.");
        }

        Task? removal = null;
        Task<bool>? replacement = null;
        try
        {
            removal = Task.Run(manager.UnregisterAll);
            Check(SpinWait.SpinUntil(() =>
            {
                return ToastComActivator.LiveInstances == initialNativeCount;
            }, TimeSpan.FromSeconds(5)), "UnregisterAll did not dispose the process COM factory");
            Check(!removal.IsCompleted, "UnregisterAll did not wait for the persistent registration lock");
            replacement = Task.Run(() =>
            {
                try
                {
                    manager.Register(appId, clsid, shortcut);
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            });
            Check(replacement.Wait(TimeSpan.FromSeconds(3)) && replacement.Result,
                "Register must reject replacement until UnregisterAll finishes persistent removal");
        }
        finally
        {
            registrationMutex.ReleaseMutex();
            removal?.GetAwaiter().GetResult();
            replacement?.GetAwaiter().GetResult();
            manager.UnregisterAll();
        }

        // The exclusion flag must be released after successful removal so the same manager remains reusable.
        manager.Register(appId, clsid, shortcut);
        manager.UnregisterAll();
    }

    internal static void TestUpgradeAndUninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string root = Path.Combine(Path.GetTempPath(), "toastaot-upgrade-" + Guid.NewGuid().ToString("N"));
        string v1 = Path.Combine(root, "v1", "App.exe");
        string v2 = Path.Combine(root, "v2 with spaces", "App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(v1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(v2)!);
        File.Copy(Environment.ProcessPath!, v1);
        File.Copy(Environment.ProcessPath!, v2);
        string shortcut = ToastRegistration.GetShortcutPath(UpgradeShortcut);
        Registry.CurrentUser.DeleteSubKeyTree(IdentityPath(SentinelApp), throwOnMissingSubKey: false);
        using (var sentinel = Registry.CurrentUser.CreateSubKey(IdentityPath(SentinelApp)))
        {
            sentinel.SetValue("DisplayName", "Unrelated app that must survive");
        }

        try
        {
            ToastRegistration.Uninstall(UpgradeApp, UpgradeClsid);
            string Command()
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistrationPath(UpgradeClsid) + @"\LocalServer32");
                return (string)key!.GetValue(null)!;
            }

            _ = ToastRegistration.Install(UpgradeApp, UpgradeClsid, UpgradeShortcut, v1, "Upgrade Test 1.0");
            Check(Command() == "\"" + v1 + "\" " + ToastHost.ComActivationArgument && File.Exists(shortcut), "Initial install incomplete");

            // Upgrade: same identity, new location and display name.
            _ = ToastRegistration.Install(UpgradeApp, UpgradeClsid, UpgradeShortcut, v2, "Upgrade Test 2.0");
            Check(Command() == "\"" + v2 + "\" " + ToastHost.ComActivationArgument, "Upgrade did not repoint the COM server (quoting with spaces)");
            using (var identity = Registry.CurrentUser.OpenSubKey(IdentityPath(UpgradeApp)))
            {
                Check(identity?.GetValue("DisplayName") as string == "Upgrade Test 2.0" && identity.GetValue("ToastAOT.Owner") as string == UpgradeClsid.ToString("B"), "Upgrade did not refresh AUMID metadata");
            }

            Check(File.Exists(shortcut), "Upgrade lost the shortcut");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(shortcut)!, Path.GetFileName(shortcut) + ".*.tmp").Any(), "Upgrade leaked a temporary shortcut");

            // Downgrade then reinstall again is idempotent.
            _ = ToastRegistration.Install(UpgradeApp, UpgradeClsid, UpgradeShortcut, v1, "Upgrade Test 1.0");
            _ = ToastRegistration.Install(UpgradeApp, UpgradeClsid, UpgradeShortcut, v1, "Upgrade Test 1.0");
            Check(Command().Contains(v1, StringComparison.Ordinal), "Repeated install changed the command");

            // Uninstall must remove only what the library created.
            ToastRegistration.Uninstall(UpgradeApp, UpgradeClsid);
            Check(!File.Exists(shortcut), "Uninstall left the shortcut");
            using (var removed = Registry.CurrentUser.OpenSubKey(RegistrationPath(UpgradeClsid)))
            using (var removedIdentity = Registry.CurrentUser.OpenSubKey(IdentityPath(UpgradeApp)))
            {
                Check(removed == null && removedIdentity == null, "Uninstall left registry keys");
            }

            ToastRegistration.Uninstall(UpgradeApp, UpgradeClsid);

            // Repeated install/uninstall cycles never accumulate state or fail.
            for (int i = 0; i < 25; ++i)
            {
                _ = ToastRegistration.Install(UpgradeApp, UpgradeClsid, UpgradeShortcut, i % 2 == 0 ? v1 : v2);
                ToastRegistration.Uninstall(UpgradeApp, UpgradeClsid);
            }

            Check(!File.Exists(shortcut), "Repeated cycles left the shortcut");
            using (var removed = Registry.CurrentUser.OpenSubKey(RegistrationPath(UpgradeClsid)))
            using (var removedIdentity = Registry.CurrentUser.OpenSubKey(IdentityPath(UpgradeApp)))
            {
                Check(removed == null && removedIdentity == null, "Repeated cycles left registry keys");
            }

            // Deleting the install folder (a user removing the app by hand) must not block a later clean uninstall.
            _ = ToastRegistration.Install(UpgradeApp, UpgradeClsid, UpgradeShortcut, v2);
            Directory.Delete(Path.GetDirectoryName(v2)!, recursive: true);
            ToastRegistration.Uninstall(UpgradeApp, UpgradeClsid);
            Check(!File.Exists(shortcut), "Uninstall with the executable already deleted left the shortcut");

            using var survivor = Registry.CurrentUser.OpenSubKey(IdentityPath(SentinelApp));
            Check(survivor?.GetValue("DisplayName") as string == "Unrelated app that must survive", "Install/uninstall disturbed an unrelated AUMID");
        }
        finally
        {
            ToastRegistration.Uninstall(UpgradeApp, UpgradeClsid);
            Registry.CurrentUser.DeleteSubKeyTree(IdentityPath(SentinelApp), throwOnMissingSubKey: false);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ----- Windows: crash recovery -----------------------------------------------------------------------

    /// <summary>Child entry point: register, signal readiness and wait to be killed.</summary>
    internal static void HoldRegistration(string readyFile)
    {
        using var host = ToastHost.Register(CrashApp, CrashClsid, CrashShortcut);
        File.WriteAllText(readyFile, "ready");
        Thread.Sleep(Timeout.Infinite);
    }

    /// <summary>Child entry point: hold the machine-wide registration lock and wait to be killed.</summary>
    internal static void HoldLock(string readyFile)
    {
        using var mutex = new Mutex(false, @"Local\ToastAOT.Registration");
        _ = mutex.WaitOne();
        File.WriteAllText(readyFile, "ready");
        Thread.Sleep(Timeout.Infinite);
    }

    private static Process StartChild(string argument, string readyFile)
    {
        File.Delete(readyFile);
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument + " \"" + readyFile + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start child.");
        if (!SpinWait.SpinUntil(() =>
        {
            return File.Exists(readyFile) || child.HasExited;
        }, TimeSpan.FromSeconds(30)) || child.HasExited)
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("Child did not become ready: " + argument);
        }

        return child;
    }

    internal static void TestCrashRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string ready = Path.Combine(Path.GetTempPath(), "toastaot-crash-" + Guid.NewGuid().ToString("N") + ".ready");
        string shortcut = ToastRegistration.GetShortcutPath(CrashShortcut);
        ToastRegistration.Uninstall(CrashApp, CrashClsid);
        try
        {
            // A process that dies while registered leaves a valid persistent registration, and the next launch recovers.
            using (var child = StartChild("--hold-registration", ready))
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistrationPath(CrashClsid) + @"\LocalServer32"))
                {
                    Check(key != null && File.Exists(shortcut), "Child registration not visible while running");
                }

                child.Kill(entireProcessTree: true);
                _ = child.WaitForExit(10000);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(RegistrationPath(CrashClsid) + @"\LocalServer32"))
            {
                Check(key?.GetValue(null) is string && File.Exists(shortcut), "Killed process lost its persistent registration");
            }

            using (var host = ToastHost.Register(CrashApp, CrashClsid, CrashShortcut))
            {
                Check(host.ReplacedShortcutBackupPath == null, "Recovery registration claimed a replaced shortcut");
            }

            // Interrupted publication: registry complete but shortcut missing. The next registration repairs it.
            File.Delete(shortcut);
            using (var host = ToastHost.Register(CrashApp, CrashClsid, CrashShortcut))
            {
                Check(File.Exists(shortcut), "Registration did not repair a missing shortcut");
            }

            // A leftover temporary shortcut from an interrupted install does not block registration.
            File.WriteAllText(shortcut + "." + Guid.NewGuid().ToString("N") + ".tmp", "partial");
            using (var host = ToastHost.Register(CrashApp, CrashClsid, CrashShortcut))
            {
                Check(File.Exists(shortcut), "Registration failed after an interrupted install");
            }

            foreach (string leftover in Directory.EnumerateFiles(Path.GetDirectoryName(shortcut)!, Path.GetFileName(shortcut) + ".*.tmp"))
            {
                File.Delete(leftover);
            }

            // A process killed while holding the registration lock must not wedge later installs (abandoned mutex).
            using (var child = StartChild("--hold-lock", ready))
            {
                var install = Task.Run(() =>
                {
                    _ = ToastRegistration.Install(CrashApp, CrashClsid, CrashShortcut, Environment.ProcessPath!);
                });
                Thread.Sleep(1000);
                Check(!install.IsCompleted, "Install did not wait for the registration lock");
                child.Kill(entireProcessTree: true);
                _ = child.WaitForExit(10000);
                Check(install.Wait(TimeSpan.FromSeconds(15)), "Install stayed blocked after the lock holder crashed");
            }
        }
        finally
        {
            ToastHost.UnregisterAll(CrashApp, CrashClsid);
            File.Delete(ready);
        }

        using var removed = Registry.CurrentUser.OpenSubKey(RegistrationPath(CrashClsid));
        Check(removed == null && !File.Exists(shortcut), "Crash-recovery cleanup left registration behind");
        Console.WriteLine($"PASS: {_checks} lifecycle/adoption/upgrade/crash-recovery checks");
    }
}
