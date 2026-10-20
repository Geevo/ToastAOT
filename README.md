# ToastAOT

## What is ToastAOT?

A lightweight C# library for Windows toast notifications, with support for NativeAOT and trimming. It runs in unpackaged Win32 desktop applications without the Windows App SDK runtime or third-party dependencies.

ToastAOT aims to follow the same syntax and usage patterns as Microsoft's [Win32 app notification documentation](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet?pivots=winforms): register the application, subscribe to activation, build a notification and show it. Use `ToastAOT` and `ToastAOT.Builder` in place of the corresponding Microsoft namespaces.

## When should you use it?

Use ToastAOT when your desktop application needs actionable Windows notifications and you want to publish with NativeAOT or avoid deploying the Windows App SDK runtime.

It supports buttons, text input, selection lists, images, progress updates, notification history and activation while the application is running or closed. It is a source-level subset of Microsoft's API; see the [feature comparison](doc/FEATURE_PARITY.md) for differences.

## Requirements

- Windows 10 build 17763 or later, running as a standard user.
- .NET 8, 9 or 10. .NET 11 preview is also targeted.
- An unpackaged application deployed as an executable at a stable path.

Elevated processes are not supported.

## Installation

Build from source and add a project reference:

```powershell
dotnet add path\to\YourApp.csproj reference ToastAOT\ToastAOT.csproj
```

## How to use

This console example sends a notification with a Reply button. It also handles a notification click after the original process has exited.

```csharp
using ToastAOT;
using ToastAOT.Builder;

var manager = AppNotificationManager.Default;
manager.NotificationInvoked += (_, activation) =>
{
    activation.Arguments.TryGetValue("action", out string? action);
    activation.UserInput.TryGetValue("reply", out string? reply);
    Console.WriteLine($"Action: {action}; reply: {reply}");
};
manager.ActivationHandlerFailed += exception =>
{
    Console.Error.WriteLine(exception);
};

manager.Register(
    "YourCompany.YourApp",
    new Guid("CA87A55D-A699-4D79-969E-F75B7328ADBA"),
    "Your App");

try
{
    if (manager.IsComActivationLaunch)
    {
        if (!manager.WaitForActivation(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("Notification activation timed out.");
        }
    }
    else
    {
        var notification = new AppNotificationBuilder()
            .AddText("New message")
            .AddText("Reply here, or click the notification to open the app.")
            .AddArgument("action", "open")
            .AddTextBox("reply", "Type a reply", "Reply")
            .AddButton(new AppNotificationButton("Reply")
                .AddArgument("action", "reply")
                .SetInputId("reply"))
            .SetTag("message-1")
            .SetGroup("messages")
            .BuildNotification();

        manager.Show(notification);
        Console.WriteLine("Press Enter to exit. The notification can reopen the app.");
        Console.ReadLine();
    }
}
finally
{
    manager.Unregister();
}
```

Choose your own permanent AppUserModelID and generate a CLSID once for your application. Keep both across upgrades. Parameterless `Register()` instead derives an identity from the executable path; moving the executable changes it.

Subscribe on every launch. GUI applications should run their normal message loop instead of calling `WaitForActivation`, and dispatch callbacks to the UI thread. A callback can arrive before the first window exists. See [registration and activation](doc/LIFECYCLE.md).

### Notification history

After registration:

```csharp
var notifications = await manager.GetAllAsync();
await manager.RemoveByTagAndGroupAsync("message-1", "messages");
await manager.RemoveByGroupAsync("messages");
await manager.RemoveAllAsync();
```

Use tags and groups to identify notifications. Platform-generated notification IDs and removal by ID are not exposed.

### Progress updates

```csharp
var notification = new AppNotificationBuilder()
    .AddText("Downloading archive")
    .AddProgressBar(new AppNotificationProgressBar().BindTitle())
    .SetTag("download-1")
    .SetGroup("downloads")
    .BuildNotification();

notification.Progress = new AppNotificationProgressData(1)
{
    Title = "Archive.zip",
    Value = 0.25,
    Status = "Downloading"
};
manager.Show(notification);
```

Use `UpdateAsync(data, tag, group)` to update progress. Value and status bind by default; optional title and value text require `BindTitle()` and `BindValueStringOverride()`. See [API differences](doc/FEATURE_PARITY.md) for sequence and history behaviour.

## NativeAOT deployment

Set `PublishAot` in the application project:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

Then publish for Windows:

```powershell
dotnet publish path\to\YourApp.csproj -c Release -r win-x64
```

Publishing requires the Visual Studio C++ build tools and a Windows SDK. The published executable does not require a .NET or Windows App SDK runtime installation.

## Samples

- [Console](ToastAOT.Samples.ConsoleDemo): buttons, reply input and closed-app activation.
- [PhotinoX](ToastAOT.Samples.PhotinoXDemo): a desktop WebView sample with reply input, selections, progress and history.

## Licence

[MIT](LICENSE).
