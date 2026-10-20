using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ToastAOT.Tests;

internal static unsafe class WindowsTests
{
    private const string AppId = "ToastAOT.Tests.Isolated";
    private const string Shortcut = "ToastAOT Integration Tests";
    private static readonly Guid Clsid = new("D63627EE-CA80-4274-B8A8-C6F029FBB0C5");
    private static readonly string ResultFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToastAOT-integration-result.txt");
    private const string Arguments = "action=reply&unicode=%F0%9F%98%80%26%2B";
    private const string BodyArguments = "action=open&unicode=%F0%9F%98%80%26%2B";
    private static int _checks;

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new Exception(message);
        }

        ++_checks;
    }

    internal static void RunServer()
    {
        File.WriteAllText(ResultFile + ".startup", Environment.CommandLine);
        using var host = ToastHost.Register(AppId, Clsid, Shortcut);
        host.Activated += activation =>
        {
            Thread.Sleep(250); // The waiter must not return and exit before the handler completes.
            File.WriteAllText(ResultFile, ActivationResult(activation));
        };
        if (!host.WaitForActivation(TimeSpan.FromSeconds(30)))
        {
            Environment.ExitCode = 2;
        }
    }

    internal static void PrepareShellTest(bool warm)
    {
        File.Delete(ResultFile);
        using var host = ToastHost.Register(AppId, Clsid, Shortcut);
        host.Activated += activation =>
        {
            File.WriteAllText(ResultFile, ActivationResult(activation));
        };
        host.Show(new ToastBuilder().AddText("ToastAOT shell acceptance test")
            .AddText("Enter Hello & + 世界 😀 and choose Reply. This is test data.")
            .AddTextInput("reply", "Hello & + 世界 😀")
            .AddSelectionInput("choice", [("one", "One"), ("two", "Two")], "two")
            .AddButton("Reply", Arguments, hintInputId: "reply")
            .SetLaunchArguments(BodyArguments).SetIdentity("shell-test", "shell")
            .Build());
        Console.WriteLine(warm ? "Waiting up to 2 minutes for a real shell click." : "Process exiting; activate the notification from Notification Center.");
        if (warm && !host.WaitForActivation(TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException("No shell activation received.");
        }
    }

    internal static void VerifyShellTest(bool body = false)
    {
        try
        {
            Check(File.Exists(ResultFile) && File.ReadAllText(ResultFile) == (body ? BodyArguments : Arguments) + "|Hello & + 世界 😀|two", "Real shell callback not received or input mismatch.");
            Console.WriteLine("PASS: real shell activation and Unicode text input");
        }
        finally
        {
            CleanupShellTest();
        }
    }

    internal static void CleanupShellTest()
    {
        ToastHost.UnregisterAll(AppId, Clsid);
        File.Delete(ResultFile);
        File.Delete(ResultFile + ".startup");
    }

    internal static void Run(bool includeColdStart, bool includeNotifications = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        bool coldStartSkipped = false;
        using var mutex = new Mutex(false, "Local\\ToastAOT.IntegrationTests");
        if (!mutex.WaitOne(0))
        {
            throw new InvalidOperationException("Integration tests are already running.");
        }

        try
        {
            using (var host = ToastHost.Register(AppId, Clsid, Shortcut))
            {
                Check(File.Exists(ToastRegistration.GetShortcutPath(Shortcut)), "Shortcut missing");
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{Clsid:D}}}\LocalServer32");
                Check((key?.GetValue(null) as string) == "\"" + Environment.ProcessPath + "\" " + ToastHost.ComActivationArgument, "COM launch command mismatch");
                bool duplicateRejected = false;
                try
                {
                    using var duplicate = ToastHost.Register(AppId, Clsid, Shortcut);
                }
                catch (InvalidOperationException)
                {
                    duplicateRejected = true;
                }
                Check(duplicateRejected, "Duplicate in-process registration allowed");
                var conflictingClsid = Guid.NewGuid();
                bool identityRejected = false;
                try
                {
                    using var conflict = ToastHost.Register(AppId, conflictingClsid, "Conflicting integration registration");
                }
                catch (InvalidOperationException)
                {
                    identityRejected = true;
                }
                Check(identityRejected, "A second CLSID overwrote an existing AUMID");
                int invoked = 0;
                int failed = 0;
                host.ActivationHandlerFailed += _ =>
                {
                    ++failed;
                };
                host.Activated += _ =>
                {
                    throw new InvalidOperationException("Deliberate handler failure");
                };
                host.Activated += activation =>
                {
                    Check(activation.AppId == AppId && activation.RawArguments == Arguments, "Warm arguments corrupted");
                    Check(activation.Arguments["unicode"] == "😀&+" && activation.UserInput["reply"] == "Hello & + 世界 😀", "Unicode input corrupted");
                    Check(activation.UserInput["choice"] == "two", "Selection input missing");
                    Thread.Sleep(150);
                    ++invoked;
                };
                var delivery = Task.Run(() =>
                {
                    InvokeCom();
                });
                Check(host.WaitForActivation(TimeSpan.FromSeconds(10)), "Warm activation timed out");
                Check(invoked == 1 && failed == 1, "Wait returned before callback completion or handler failure escaped");
                delivery.GetAwaiter().GetResult();
                RunClient("Cross-process warm COM client", 15000);
                Check(invoked == 2 && failed == 2, "Cross-process warm callback not delivered");
                if (includeNotifications)
                {
                    Check(Enum.IsDefined(host.Setting), "Initial notification setting failed");
                    host.ClearHistory();
                    Check(host.GetAll().Count == 0, "History not cleared");
                    LifecycleTests.ShowWithoutTagOrGroup(host);
                    Console.WriteLine("  readback: " + LifecycleTests.ReadbackAndExpiry(host));
                    var progress = new ToastProgressData { Title = "Download", Status = "Working", Value = .25, SequenceNumber = 1 };
                    host.Show(new ToastBuilder().AddText("ToastAOT integration test").AddProgressBar(progress)
                        .SetIdentity("progress", "integration").SetSuppressPopup().SetPriority(ToastPriority.High).SetExpiration(DateTimeOffset.UtcNow.AddMinutes(2)).Build() with
                    {
                        ExpiresOnReboot = true
                    });
                    Check(Enum.IsDefined(host.Setting), "Unknown notification setting");
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return host.GetAll().Any(t => t.Tag == "progress");
                    }, TimeSpan.FromSeconds(10)), "Notification did not enter history");
                    var message = host.GetAll().Single(t => t.Tag == "progress");
                    Check(message.Group == "integration" && message.Xml.Contains("progressTitle") && message.Expiration.HasValue, "History properties or XML missing");
                    Check(message.Progress?.Value == .25 && message.Progress.Title == "Download", "History lost bound progress values");
                    uint nextSequence = checked(message.Progress!.SequenceNumber + 1);
                    Check(host.Update(progress with
                    {
                        Value = .75,
                        SequenceNumber = nextSequence
                    }, "progress", "integration") == ToastUpdateResult.Succeeded, "Progress update failed");
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return host.GetAll().Single().Progress?.Value == .75;
                    }, TimeSpan.FromSeconds(5)), "Updated value not persisted: " + host.GetAll().Single().Progress);
                    // Windows can return Succeeded for a stale update while ignoring its values.
                    _ = host.Update(progress, "progress", "integration");
                    Check(host.GetAll().Single().Progress?.Value == .75, "Stale sequence overwrote current progress");
                    host.RemoveByGroup("integration");
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return host.GetAll().Count == 0;
                    }, TimeSpan.FromSeconds(5)), "Group removal failed");
                    Check(host.Update(progress, "missing") == ToastUpdateResult.NotificationNotFound, "Missing notification result incorrect");
                    host.Show(new ToastBuilder().AddText("Tag removal test").SetIdentity("remove", "tests").SetSuppressPopup().Build());
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return host.GetAll().Count == 1;
                    }, TimeSpan.FromSeconds(5)), "Second toast not stored");
                    host.RemoveFromHistory("remove", "tests");
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return host.GetAll().Count == 0;
                    }, TimeSpan.FromSeconds(5)), "Tag removal failed");
                }
            }

            TestRegistrationRollback();
            if (includeNotifications)
            {
                using (var manager = new AppNotificationManager())
                {
                    int received = 0;
                    int handlerFailures = 0;
                    manager.ActivationHandlerFailed += _ =>
                    {
                        ++handlerFailures;
                    };
                    manager.NotificationInvoked += (_, _) =>
                    {
                        throw new InvalidOperationException("Deliberate facade handler failure");
                    };
                    manager.NotificationInvoked += (_, activation) =>
                    {
                        Check(activation.Arguments["unicode"] == "😀&+" && activation.UserInput["choice"] == "two", "Manager argument or selection delivery mismatch");
                        ++received;
                    };
                    manager.Register(AppId, Clsid, Shortcut);
                    LifecycleTests.BuilderNotificationWithoutTagOrGroup(manager);
                    RunClient("SDK format callback client", 15000, sdkFormat: true);
                    Check(manager.WaitForActivation(TimeSpan.FromSeconds(5)) && received == 1 && handlerFailures == 1, "Manager activation event or exception isolation failed");
                    var notification = new ToastAOT.Builder.AppNotificationBuilder().AddText("Familiar API test")
                        .AddProgressBar(new()).SetTag("facade").SetGroup("api").BuildNotification();
                    notification.SuppressDisplay = true;
                    notification.Progress = new(1)
                    {
                        Value = .1,
                        Status = "Working"
                    };
                    manager.Show(notification);
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return manager.GetAllAsync().GetAwaiter().GetResult().Count == 1;
                    }, TimeSpan.FromSeconds(5)), "Manager Show/GetAll failed");
                    uint sequence = checked(manager.GetAllAsync().GetAwaiter().GetResult().Single().Progress!.SequenceNumber + 1);
                    Check(manager.UpdateAsync(new(sequence)
                    {
                        Value = .8,
                        Status = "Nearly done"
                    }, "facade", "api").GetAwaiter().GetResult() == AppNotificationProgressResult.Succeeded, "Manager update failed");
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return manager.GetAllAsync().GetAwaiter().GetResult().Single().Progress?.Value == .8;
                    }, TimeSpan.FromSeconds(5)), "Manager updated progress not persisted");
                    manager.RemoveByTagAsync("facade").GetAwaiter().GetResult();
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return manager.GetAllAsync().GetAwaiter().GetResult().Count == 0;
                    }, TimeSpan.FromSeconds(5)), "Manager tag removal failed");
                }
            }

            LifecycleTests.TestManagerUnregisterAllSerialization();
            LifecycleTests.TestAdoption();
            LifecycleTests.TestUpgradeAndUninstall();
            LifecycleTests.TestCrashRecovery();
            if (includeNotifications)
            {
                LifecycleTests.ManagerBuffersRealActivation(label =>
                {
                    RunClient(label, 15000, sdkFormat: true);
                }, AppId, Clsid, Shortcut);
                StressTests.RunWindows(StressIterations("--stress-iterations=", 150), StressIterations("--stress-registrations=", 60),
                    StressIterations("--stress-activations=", 25), SoakMinutes(), AppId, Clsid, Shortcut, label =>
                    {
                        RunClient(label, 15000);
                    });
            }

            if (includeColdStart)
            {
                File.Delete(ResultFile);
                File.Delete(ResultFile + ".startup");
                using (var registration = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{Clsid:D}}}\LocalServer32"))
                {
                    Console.WriteLine("Cold-start target: " + registration?.GetValue(null));
                }

                using (var registration = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{Clsid:D}}}\LocalServer32"))
                {
                    Console.WriteLine("HKCR target: " + registration?.GetValue(null));
                }
                // There is now no registered factory in this process. SCM must launch the
                // actual apphost/native EXE using LocalServer32 and marshal the callback.
                var (coldExit, coldError) = RunClientCapture(45000, sdkFormat: false);
                if (coldExit != 0 && ColdStartBlockedByHost(coldError, out string reason))
                {
                    coldStartSkipped = true;
                    Console.WriteLine("SKIPPED (environmental, NOT a pass): cold COM start. " + reason);
                }
                else
                {
                    Check(coldExit == 0, "Fresh cold-start COM client failed: " + coldError);
                    Check(SpinWait.SpinUntil(() =>
                    {
                        return File.Exists(ResultFile);
                    }, TimeSpan.FromSeconds(10)), "Cold-start handler did not write result");
                    Check(File.ReadAllText(ResultFile) == Arguments + "|Hello & + 世界 😀|two", "Cold-start argument or user input mismatch");
                }
            }
        }
        finally
        {
            if (includeNotifications)
            {
                ToastHost.UnregisterAll(AppId, Clsid);
            }
            else
            {
                ToastRegistration.Uninstall(AppId, Clsid);
            }

            File.Delete(ResultFile);
            File.Delete(ResultFile + ".startup");
            mutex.ReleaseMutex();
        }
        Check(!File.Exists(ToastRegistration.GetShortcutPath(Shortcut)), "Uninstall left shortcut behind");
        using var removed = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{Clsid:D}}}");
        Check(removed == null, "Uninstall left COM registration behind");
        using var identity = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        Check(identity == null, "Uninstall left AUMID registration behind");
        Console.WriteLine($"PASS: {_checks} Windows registration/{(includeNotifications ? "WinRT" : "WinRT excluded")}/warm COM/{(coldStartSkipped ? "cold COM SKIPPED (environmental; rerun from a normal desktop launch)" : includeColdStart ? "cold COM" : "cold COM excluded")} integration checks ({RuntimeInformation.ProcessArchitecture})");
    }

    private static void TestRegistrationRollback()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        string shortcut = ToastRegistration.GetShortcutPath(Shortcut);
        var originalAttributes = File.GetAttributes(shortcut);
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{Clsid:D}}}\LocalServer32", writable: true)!;
        string originalCommand = (string)key.GetValue(null)!;
        // An identical shortcut is deliberately not rewritten, so make it stale to force a publication attempt.
        File.WriteAllBytes(shortcut, "stale shortcut"u8.ToArray());
        byte[] originalShortcut = File.ReadAllBytes(shortcut);
        try
        {
            key.SetValue(null, "rollback-test-sentinel");
            File.SetAttributes(shortcut, originalAttributes | FileAttributes.ReadOnly);
            bool failed = false;
            try
            {
                using var attempted = ToastHost.Register(AppId, Clsid, Shortcut);
            }
            catch (UnauthorizedAccessException)
            {
                failed = true;
            }
            catch (IOException)
            {
                failed = true;
            }
            Check(failed, "Could not exercise shortcut publication failure");
            Check((string?)key.GetValue(null) == "rollback-test-sentinel", "Failed install did not restore registry command");
            Check(File.ReadAllBytes(shortcut).SequenceEqual(originalShortcut), "Failed install changed the original shortcut");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(shortcut)!, Path.GetFileName(shortcut) + ".*.tmp").Any(), "Failed install leaked a temporary shortcut");
        }
        finally
        {
            File.SetAttributes(shortcut, originalAttributes);
            key.SetValue(null, originalCommand);
        }
        bool serverFailure = false;
        try
        {
            _ = ToastRegistration.Install(AppId, Clsid, Shortcut, Environment.ProcessPath!,
                registerComServer: () =>
                {
                    throw new InvalidOperationException("Injected COM startup failure");
                });
        }
        catch (InvalidOperationException)
        {
            serverFailure = true;
        }
        Check(serverFailure && (string?)key.GetValue(null) == originalCommand, "COM startup failure did not restore registration");
        Check(File.ReadAllBytes(shortcut).SequenceEqual(originalShortcut), "COM startup failure replaced the original shortcut");

        Guid failedClsid = Guid.NewGuid();
        string failedApp = AppId + ".Failed";
        string failedShortcut = Shortcut + " " + failedClsid.ToString("N");
        try
        {
            _ = ToastRegistration.Install(failedApp, failedClsid, failedShortcut, Environment.ProcessPath!,
                registerComServer: () =>
                {
                    throw new InvalidOperationException("Injected first registration failure");
                });
        }
        catch (InvalidOperationException)
        {
        }
        using var failedClass = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{failedClsid:D}}}");
        using var failedIdentity = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{failedApp}");
        Check(failedClass == null && failedIdentity == null && !File.Exists(ToastRegistration.GetShortcutPath(failedShortcut)), "Failed first registration left persistent data behind");

        bool unauthorizedCleanup = false;

        bool cleanupCalled = false;
        try
        {
            ToastRegistration.Uninstall(AppId, Guid.NewGuid(), () =>
            {
                cleanupCalled = true;
            });
        }
        catch (InvalidOperationException)
        {
            unauthorizedCleanup = true;
        }
        Check(unauthorizedCleanup && !cleanupCalled, "Foreign AUMID cleanup reached notification history");

        _ = ToastRegistration.Install(failedApp, failedClsid, failedShortcut, Environment.ProcessPath!);
        bool cleanupFailed = false;
        try
        {
            ToastRegistration.Uninstall(failedApp, failedClsid, () =>
            {
                throw new IOException("Injected history failure");
            });
        }
        catch (IOException)
        {
            cleanupFailed = true;
        }
        Check(cleanupFailed && File.Exists(ToastRegistration.GetShortcutPath(failedShortcut)), "History failure removed registration prematurely");
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{failedClsid:D}}}");
        ToastRegistration.Uninstall(failedApp, failedClsid);
        using var recoveredIdentity = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{failedApp}");
        Check(recoveredIdentity == null && !File.Exists(ToastRegistration.GetShortcutPath(failedShortcut)), "Cleanup did not recover an identity-only partial registration");
    }

    private static string ActivationResult(ToastActivation activation)
    {
        return activation.RawArguments + "|" +
        activation.UserInput.GetValueOrDefault("reply", "") + "|" + activation.UserInput.GetValueOrDefault("choice", "");
    }

    private static int StressIterations(string prefix, int fallback)
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(argument.AsSpan(prefix.Length), out int value) && value >= 0)
            {
                return value;
            }
        }

        return fallback;
    }

    private static double SoakMinutes()
    {
        const string prefix = "--soak-minutes=";
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal) &&
                double.TryParse(argument.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>
    /// A 0x80040154 cold start is an environmental skip only when a probe proves that SCM cannot see this process's
    /// HKCU class writes (package registry virtualisation, for example an MSIX host).
    /// Set TOASTAOT_REQUIRE_COLD_START=1 to make it fail anyway.
    /// </summary>
    private static bool ColdStartBlockedByHost(string clientError, out string reason)
    {
        reason = "";
        if (!clientError.Contains("0x80040154", StringComparison.Ordinal) ||
            Environment.GetEnvironmentVariable("TOASTAOT_REQUIRE_COLD_START") == "1")
        {
            return false;
        }

        if (ColdStartProbe.IsClassRegistrationVisibleToServiceControl(out int probe))
        {
            return false;
        }

        reason = $"The COM service control manager cannot see this process's HKCU class registry writes (control probe HRESULT 0x{probe:X8}); " +
            "the process is running under package registry virtualisation, typically because it was launched from a packaged (MSIX) host. Re-run from a normal desktop launch (explorer.exe, Start menu, Task Scheduler). See doc/LIFECYCLE.md.";
        return true;
    }

    private static (int ExitCode, string Error) RunClientCapture(int timeoutMilliseconds, bool sdkFormat)
    {
        using var client = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, sdkFormat ? "--invoke-sdk-com" : "--invoke-com")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        })
            ?? throw new InvalidOperationException("Could not start COM test client.");
        Task<string> error = client.StandardError.ReadToEndAsync();
        if (!client.WaitForExit(timeoutMilliseconds))
        {
            client.Kill(entireProcessTree: true);
            _ = client.WaitForExit(5000);
            throw new TimeoutException("Cold-start client timed out.");
        }

        return (client.ExitCode, error.GetAwaiter().GetResult());
    }

    private static void RunClient(string label, int timeoutMilliseconds, bool sdkFormat = false)
    {
        using var client = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, sdkFormat ? "--invoke-sdk-com" : "--invoke-com")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        })
            ?? throw new InvalidOperationException("Could not start COM test client.");
        Task<string> error = client.StandardError.ReadToEndAsync();
        if (!client.WaitForExit(timeoutMilliseconds))
        {
            client.Kill(entireProcessTree: true);
            _ = client.WaitForExit(5000);
            throw new TimeoutException(label + " timed out.");
        }
        Check(client.ExitCode == 0, label + " failed: " + error.GetAwaiter().GetResult());
    }

    internal static void InvokeCom(bool sdkFormat = false, Guid? targetClsid = null, string? targetAppId = null)
    {
        int hr = Interop.CoInitializeEx(0, 0);
        Interop.Check(hr);
        void* callback = null;
        try
        {
            var clsid = targetClsid ?? Clsid;
            var iid = Interop.IID_INotificationActivationCallback;
            int result = Interop.CoCreateInstance(&clsid, 0, Interop.CLSCTX_LOCAL_SERVER, &iid, out callback);
            if (result < 0)
            {
                var unknown = Interop.IID_IUnknown;
                void* probe = null;
                int probeResult = Interop.CoCreateInstance(&clsid, 0, Interop.CLSCTX_LOCAL_SERVER, &unknown, out probe);
                Interop.Release(probe);
                Console.Error.WriteLine($"Activation HRESULT=0x{result:X8}; IUnknown HRESULT=0x{probeResult:X8}");
            }
            Interop.Check(result);
            string rawArguments = sdkFormat ? "action=reply;unicode=😀&+" : Arguments;
            string appId = targetAppId ?? AppId;
            fixed (char* app = appId, arguments = rawArguments, inputKey = "reply", inputValue = "Hello & + 世界 😀", choiceKey = "choice", choiceValue = "two")
            {
                UserInput* input = stackalloc UserInput[2];
                input[0] = new()
                {
                    Key = inputKey,
                    Value = inputValue
                };
                input[1] = new()
                {
                    Key = choiceKey,
                    Value = choiceValue
                };
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, char*, char*, UserInput*, uint, int>)Interop.VTableEntry(callback, 3))
                    (callback, app, arguments, input, 2));
            }
        }
        finally
        {
            Interop.Release(callback);
            Interop.CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInput
    {
        internal char* Key;
        internal char* Value;
    }
}
