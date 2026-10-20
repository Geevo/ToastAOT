using System.Xml.Linq;

namespace ToastAOT.Tests;

internal static class Program
{
    private static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Contains("--registry-probe"))
        {
            bool visible = ColdStartProbe.IsClassRegistrationVisibleToServiceControl(out int hr);
            Console.WriteLine($"SCM sees this process's HKCU class registrations: {visible} (probe HRESULT 0x{hr:X8})");
            return;
        }

        if (args.Contains("--hold-registration") || args.Contains("--hold-lock"))
        {
            string ready = args[Array.IndexOf(args, args.Contains("--hold-lock") ? "--hold-lock" : "--hold-registration") + 1];
            if (args.Contains("--hold-lock"))
            {
                LifecycleTests.HoldLock(ready);
            }
            else
            {
                LifecycleTests.HoldRegistration(ready);
            }

            return;
        }

        if (args.Contains("--photinox-probe"))
        {
            WindowsTests.InvokeCom(
                sdkFormat: true,
                targetClsid: new Guid("2F7696F8-EC4B-4AE5-8BED-C081C18FFC8A"),
                targetAppId: "ToastAOT.Samples.PhotinoXDemo535");
            return;
        }

        if (args.Contains("--shell-warm") || args.Contains("--shell-cold"))
        {
            WindowsTests.PrepareShellTest(args.Contains("--shell-warm"));
            return;
        }
        if (args.Contains("--shell-cleanup"))
        {
            WindowsTests.CleanupShellTest();
            return;
        }
        if (args.Contains("--shell-verify") || args.Contains("--shell-verify-body"))
        {
            WindowsTests.VerifyShellTest(args.Contains("--shell-verify-body"));
            return;
        }
        if (args.Contains("--invoke-com") || args.Contains("--invoke-sdk-com"))
        {
            WindowsTests.InvokeCom(args.Contains("--invoke-sdk-com"));
            return;
        }
        if (args.Contains(ToastHost.ComActivationArgument))
        {
            WindowsTests.RunServer();
            return;
        }

        int checks = 0;
        void Check(bool correct, string reason)
        {
            if (!correct)
            {
                throw new Exception(reason);
            }

            ++checks;
        }

        var xml = new ToastBuilder()
            .AddText("<Hello & friends>")
            .AddAttribution("source")
            .AddImage("file:///C:/logo.png", ToastImagePlacement.AppLogoOverride, true)
            .AddTextInput("reply", "Your reply")
            .AddSelectionInput("choice", [("1", "One"), ("2", "Two")], "2")
            .AddAction("Yes", "accept", "reply")
            .AddButton("Open", "https://example.com", ToastActivationType.Protocol)
            .AddArgument("who", "A&B+😀")
            .SetScenario(ToastScenario.Reminder)
            .SetSilent()
            .Build();
        var doc = XDocument.Parse(xml.Xml);
        Check(doc.Root?.Name == "toast", "No toast root");
        Check((string?)doc.Root?.Attribute("scenario") == "reminder", "Scenario attribute");
        Check((string?)doc.Descendants("text").First() == "<Hello & friends>", "XML escaping");
        Check(doc.Descendants("action").Count() == 2, "Buttons missing");
        Check(doc.Descendants("input").Count() == 2, "Inputs missing");
        Check(doc.Descendants("audio").Single().Attribute("silent")?.Value == "true", "Silent missing");
        Check(ToastActivation.ParseArguments((string?)doc.Root?.Attribute("launch"))["who"] == "A&B+😀", "Round-trip encoding");
        Check(ToastActivation.ParseArguments("action=accept&value=a%26b")["action"] == "accept", "Action parse");
        Check(new ToastActivation("app", "action=x", new Dictionary<string, string>()).ActionId == "x", "Action property");
        Check(ToastActivation.ParseArguments("").Count == 0, "Empty args");

        bool threw = false;
        try
        {
            _ = new ToastBuilder().AddText("hi").AddAction("a", "a").AddAction("b", "b").AddAction("c", "c").AddAction("d", "d").AddAction("e", "e").AddAction("f", "f").Build();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check(threw, "Too many actions should fail");

        Console.WriteLine($"PASS: {checks} portable XML/argument checks");
        PortableTests.Run();
        ApiTests.Run();
        LifecycleTests.RunPortable();
        string? nativeStress = Array.Find(args, a =>
        {
            return a.StartsWith("--native-stress=", StringComparison.Ordinal);
        });
        StressTests.RunNative(nativeStress != null && int.TryParse(nativeStress.AsSpan("--native-stress=".Length), out int nativeIterations) ? nativeIterations : 20000);
        if (args.Contains("--windows") || args.Contains("--windows-warm") || args.Contains("--com-only"))
        {
            WindowsTests.Run(args.Contains("--windows") || args.Contains("--com-only"), !args.Contains("--com-only"));
        }
    }
}
