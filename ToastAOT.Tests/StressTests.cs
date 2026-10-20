using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToastAOT.Tests;

/// <summary>
/// Bounded leak and stability checks for the native COM objects, process handles and the WinRT call path.
/// These establish absence of growth over the iterations below; they are not a substitute for a multi-hour soak,
/// which <c>--soak-minutes=N</c> allows the owner to run.
/// </summary>
internal static unsafe class StressTests
{
    private static int _checks;

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new Exception(message);
        }

        ++_checks;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private readonly record struct Snapshot(int Handles, int Threads, long PrivateBytes, int LiveNative)
    {
        internal static Snapshot Take()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            return new Snapshot(process.HandleCount, process.Threads.Count, process.PrivateMemorySize64, ToastComActivator.LiveInstances);
        }

        public override string ToString()
        {
            return $"handles={Handles} threads={Threads} private={PrivateBytes / 1024} KiB liveNative={LiveNative}";
        }
    }

    private static void AssertNoGrowth(string label, Snapshot before, Snapshot after, int handleSlack, long privateSlackBytes)
    {
        Console.WriteLine($"  {label}: before [{before}] after [{after}]");
        Check(after.LiveNative == before.LiveNative, label + ": native COM objects leaked (" + (after.LiveNative - before.LiveNative) + ")");
        Check(after.Handles - before.Handles <= handleSlack, label + ": handle count grew by " + (after.Handles - before.Handles));
        Check(after.Threads - before.Threads <= 2, label + ": thread count grew by " + (after.Threads - before.Threads));
        Check(after.PrivateBytes - before.PrivateBytes <= privateSlackBytes, label + ": private memory grew by " + (after.PrivateBytes - before.PrivateBytes) / 1024 + " KiB");
    }

    /// <summary>Drives the native vtables directly: factory, callback creation, Activate with inputs, release.</summary>
    internal static void RunNative(int iterations)
    {
        using var host = new ToastHost("stress", Guid.NewGuid(), false);
        int delivered = 0;
        host.Activated += activation =>
        {
            if (activation.UserInput["choice"] == "two")
            {
                ++delivered;
            }
        };
        void Cycle()
        {
            void* factory = ToastComActivator.CreateFactory(host);
            void* callback = null;
            try
            {
                var iid = Interop.IID_INotificationActivationCallback;
                var create = (delegate* unmanaged[Stdcall]<void*, void*, Guid*, void**, int>)Interop.VTableEntry(factory, 3);
                Require(create(factory, null, &iid, &callback) == 0, "Callback creation failed");
                var activate = (delegate* unmanaged[Stdcall]<void*, char*, char*, void*, uint, int>)Interop.VTableEntry(callback, 3);
                fixed (char* app = "stress", args = "action=reply;text=世界", key = "choice", value = "two")
                {
                    UserInputNative* input = stackalloc UserInputNative[1];
                    input[0] = new UserInputNative
                    {
                        Key = key,
                        Value = value
                    };
                    Require(activate(callback, app, args, input, 1) == 0, "Activate failed");
                }
            }
            finally
            {
                Interop.Release(callback);
                ToastComActivator.ReleaseFactory(factory);
            }
        }

        // Warm up for a full pass so JIT, tiering and GC heap growth are not mistaken for a leak.
        for (int i = 0; i < iterations; ++i)
        {
            Cycle();
        }

        var before = Snapshot.Take();
        for (int i = 0; i < iterations; ++i)
        {
            Cycle();
        }

        var after = Snapshot.Take();
        Check(delivered == 2 * iterations, "Not every native activation was delivered");
        Check(host.WaitForActivation(TimeSpan.Zero), "Callback objects still retained after the stress loop");
        AssertNoGrowth($"native vtable activation x{iterations}", before, after, handleSlack: 4, privateSlackBytes: 8 * 1024 * 1024);
        Console.WriteLine($"PASS: {_checks} native COM lifetime stress checks");
        _checks = 0;
    }

    /// <summary>
    /// .NET 11 preview runtimes leak about one handle per Process.Start (reproduced with a trivial program that spawns
    /// cmd.exe, no ToastAOT code involved), so the spawning test process is given that allowance on net11 only.
    /// </summary>
    private static int SpawnerHandleAllowance(int spawns)
    {
        return Environment.Version.Major >= 11 ? spawns + 4 : 0;
    }

    /// <summary>Real WinRT, registry and cross-process COM traffic with the shared test identity registered.</summary>
    internal static void RunWindows(int toastIterations, int registrationCycles, int crossProcessCalls, double soakMinutes, string appId, Guid clsid, string shortcut, Action<string> runClient)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        // Register / dispose repeatedly: server thread, class-object registration and shell link all released.
        for (int i = 0; i < 5; ++i)
        {
            using var warm = ToastHost.Register(appId, clsid, shortcut);
        }

        var before = Snapshot.Take();
        for (int i = 0; i < registrationCycles; ++i)
        {
            using var host = ToastHost.Register(appId, clsid, shortcut);
        }

        AssertNoGrowth($"register/dispose x{registrationCycles}", before, Snapshot.Take(), handleSlack: 8, privateSlackBytes: 24 * 1024 * 1024);

        using (var host = ToastHost.Register(appId, clsid, shortcut))
        {
            int calls = 0;
            host.Activated += _ =>
            {
                ++calls;
            };
            host.ClearHistory();
            void Toasts(int count)
            {
                for (int i = 0; i < count; ++i)
                {
                    string tag = "stress-" + (i % 8);
                    host.Show(new ToastBuilder().AddText("stress " + i).AddProgressBar(new ToastProgressData { Value = .5, Status = "s", SequenceNumber = 1 })
                        .SetIdentity(tag, "stress").SetSuppressPopup().Build());
                    var all = host.GetAll();
                    Check(all.Count <= 8, "History grew beyond the tag set");
                    if (i % 4 == 0)
                    {
                        host.RemoveFromHistory(tag, "stress");
                    }
                }

                host.RemoveByGroup("stress");
            }

            Toasts(60);
            before = Snapshot.Take();
            Toasts(toastIterations);
            AssertNoGrowth($"WinRT show/enumerate/remove x{toastIterations}", before, Snapshot.Take(), handleSlack: 20, privateSlackBytes: 32 * 1024 * 1024);

            before = Snapshot.Take();
            for (int i = 0; i < crossProcessCalls; ++i)
            {
                runClient("stress cross-process COM client");
            }

            Check(SpinWait.SpinUntil(() =>
            {
                return calls == crossProcessCalls;
            }, TimeSpan.FromSeconds(10)), "Cross-process activations lost: " + calls + " of " + crossProcessCalls);
            AssertNoGrowth($"cross-process COM activation x{crossProcessCalls}", before, Snapshot.Take(), handleSlack: 8 + SpawnerHandleAllowance(crossProcessCalls), privateSlackBytes: 24 * 1024 * 1024);

            if (soakMinutes > 0)
            {
                Console.WriteLine($"  soak: repeating for {soakMinutes} minutes");
                before = Snapshot.Take();
                var timer = Stopwatch.StartNew();
                int rounds = 0;
                int initialCalls = Volatile.Read(ref calls);
                TimeSpan nextCheckpoint = TimeSpan.FromMinutes(1);
                while (timer.Elapsed.TotalMinutes < soakMinutes)
                {
                    Toasts(25);
                    runClient("soak cross-process COM client");
                    ++rounds;
                    Check(Volatile.Read(ref calls) == initialCalls + rounds, "Soak activation delivery count mismatch at round " + rounds);

                    if (timer.Elapsed >= nextCheckpoint)
                    {
                        AssertNoGrowth($"soak checkpoint {timer.Elapsed.TotalMinutes:F1} min ({rounds} rounds)", before,
                            Snapshot.Take(), handleSlack: 24 + SpawnerHandleAllowance(rounds), privateSlackBytes: 64 * 1024 * 1024);
                        nextCheckpoint = timer.Elapsed + TimeSpan.FromMinutes(1);
                    }
                }

                AssertNoGrowth($"soak {soakMinutes} min ({rounds} rounds)", before, Snapshot.Take(), handleSlack: 24 + SpawnerHandleAllowance(rounds), privateSlackBytes: 64 * 1024 * 1024);
            }

            host.ClearHistory();
        }

        Console.WriteLine($"PASS: {_checks} Windows handle/COM/WinRT stress checks");
        _checks = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInputNative
    {
        internal char* Key;
        internal char* Value;
    }
}
