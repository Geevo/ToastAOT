# PhotinoX notification sample

A Windows desktop sample using [PhotinoX](https://github.com/ivanvoyager/PhotinoX) and ToastAOT. Notifications are sent through ToastAOT. PhotinoX retains the upstream C# namespace `Photino.NET`; the separate Photino package is not referenced.

## Build and run

From the repository root:

```powershell
dotnet publish .\ToastAOT.Samples.PhotinoXDemo -c Release -f net10.0-windows -p:IlcTreatWarningsAsErrors=true -o artifacts\photinox-demo
```

Launch `artifacts\photinox-demo\ToastAOT.Samples.PhotinoXDemo.exe` from File Explorer as a normal desktop user. Keep native DLLs and `wwwroot` beside the executable. The sample requires Microsoft Edge WebView2; its NativeAOT publish does not require .NET or the Windows App SDK runtime.

The project targets .NET 8, 9, 10 and 11 preview. Select a framework with `-f` and publish each to a separate directory.

## Try notifications

**Send test notification** submits a toast with body activation, Reply/Dismiss buttons, text input, selection and progress. Reply and watch the fields update in the WebView. **Refresh history** reads Windows history; **Clear test history** removes this sample's notification group. Dismiss is an application action carrying `action=dismiss`.

**Send and exit** leaves a notification in Notification Center. Clicking its body or Reply reopens the application and delivers the activation arguments. A callback can arrive before the WebView is ready; the sample buffers display until the ready handshake.

The icon controls re-register the same sample identity with a custom PNG, no icon or a missing file, and log the resulting registry metadata. Windows may retain an earlier attribution icon after metadata removal; see [registration and icons](../doc/LIFECYCLE.md#icons).

The sample uses a stable AppUserModelID/CLSID. Normal exit retains registration for future activation. Close it before removing its registration and notifications:

```powershell
.\artifacts\photinox-demo\ToastAOT.Samples.PhotinoXDemo.exe --unregister
```

## Implementation

`DemoApplication` subscribes before registration, queues activation until the page is ready, and dispatches through `PhotinoApplication.Dispatcher`. JSON uses source generation for trimming. The WebView loads local assets, restricts navigation/message origin and renders input with `textContent`.
