
namespace ToastAOT.Tests;

internal static unsafe class PortableTests
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

    internal static void Run()
    {
        foreach (string text in new[] { "", "&=+%", "世界 😀", "line\r\nline", "<xml>\"'", new string('a', 1024) })
        {
            var encoded = ToastBuilder.EncodeArguments(new Dictionary<string, string> { [text] = text });
            Check(ToastActivation.ParseArguments(encoded)[text] == text, "Argument round-trip failed");
        }
        Check(ToastActivation.ParseArguments("x=a=b&x=last&flag")["x"] == "last", "Duplicate argument semantics");
        Check(ToastActivation.ParseArguments("x=a+b")["x"] == "a b", "Form plus decoding");
        var inputs = new Dictionary<string, string> { ["x"] = "first" };
        var activation = new ToastActivation("app", "action=reply", inputs);
        inputs["x"] = "changed";
        Check(activation.UserInput["x"] == "first", "Activation did not snapshot user input");
        Reject<NotSupportedException>(() =>
        {
            ((IDictionary<string, string>)activation.UserInput).Add("y", "mutate");
        });
        Reject<ArgumentException>(() =>
        {
            ToastRegistration.ValidateIdentity("a b", Guid.NewGuid());
        });
        Reject<ArgumentException>(() =>
        {
            ToastRegistration.ValidateIdentity("app", Guid.Empty);
        });
        foreach (string name in new[] { "../escape", "a/b", "a\\b", "", "bad." })
        {
            Reject<ArgumentException>(() =>
            {
                _ = ToastRegistration.GetShortcutPath(name);
            });
        }

        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a").AddText("b").AddText("c").AddText("d").Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a").AddTextInput("x").AddTextInput("x").Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a").AddAction("reply", "reply", "missing").Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a").AddSelectionInput("x", []).Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a").AddSelectionInput("x", [("a", "A")], "b").Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a")
                .AddTextInput("reply")
                .AddSelectionInput("reply", [("one", "One")])
                .Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a")
                .AddSelectionInput("choice", [("one", "One"), ("one", "Duplicate")])
                .Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a")
                .AddSelectionInput("choice", [(" ", "Blank ID")])
                .Build();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a")
                .AddSelectionInput("choice", [("one", "One")])
                .AddAction("Reply", "reply", "choice")
                .Build();
        });
        var caseSensitiveInputs = new ToastBuilder().AddText("a")
            .AddTextInput("reply")
            .AddSelectionInput("Reply", [("one", "One"), ("ONE", "Uppercase")], "ONE")
            .AddAction("Reply", "reply", "reply")
            .Build();
        Check(caseSensitiveInputs.Xml.Contains("defaultInput=\"ONE\""), "Input and option IDs must remain ordinal and case-sensitive");
        Check(ToastBuilder.EncodeArguments([
            new KeyValuePair<string, string>("", ""),
            new KeyValuePair<string, string>("key", "a&b"),
            new KeyValuePair<string, string>("key", "+")]) == "=&key=a%26b&key=%2B",
            "Argument encoding must preserve empty values, pair order and duplicate keys");
        Reject<InvalidOperationException>(() =>
        {
            _ = new ToastBuilder().AddText("a").SetScenario((ToastScenario)99).Build();
        });
        Reject<ArgumentOutOfRangeException>(() =>
        {
            _ = new ToastBuilder().AddProgress("x", double.NaN, "s");
        });
        Reject<ArgumentOutOfRangeException>(() =>
        {
            new ToastProgressData
            {
                Value = double.PositiveInfinity
            }.Validate();
        });
        Reject<ArgumentException>(() =>
        {
            new ToastMessage("<wrong/>").Validate();
        });
        Reject<System.Xml.XmlException>(() =>
        {
            new ToastMessage("<!DOCTYPE toast [<!ENTITY x SYSTEM 'file:///C:/secret'>]><toast>&x;</toast>").Validate();
        });
        Reject<ArgumentException>(() =>
        {
            _ = new ToastBuilder().AddText(new string('x', 6000)).Build();
        });
        Reject<ArgumentException>(() =>
        {
            _ = new ToastBuilder().AddText("x").SetIdentity(new string('x', 65)).Build();
        });
        var progress = new ToastBuilder().AddText("progress").AddProgressBar(new()
        {
            Value = .2,
            SequenceNumber = 1
        }).Build();
        Check(progress.Xml.Contains("{progressValue}") && progress.Progress?.SequenceNumber == 1, "Bindable progress missing");

        using var host = new ToastHost("app", Guid.NewGuid(), false);
        host.DispatchActivation(activation);
        Check(!host.WaitForActivation(TimeSpan.Zero), "Buffered activation marked complete before delivery");
        int delivered = 0;
        host.Activated += a =>
        {
            Check(a == activation, "Queued activation changed");
            ++delivered;
        };
        Check(delivered == 1 && host.WaitForActivation(TimeSpan.Zero), "Queued activation lost");
        Reject<ArgumentException>(() =>
        {
            host.DispatchActivation(new("wrong", "", inputs));
        });
        Reject<ArgumentOutOfRangeException>(() =>
        {
            _ = host.WaitForActivation(TimeSpan.FromMilliseconds(-2));
        });
        using var waitingHost = new ToastHost("app", Guid.NewGuid(), false);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        waitingHost.Activated += _ =>
        {
            entered.Set();
            finish.Wait();
        };
        var task = Task.Run(() =>
        {
            waitingHost.DispatchActivation(activation);
        });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Handler never started");
        Check(!waitingHost.WaitForActivation(TimeSpan.FromMilliseconds(50)), "Wait completed while handler was running");
        finish.Set();
        Check(waitingHost.WaitForActivation(TimeSpan.FromSeconds(5)), "Completed activation not signaled");
        task.GetAwaiter().GetResult();
        using var queuedHost = new ToastHost("app", Guid.NewGuid(), false);
        for (int i = 0; i < 1024; ++i)
        {
            queuedHost.DispatchActivation(activation);
        }

        Reject<InvalidOperationException>(() =>
        {
            queuedHost.DispatchActivation(activation);
        });
        using var disposableWaiter = new ToastHost("app", Guid.NewGuid(), false);
        var disposedWait = Task.Run(() =>
        {
            try
            {
                _ = disposableWaiter.WaitForActivation(Timeout.InfiniteTimeSpan);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        });
        disposableWaiter.Dispose();
        Check(disposedWait.Wait(TimeSpan.FromSeconds(5)) && disposedWait.Result, "Dispose failed to wake an activation waiter");

        TestVtables(host);
        Check(host.WaitForActivation(TimeSpan.Zero), "Released callback kept the host waiter blocked");
        var rooted = CreateRootedHost();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(rooted.Host.IsAlive, "Factory did not root its host");
        ToastComActivator.ReleaseFactory((void*)rooted.Factory);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(!rooted.Host.IsAlive, "Final native Release leaked the host GCHandle");
        host.Dispose();
        host.Dispose();
        Reject<ObjectDisposedException>(() =>
        {
            host.Show(new("<toast/>"));
        });
        Reject<ObjectDisposedException>(() =>
        {
            _ = host.WaitForActivation(TimeSpan.Zero);
        });
        Reject<ObjectDisposedException>(() =>
        {
            host.Activated += _ =>
            {
            };
        });
        Console.WriteLine($"PASS: {_checks} validation/lifecycle/native-vtable regression checks");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Host, nint Factory) CreateRootedHost()
    {
        var host = new ToastHost("gc-test", Guid.NewGuid(), false);
        return (new WeakReference(host), (nint)ToastComActivator.CreateFactory(host));
    }

    private static void TestVtables(ToastHost host)
    {
        void* factory = ToastComActivator.CreateFactory(host);
        void* queried = null;
        void* callback = null;
        try
        {
            var unknown = Interop.IID_IUnknown;
            Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Interop.VTableEntry(factory, 0))(factory, &unknown, &queried) == 0, "IUnknown QI failed");
            Check(queried == factory, "COM identity broken");
            Interop.Release(queried);
            queried = null;
            queried = (void*)1;
            Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Interop.VTableEntry(factory, 0))(factory, null, &queried) == Interop.E_POINTER && queried == null, "QI left an invalid output pointer on error");
            var missing = Guid.NewGuid();
            Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Interop.VTableEntry(factory, 0))(factory, &missing, &queried) == Interop.E_NOINTERFACE && queried == null, "QI failure contract broken");
            var iid = Interop.IID_INotificationActivationCallback;
            var create = (delegate* unmanaged[Stdcall]<void*, void*, Guid*, void**, int>)Interop.VTableEntry(factory, 3);
            Check(create(factory, (void*)1, &iid, &callback) == Interop.CLASS_E_NOAGGREGATION && callback == null, "Aggregation accepted");
            Check(create(factory, null, &iid, &callback) == 0 && callback != null, "Callback creation failed");
            Check(!host.WaitForActivation(TimeSpan.Zero), "A retained COM callback allowed the process waiter to exit");
            var activate = (delegate* unmanaged[Stdcall]<void*, char*, char*, void*, uint, int>)Interop.VTableEntry(callback, 3);
            Check(activate(callback, null, null, null, 1) == Interop.E_FAIL, "Invalid input array accepted");
            Check(activate(callback, null, null, null, 1025) == Interop.E_FAIL, "Oversized input array accepted");
        }
        finally
        {
            Interop.Release(callback);
            ToastComActivator.ReleaseFactory(factory);
        }
    }
}
