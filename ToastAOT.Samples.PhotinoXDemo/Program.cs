using ToastAOT.Builder;

namespace ToastAOT.Samples.PhotinoXDemo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(DemoApplication.DataDirectory);

        try
        {
            if (args.Contains("--unregister", StringComparer.Ordinal))
            {
                ToastHost.UnregisterAll(DemoApplication.AppId, DemoApplication.ActivatorClsid);
                return 0;
            }

            if (args.Contains("--basic-toast", StringComparer.Ordinal))
            {
                using var notifications = new AppNotificationManager();
                notifications.Register(DemoApplication.AppId, DemoApplication.ActivatorClsid, "ToastAOT PhotinoX Demo");
                AppNotification notification = new AppNotificationBuilder()
                    .AddText("Basic NativeAOT notification")
                    .AddText("This test has no inputs, buttons, progress or popup suppression.")
                    .SetTag(Guid.NewGuid().ToString("N")[..16])
                    .SetGroup("photino-demo")
                    .BuildNotification();
                notifications.Show(notification);
                File.WriteAllText(
                    Path.Combine(DemoApplication.DataDirectory, "basic-toast-result.txt"),
                    "Submitted; setting=" + notifications.Setting + Environment.NewLine + notification.Payload);
                return 0;
            }

            using var demo = new DemoApplication(args.Contains("--smoke-test", StringComparer.Ordinal));
            return demo.Run(args.Contains("--send-and-exit", StringComparer.Ordinal));
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(DemoApplication.DataDirectory, "startup-error.txt"), exception.ToString());
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
