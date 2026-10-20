using System.Xml.Linq;
using ToastAOT.Builder;

namespace ToastAOT.Tests;

internal static class ApiTests
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

    private static void VerifyCollectionAssignment()
    {
        var arguments = new Dictionary<string, string> { ["value"] = "A;B=C+世界 😀" };
        var button = new AppNotificationButton("Open")
        {
            Arguments = arguments
        };
        var items = new Dictionary<string, string> { ["one"] = "One & <first>" };
        var combo = new AppNotificationComboBox("choice")
        {
            Items = items,
            SelectedItem = "one"
        };
        var builder = new AppNotificationBuilder().AddText("Assigned collections").AddButton(button).AddComboBox(combo);
        AppNotification first = builder.BuildNotification();
        XDocument xml = XDocument.Parse(first.Payload);
        string encoded = (string)xml.Descendants("action").Single().Attribute("arguments")!;
        Check(AppNotificationArgumentCodec.Parse(encoded)["value"] == arguments["value"], "Assigned button arguments lost encoding");
        Check((string?)xml.Descendants("selection").Single().Attribute("content") == items["one"], "Assigned combo items lost XML escaping");
        arguments["later"] = "updated";
        items["two"] = "Two";
        Check(builder.BuildNotification().Payload.Contains("later=updated") &&
            XDocument.Parse(builder.BuildNotification().Payload).Descendants("selection").Count() == 2,
            "Collection assignment did not retain the supplied map like the SDK");
        Check(!first.Payload.Contains("later=updated") && XDocument.Parse(first.Payload).Descendants("selection").Count() == 1,
            "Collection mutation changed an already-built notification");
        Reject<ArgumentNullException>(() =>
        {
            button.Arguments = null!;
        });
        Reject<ArgumentNullException>(() =>
        {
            combo.Items = null!;
        });
        var protocol = new AppNotificationButton("Protocol").SetInvokeUri(new Uri("https://example.com"));
        protocol.Arguments = arguments;
        Reject<InvalidOperationException>(() =>
        {
            _ = new AppNotificationBuilder().AddText("Invalid mixed activation").AddButton(protocol).BuildNotification();
        });
    }

    private static void VerifyProgressBindings()
    {
        var progress = new AppNotificationProgressBar();
        XElement defaults = progress.ToXml();
        Check(defaults.Attribute("title") == null && defaults.Attribute("valueStringOverride") == null,
            "Unset optional progress fields must be omitted, matching the SDK");
        Check((string?)defaults.Attribute("status") == "{progressStatus}" &&
            (string?)defaults.Attribute("value") == "{progressValue}", "Default progress value/status bindings missing");

        progress.SetTitle("Downloading & <files>").SetStatus("Working").SetValue(.4).SetValueStringOverride("4/10");
        XElement original = progress.ToXml();
        progress.BindTitle().BindStatus().BindValue().BindValueStringOverride();
        Check(progress.Title == "Downloading & <files>" && progress.Status == "Working" &&
            progress.Value == .4 && progress.ValueStringOverride == "4/10", "Binding discarded previously set progress property values");
        XElement rebound = progress.ToXml();
        Check((string?)rebound.Attribute("title") == "{progressTitle}" &&
            (string?)rebound.Attribute("status") == "{progressStatus}" &&
            (string?)rebound.Attribute("value") == "{progressValue}" &&
            (string?)rebound.Attribute("valueStringOverride") == "{progressValueString}", "Explicit progress bindings incorrect");
        Check((string?)original.Attribute("title") == "Downloading & <files>" &&
            (string?)original.Attribute("value") == "0.4", "Rebinding mutated a previous XML snapshot");
        progress.Title = "";
        progress.Status = "";
        progress.Value = 0;
        progress.ValueStringOverride = "";
        XElement empty = progress.ToXml();
        Check((string?)empty.Attribute("title") == "" && (string?)empty.Attribute("status") == "" &&
            (string?)empty.Attribute("value") == "0" && (string?)empty.Attribute("valueStringOverride") == "",
            "Static empty values must remove progress bindings");
    }

    internal static void Run()
    {
        string[] values = ["", ";=%3B%3D%25", "a&b+c", "世界 😀", "'\"<>&", "a%20b", "%3b"];
        foreach (string value in values)
        {
            var builder = new AppNotificationBuilder().AddText(value).AddArgument("value", value).AddArgument("empty", "");
            var xml = XDocument.Parse(builder.BuildNotification().Payload);
            string raw = (string)xml.Root!.Attribute("launch")!;
            var args = new AppNotificationActivatedEventArgs(new("app", raw, new Dictionary<string, string>()));
            Check(args.Arguments["value"] == value && args.Arguments["empty"] == "", "SDK argument encoding round-trip");
            Check(xml.Descendants("text").Single().Value == value, "XML text escaping");
        }
        var button = new AppNotificationButton("Reply <&>").AddArgument("action", "reply").AddArgument("value", "a;b=c+&😀")
            .SetInputId("reply").SetIcon(new("file:///C:/button.png")).SetToolTip("Tip <&>").SetButtonStyle(AppNotificationButtonStyle.Success);
        var content = new AppNotificationBuilder().AddText("Hello", new AppNotificationTextProperties().SetLanguage("en-GB").SetMaxLines(2))
            .AddArgument("id", "first").AddArgument("id", "last").AddTextBox("reply", "Enter a reply", "Reply")
            .AddComboBox(new AppNotificationComboBox("choice").AddItem("one", "One").AddItem("two", "Two").SetSelectedItem("two").SetTitle("Choose"))
            .AddButton(button).AddButton(new AppNotificationButton("Web").SetInvokeUri(new("https://example.com/?a=1&b=2"), "Target.Package"))
            .AddButton(new AppNotificationButton("More").AddArgument("action", "more").SetContextMenuPlacement())
            .SetInlineImage(new("file:///C:/old.png")).SetInlineImage(new("file:///C:/new.png"), AppNotificationImageCrop.Circle, "Alt <&>")
            .SetHeroImage(new("file:///C:/hero.png"), "Hero").SetAppLogoOverride(new("file:///C:/logo.png"))
            .SetAttributionText("Source &", "en-GB").SetAudioEvent(AppNotificationSoundEvent.Alarm2, AppNotificationAudioLooping.Loop)
            .SetTimeStamp(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(1)))
            .SetDuration(AppNotificationDuration.Long).SetScenario(AppNotificationScenario.Reminder).SetTag("tag").SetGroup("group");
        var first = content.BuildNotification();
        var second = content.BuildNotification();
        Check(first.Payload == second.Payload, "Repeated BuildNotification mutated the builder");
        Check(first.Tag == "tag" && first.Group == "group", "Identity properties missing");
        var doc = XDocument.Parse(first.Payload);
        Check((string?)doc.Root!.Attribute("launch") == "id=last", "Duplicate arguments must replace existing values");
        Check(doc.Descendants("action").Count() == 3 && doc.Descendants("input").Count() == 2, "Actions or inputs missing");
        Check(doc.Descendants("image").Count() == 3 && !first.Payload.Contains("old.png"), "Image setter appended instead of replaced");
        var action = doc.Descendants("action").First();
        Check((string?)action.Attribute("content") == "Reply <&>" && (string?)action.Attribute("hint-toolTip") == "Tip <&>", "Button escaping");
        Check((string?)action.Attribute("hint-buttonStyle") == "Success" && (string?)doc.Root.Attribute("useButtonStyle") == "true", "Button style schema mismatch");
        Check((string?)action.Attribute("hint-inputId") == "reply" && (string?)action.Attribute("imageUri") == "file:///C:/button.png", "Button icon or input hint missing");
        var parsed = AppNotificationArgumentCodec.Parse((string)action.Attribute("arguments")!);
        Check(parsed["value"] == "a;b=c+&😀", "SDK button arguments corrupted");
        Check((string?)doc.Descendants("action").ElementAt(1).Attribute("protocolActivationTargetApplicationPfn") == "Target.Package", "Protocol target missing");
        Check((string?)doc.Descendants("action").Last().Attribute("placement") == "contextMenu", "Context menu missing");
        Check((string?)doc.Root.Attribute("duration") == "long" && (string?)doc.Root.Attribute("scenario") == "reminder", "Scenario or duration missing");
        Check((string?)doc.Descendants("audio").Single().Attribute("src") == "ms-winsoundevent:Notification.Looping.Alarm2", "Sound event mismatch");
        Check((string?)doc.Descendants("audio").Single().Attribute("loop") == "true", "Looping audio missing");
        Check(doc.Descendants("selection").Count() == 2, "Combo items missing");
        Check(doc.Descendants("text").First().Attribute("lang")?.Value == "en-GB" && doc.Descendants("text").First().Attribute("hint-maxLines")?.Value == "2", "Text properties missing");
        Check(doc.Root.Attribute("displayTimestamp")?.Value.StartsWith("2026-10-19T12:00:00") == true, "Timestamp missing");
        button.Content = "Changed";
        Check(!first.Payload.Contains("Changed") && content.BuildNotification().Payload.Contains("Changed"), "Notification snapshot mutated");
        var call = XDocument.Parse(new AppNotificationBuilder().AddText("Call", new AppNotificationTextProperties().SetIncomingCallAlignment()).BuildNotification().Payload);
        Check((string?)call.Root!.Attribute("scenario") == "incomingCall", "Incoming call alignment did not select incomingCall scenario");
        VerifyCollectionAssignment();
        VerifyProgressBindings();
        var bar = new AppNotificationProgressBar();
        var progressBuilder = new AppNotificationBuilder().AddProgressBar(bar);
        Check(progressBuilder.BuildNotification().Payload.Contains("{progressValue}"), "Progress binding missing");
        bar.SetValue(.5).SetTitle("Static").SetStatus("Working").SetValueStringOverride("Half");
        Check(progressBuilder.BuildNotification().Payload.Contains("value=\"0.5\""), "Static progress missing");
        bar.BindValue().BindTitle().BindStatus().BindValueStringOverride();
        Check(progressBuilder.BuildNotification().Payload.Contains("{progressTitle}"), "Rebinding failed");
        Check(new AppNotificationBuilder().AddText("a").MuteAudio().BuildNotification().Payload.Contains("silent=\"true\""), "Mute audio missing");
        Reject<ArgumentOutOfRangeException>(() =>
        {
            new AppNotificationProgressData(0);
        });
        Reject<ArgumentOutOfRangeException>(() =>
        {
            new AppNotificationProgressData(1).SequenceNumber = 0;
        });
        Reject<ArgumentOutOfRangeException>(() =>
        {
            bar.SetValue(double.NaN);
        });
        Reject<ArgumentException>(() =>
        {
            new AppNotificationBuilder().AddArgument("", "x");
        });
        Reject<InvalidOperationException>(() =>
        {
            new AppNotificationButton("protocol").SetInvokeUri(new("https://example.com")).AddArgument("x", "y");
        });
        Reject<InvalidOperationException>(() =>
        {
            new AppNotificationButton("protocol").AddArgument("x", "y").SetInvokeUri(new("https://example.com"));
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new AppNotificationBuilder().AddText("a").AddTextBox("x").AddComboBox(new("x")).BuildNotification();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new AppNotificationBuilder().AddText("a").AddButton(new AppNotificationButton("b").SetInputId("missing")).BuildNotification();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = new AppNotificationBuilder().AddText("a").AddComboBox(new AppNotificationComboBox("x").AddItem("a", "A").SetSelectedItem("bad")).BuildNotification();
        });
        Reject<ArgumentException>(() =>
        {
            new AppNotification("<not-a-toast/>");
        });
        var tooManyButtons = new AppNotificationBuilder().AddText("a");
        for (int i = 0; i < 6; ++i)
        {
            tooManyButtons.AddButton(new("b"));
        }

        Reject<InvalidOperationException>(() =>
        {
            _ = tooManyButtons.BuildNotification();
        });
        using var manager = new AppNotificationManager();
        Reject<InvalidOperationException>(() =>
        {
            manager.Show(new("<toast/>"));
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = manager.GetAllAsync();
        });
        Reject<InvalidOperationException>(() =>
        {
            _ = manager.WaitForActivation(TimeSpan.Zero);
        });
        manager.Unregister();
        Console.WriteLine($"PASS: {_checks} familiar API/content/argument contract checks");
    }
}
