# Microsoft AppNotifications API comparison

ToastAOT targets unpackaged Win32 applications. It uses the concepts and naming from Microsoft's [notification guide](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet?pivots=winforms), with namespaces `ToastAOT` and `ToastAOT.Builder`. It is not a binary-compatible replacement and does not provide complete API parity.

## Manager and notification

| Feature | ToastAOT | Verification or difference |
| --- | --- | --- |
| Registration and explicit identities | Supported | Per-user COM, AUMID and shortcut registration; ownership, rollback and adoption tests |
| Custom registration icon | Supported | Metadata and custom PNG rendering tested; immediate visible removal is not reliable |
| Running-app activation | Supported | Separate-process COM and manual shell actions tested |
| Closed-app activation | Supported | Automated SCM launch and manual Notification Center body/Reply tested on Windows x64 |
| Argument and user-input delivery | Supported | Unicode, literal plus, text input and selected values tested |
| Activation buffering | Extension | Up to 1,024 activations before subscription; application owns UI readiness |
| Unregister / UnregisterAll | Supported | Process cleanup, persistent removal and registration exclusion tested |
| Settings / IsSupported | Supported | Broader policy and environment coverage remains pending |
| Show | Supported | XML, data, tag/group, priority, suppression, expiration and reboot flag |
| GetAllAsync | Supported | OS history snapshots; transient properties may be reconstructed or defaulted |
| Removal by tag, group or both | Supported | Windows integration tested; removal by tag across groups is not atomic |
| RemoveAllAsync | Supported | Windows integration tested |
| UpdateAsync | Supported | Stored progress updates tested; see sequence notes below |
| Expiration | Supported | Timed removal from history tested |
| Priority and suppression | Supported | Native calls tested; broader visible/policy behaviour pending |
| ExpiresOnReboot | Supported | Native setter tested; actual reboot behaviour pending |
| Platform Id / RemoveByIdAsync | Not implemented | No supported own-app numeric-ID contract exposed by the public WinRT transport; use tags/groups |
| Conferencing configuration | Not implemented | Preview camera/device-setting contracts are not implemented |
| Packaged registration | Not supported yet | Explicitly rejected |
| SDK AppLifecycle / AppInstance | Outside scope | Application owns process and UI lifetime |

## Builder

| Feature | Coverage |
| --- | --- |
| Arguments and payload snapshots | SDK semicolon encoding, duplicate replacement, Unicode and repeated builds tested |
| Text and text properties | XML generation tested |
| Attribution text, timestamp | XML generation tested |
| Inline, hero and app-logo images | XML generation tested; broader rendering coverage pending |
| Duration and scenarios | XML generation tested; audible/policy behaviour pending |
| Audio, looping and mute | XML generation tested; sound acceptance pending |
| Text boxes and combo boxes | XML and shell input delivery tested; combo Items dictionary is assignable |
| Buttons | Arguments, tooltip, icons, input hint, context menu and style XML; basic shell actions tested |
| Protocol actions | XML generation tested; external-handler acceptance pending |
| Progress | Default/static/rebound XML and Windows storage tested |
| Camera preview / device settings | Not implemented |

## Behavioural differences

Progress value and status bind by default. Optional title and value text are omitted until set or bound with `BindTitle()` / `BindValueStringOverride()`. Assigning a property makes that field static; binding it again retains the stored property value.

On the tested Windows build, history can report a platform sequence greater than the submitted progress sequence. Read the current value with `GetAllAsync()` and use a greater sequence for the next update. A stale update may return success without changing values. The familiar API requires a nonzero sequence; the lower-level `ToastHost.Update` API also allows zero to disable ordering.

History is an OS snapshot, not a durable copy of the submitted object. In local tests, high priority and the reboot flag read back as default values. This does not establish their visible or reboot behaviour.

`AppNotificationBuilder` uses the SDK's semicolon argument format, escaping `%`, `;` and `=`. The lower-level `ToastBuilder` uses URL-query encoding. Do not mix their argument parsers.

Managed tasks and read-only collections replace WinRT async/collection projections. Missing icon files fall back instead of throwing. Registration is strict about ownership; adopting existing registrations is opt-in. Raw XML is supported, with DTDs, invalid roots and oversized payloads rejected.

Scheduled notifications, badges, notification listeners and cloud push are separate Windows APIs and are not part of this library's local notification surface. Only documented OS interfaces are used.
