using ToastAOT.Builder;

namespace ToastAOT.Samples.ConsoleDemo;

internal static class Program
{
    private static void Main()
    {
        // Subscribe on every launch, before Register, including launches performed by COM.
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (sender, activation) =>
        {
            _ = activation.Arguments.TryGetValue("action", out string? action);
            IEnumerable<string> inputValues = activation.UserInput.Select(input => input.Key + ":" + input.Value);

            string details = $"{DateTimeOffset.Now:O} | action={action ?? "(toast body)"} | " +
                $"args={activation.Argument} | input={string.Join(",", inputValues)}";
            Console.WriteLine(details);

            // Diagnostic sample only: avoid logging sensitive user input in your application.
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ToastAOT-demo-activations.txt");

            File.AppendAllText(logPath, details + Environment.NewLine);
        };

        manager.ActivationHandlerFailed += e =>
        {
            Console.Error.WriteLine(e);
        };

        // Keep explicit identity stable across upgrades. Register() without arguments can
        // instead derive the identity from the executable path.
        manager.Register("ToastAOT.Samples.ConsoleDemo", new Guid("6CCB374B-9F71-4D76-B73F-6594B99D01A2"), "ToastAOT Console Demo");
        try
        {
            if (manager.IsComActivationLaunch)
            {
                // GUI applications should start their normal message loop instead of waiting.
                if (!manager.WaitForActivation(TimeSpan.FromSeconds(30)))
                {
                    Console.Error.WriteLine("No COM activation received before timeout.");
                    Environment.ExitCode = 2;
                }
                return;
            }

            manager.Show(new AppNotificationBuilder()
                .AddText("ToastAOT actionable Windows toast")
                .AddText("Click an action, or exit and use Notification Center.")
                .AddArgument("origin", "demo")
                .AddTextBox("reply", "Type a short reply", "Reply")
                .AddButton(new AppNotificationButton("Accept").AddArgument("action", "accept").SetInputId("reply"))
                .AddButton(new AppNotificationButton("Reject").AddArgument("action", "reject"))
                .SetTag("demo-1").SetGroup("test")
                .BuildNotification());
            Console.WriteLine("Toast submitted. Press Enter to exit, then click it in Notification Center.");
            _ = Console.ReadLine();
        }
        finally
        {
            // Retain registration for cold starts. Call UnregisterAll only when uninstalling.
            manager.Unregister();
        }
    }
}
