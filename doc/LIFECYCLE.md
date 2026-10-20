# Registration and activation

## Application identity

`Register()` derives an AppUserModelID and CLSID from the executable's absolute path. Use `Register(appId, activatorClsid, displayName)` when an installer may change that path between versions. Generate a unique CLSID once and preserve it with the AppUserModelID. The display name is also used as a Start Menu shortcut name and must be a valid simple filename.

Deploy an apphost executable or a NativeAOT executable. `dotnet.exe app.dll` cannot be registered as the COM server. Framework-dependent deployments must retain their runtime configuration and assemblies alongside the apphost.

Registration creates a per-user Start Menu shortcut and COM/AUMID registry entries. It refuses unrelated registrations by default. Calls on one manager cannot overlap; registering an already registered manager throws `InvalidOperationException`. Call `Unregister()` before registering it again.

For an installer-owned legacy registration that you intend to replace, use `RegisterAdoptingExisting`. Adoption preserves a shortcut backup, rolls back on failure and refuses registrations owned by another ToastAOT identity. `ReplacedShortcutBackupPath` reports the backup path. Adoption is an explicit migration choice, not the default installation path.

## Icons

`Register(displayName, iconUri)` accepts a local icon for an automatic identity. Explicit registration and adoption also accept `iconUri`:

```csharp
manager.Register(appId, activatorClsid, displayName, iconUri: iconUri);
```

Omitting the icon, or supplying a missing local file, clears any previous `IconUri` registry value and allows registration to continue. Relative and non-file URIs are rejected. Supply the icon on subsequent registrations when retaining custom branding.

Immediate visible logo refresh is not guaranteed. Microsoft's SDK 2.0.1 also retained a previous custom logo after removing `IconUri` on the tested Windows build. This is a rendering limitation observed in both implementations; its cause is unconfirmed. ToastAOT does not reset system caches or restart the shell.

Microsoft's implementation rejects missing icon files. ToastAOT deliberately treats a missing cosmetic asset as a fallback instead of preventing notification registration.

## Running and closed applications

Subscribe to `NotificationInvoked` on every launch. Windows delivers the activation argument and user input through COM; they are not recovered from the process command line. `IsComActivationLaunch` identifies a process started using the registered activation flag.

Callbacks normally arrive on COM threads. Dispatch UI work through the application's dispatcher. The manager buffers up to 1,024 activations received before the first subscriber, then replays them on the subscribing thread. Overflow and handler exceptions are reported through `ActivationHandlerFailed`.

Buffering does not wait for a window or dispatcher to become ready. Either subscribe once the UI can accept work, or queue activations in the application and drain that queue after startup. If a framework dispatcher cannot accept work immediately, retry at the application level.

GUI applications should keep their normal message loop. Console hosts can use `WaitForActivation` to wait for synchronous handlers and native callback release. Do not call it from an activation handler. Asynchronous work must have its own lifetime management; `async void` handlers cannot keep a console process alive reliably.

Activation input is untrusted local input. Validate action names, identifiers, user text and URIs before using them. Protocol actions invoke the URI handler instead of raising the notification callback.

## Shutdown and uninstall

`Unregister()` and `Dispose()` revoke the current process factory but retain persistent registration for future cold starts. They do not remove the user's notifications.

`UnregisterAll()` removes owned shortcut and registry entries and clears the application's notification history. Use it for uninstall after outstanding application operations have finished. Windows-owned notification preferences may remain.

Registration writes are serialized; shortcut publication is atomic and registry changes roll back on failure. This does not replace an installer's recovery and upgrade logic.

## Launch environment

Use a normal, unelevated desktop process. Packaged applications are not supported yet. Package registry virtualization can also affect testing performed under a packaged host: registrations visible to the test process may not be visible to the COM service control manager. Launching from File Explorer or a normal desktop terminal avoids relying on that host's registry view.
