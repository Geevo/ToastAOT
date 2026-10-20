using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ToastAOT;

/// <summary>Per-user LocalServer32 and Start Menu shortcut registration for unpackaged Win32 apps.</summary>
internal static unsafe class ToastRegistration
{
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_CLSID = 72;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid id, uint propertyId)
        {
            FormatId = id;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public void* Value;
    }

    private static readonly Guid PropertyFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static string GetShortcutPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('\\') || name.Contains('/') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
        {
            throw new ArgumentException("Specify a simple Start Menu shortcut name (without folders or extension).", nameof(name));
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", name + ".lnk");
    }

    /// <summary>
    /// Installs the per-user registration. With <paramref name="adoptExisting"/> an unmarked (hand-made or third-party)
    /// shortcut, AUMID key or CLSID key is replaced instead of refused; the replaced shortcut is first copied to a
    /// backup file whose path is returned. Registrations owned by another ToastAOT identity are never adopted.
    /// </summary>
    internal static string? Install(string appId, Guid clsid, string shortcutName, string exePath, string? displayName = null, Uri? iconUri = null, Action? registerComServer = null, bool adoptExisting = false)
    {
        string? backup = null;
        WithRegistrationLock(() =>
        {
            backup = InstallCore(appId, clsid, shortcutName, exePath, displayName, iconUri, registerComServer, adoptExisting);
        });
        return backup;
    }

    private static string? InstallCore(string appId, Guid clsid, string shortcutName, string exePath, string? displayName, Uri? iconUri, Action? registerComServer, bool adoptExisting)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        ValidateIdentity(appId, clsid);
        uint packageLength = 0;
        if (Interop.GetCurrentPackageFullName(ref packageLength, null) != 15700 /* APPMODEL_ERROR_NO_PACKAGE */)
        {
            throw new NotSupportedException("Packaged applications must use manifest registration.");
        }

        if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath) ||
            !string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            exePath.IndexOfAny(['\0', '"']) >= 0 ||
            string.Equals(Path.GetFileName(exePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Supply the full path to an existing .exe.", nameof(exePath));
        }

        string shortcutPath = GetShortcutPath(shortcutName);
        string? iconPath = null;
        if (displayName?.Contains('\0') == true)
        {
            throw new ArgumentException("Display name cannot contain NUL.", nameof(displayName));
        }

        if (iconUri != null)
        {
            if (!iconUri.IsAbsoluteUri || !iconUri.IsFile)
            {
                throw new ArgumentException("The notification icon must be an absolute local file URI.", nameof(iconUri));
            }

            // A missing cosmetic asset must not prevent notification registration.
            if (File.Exists(iconUri.LocalPath))
            {
                iconPath = iconUri.LocalPath;
            }
        }
        string registrationPath = $@"Software\Classes\CLSID\{{{clsid:D}}}";
        string identityPath = $@"Software\Classes\AppUserModelId\{appId}";
        using (var existingIdentity = Registry.CurrentUser.OpenSubKey(identityPath))
        {
            if (existingIdentity != null)
            {
                string? owner = existingIdentity.GetValue("ToastAOT.Owner") as string;
                if (owner != clsid.ToString("B"))
                {
                    if (!adoptExisting || owner != null)
                    {
                        throw new InvalidOperationException("The AUMID is already owned by another registration.");
                    }
                }
            }
        }
        bool shortcutIsOurs = false;
        using (var existing = Registry.CurrentUser.OpenSubKey(registrationPath))
        {
            if (existing != null)
            {
                string? markedApp = existing.GetValue("ToastAOT.AppId") as string;
                string? markedShortcut = existing.GetValue("ToastAOT.Shortcut") as string;
                shortcutIsOurs = markedApp == appId && markedShortcut == shortcutPath;
                if (!shortcutIsOurs && (!adoptExisting || markedApp != null || markedShortcut != null))
                {
                    throw new InvalidOperationException("The activator CLSID is already owned by another registration.");
                }
            }
        }
        bool replaceForeignShortcut = false;
        if (!shortcutIsOurs && File.Exists(shortcutPath))
        {
            if (!adoptExisting)
            {
                throw new InvalidOperationException("The Start Menu shortcut already exists and is not owned by this registration.");
            }

            string? claimedBy = FindShortcutClaimant(shortcutPath, clsid);
            if (claimedBy != null)
            {
                throw new InvalidOperationException("The Start Menu shortcut belongs to another ToastAOT registration (" + claimedBy + ") and cannot be adopted.");
            }

            replaceForeignShortcut = true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Interop.Check(Interop.SetCurrentProcessExplicitAppUserModelID(appId));
        string temporaryShortcut = shortcutPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var undo = new List<Action>();
        string? backupPath = null;
        try
        {
            SaveShortcut(temporaryShortcut, exePath, appId, clsid);
            SetRegistrationValue(registrationPath, "ToastAOT.AppId", appId, undo);
            SetRegistrationValue(registrationPath, "ToastAOT.Shortcut", shortcutPath, undo);
            // Quoting is essential for paths with spaces; the flag marks cold starts.
            SetRegistrationValue(registrationPath + @"\LocalServer32", "", "\"" + exePath + "\" " + ToastHost.ComActivationArgument, undo);
            // Explicit identity avoids waiting for asynchronous Start Menu indexing.
            SetRegistrationValue(identityPath, "ToastAOT.Owner", clsid.ToString("B"), undo);
            SetRegistrationValue(identityPath, "ToastAOT.Shortcut", shortcutPath, undo);
            SetRegistrationValue(identityPath, "DisplayName", displayName ?? shortcutName, undo);
            SetRegistrationValue(identityPath, "CustomActivator", clsid.ToString("B"), undo);
            // Clear stale metadata when the caller omits the icon or its file no longer exists.
            SetRegistrationValue(identityPath, "IconUri", iconPath, undo);

            registerComServer?.Invoke();
            if (replaceForeignShortcut)
            {
                // Keep the adopted shortcut so the owner can restore it. The extension keeps it out of the Start Menu.
                backupPath = shortcutPath + ".replaced-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
                File.Copy(shortcutPath, backupPath, overwrite: false);
                string createdBackup = backupPath;
                undo.Add(() =>
                {
                    File.Delete(createdBackup);
                });
            }

            // Keep publication as the final transactional operation: undo restores registry values, not the shortcut.
            // Any future fallible step after publication must also add shortcut restoration to undo.
            // Publish a complete shortcut only after the registration is installed. Leave an identical shortcut
            // alone: the shell and search indexer may hold it open, and rewriting it gains nothing.
            if (!File.Exists(shortcutPath) || !File.ReadAllBytes(shortcutPath).AsSpan().SequenceEqual(File.ReadAllBytes(temporaryShortcut)))
            {
                PublishShortcut(temporaryShortcut, shortcutPath);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            for (int i = undo.Count - 1; i >= 0; --i)
            {
                try
                {
                    undo[i]();
                }
                catch (Exception rollbackError)
                {
                    errors.Add(rollbackError);
                }
            }

            if (errors.Count > 1)
            {
                throw new AggregateException("Registration failed and rollback could not fully restore prior state.", errors);
            }

            throw;
        }
        finally
        {
            File.Delete(temporaryShortcut);
        }

        return backupPath;
    }

    /// <summary>The shell and indexer briefly open new shortcuts, so replacing one can hit a sharing violation or a delete-pending access denied.</summary>
    private static void PublishShortcut(string temporaryShortcut, string shortcutPath)
    {
        for (int attempt = 0; ; ++attempt)
        {
            try
            {
                File.Move(temporaryShortcut, shortcutPath, overwrite: true);
                return;
            }
            catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && attempt < 8 && !new FileInfo(shortcutPath).IsReadOnly)
            {
                Thread.Sleep(25 << attempt);
            }
        }
    }

    private static string? FindShortcutClaimant(string shortcutPath, Guid clsid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var identities = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        if (identities == null)
        {
            return null;
        }

        foreach (string name in identities.GetSubKeyNames())
        {
            using var key = identities.OpenSubKey(name);
            string? owner = key?.GetValue("ToastAOT.Owner") as string;
            if (owner != null && owner != clsid.ToString("B") &&
                string.Equals(key!.GetValue("ToastAOT.Shortcut") as string, shortcutPath, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void SetRegistrationValue(string path, string name, string? value, List<Action> undo)
    {
        using var existing = Registry.CurrentUser.OpenSubKey(path);
        if (existing == null)
        {
            undo.Add(() =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            });
        }
        else
        {
            object? previous = existing.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            RegistryValueKind kind = previous == null ? RegistryValueKind.String : existing.GetValueKind(name);
            undo.Add(() =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                if (previous == null)
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(name, previous, kind);
                }
            });
        }
        using var destination = Registry.CurrentUser.CreateSubKey(path, writable: true);
        if (value == null)
        {
            destination.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            destination.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private static void WithRegistrationLock(Action action)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        using var mutex = new Mutex(false, @"Local\ToastAOT.Registration");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Another process is changing this notification registration.");
            }
        }
        catch (AbandonedMutexException)
        { /* Previous owner exited; revalidate the stored registration. */
        }
        try
        {
            action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static void ValidateIdentity(string appId, Guid clsid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (appId.Length > 128 || appId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '/'))
        {
            throw new ArgumentException("An AUMID must be at most 128 characters with no spaces, controls or path separators.", nameof(appId));
        }

        if (clsid == Guid.Empty)
        {
            throw new ArgumentException("Choose a stable non-empty GUID.", nameof(clsid));
        }
    }

    // Only uninstall a matching library-owned registration. Normal process exit must
    // retain it, otherwise notifications cannot launch the application later.
    internal static void Uninstall(string appId, Guid clsid, Action? beforeRemove = null)
    {
        WithRegistrationLock(() =>
        {
            UninstallCore(appId, clsid, beforeRemove);
        });
    }

    private static void UninstallCore(string appId, Guid clsid, Action? beforeRemove)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        ValidateIdentity(appId, clsid);
        string path = $@"Software\Classes\CLSID\{{{clsid:D}}}";
        string? shortcut = null;
        bool ownsClass;
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            ownsClass = key != null;
            if (key != null && key.GetValue("ToastAOT.AppId") as string != appId)
            {
                throw new InvalidOperationException("The registration is not owned by this application.");
            }

            shortcut = key?.GetValue("ToastAOT.Shortcut") as string;
        }
        string identityPath = $@"Software\Classes\AppUserModelId\{appId}";
        bool ownsIdentity;
        using (var identity = Registry.CurrentUser.OpenSubKey(identityPath))
        {
            ownsIdentity = identity != null;
            if (identity != null && identity.GetValue("ToastAOT.Owner") as string != clsid.ToString("B"))
            {
                throw new InvalidOperationException("The AUMID is not owned by this application.");
            }

            shortcut ??= identity?.GetValue("ToastAOT.Shortcut") as string;
        }
        if (!ownsClass && !ownsIdentity)
        {
            return;
        }
        // Clear history only after ownership validation, while registration still
        // exists. If the platform fails, uninstall can safely be retried.
        beforeRemove?.Invoke();
        if (shortcut != null && string.Equals(GetShortcutPath(Path.GetFileNameWithoutExtension(shortcut)), shortcut, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(shortcut);
        }

        if (ownsClass)
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }

        if (ownsIdentity)
        {
            Registry.CurrentUser.DeleteSubKeyTree(identityPath, throwOnMissingSubKey: false);
        }
    }

    private static void SaveShortcut(string destination, string exePath, string appId, Guid clsid)
    {
        // Shell link and property store APIs are regular COM (implemented by Windows), but
        // we call their vtables directly so NativeAOT does not need runtime COM interop.
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        int init = Interop.CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        bool uninitialize = init >= 0;
        if (init < 0 && init != RPC_E_CHANGED_MODE)
        {
            Interop.Check(init);
        }

        void* shellLink = null;
        void* store = null;
        void* file = null;
        try
        {
            var clsidShell = Interop.CLSID_ShellLink;
            var iidShell = Interop.IID_IShellLinkW;
            Interop.Check(Interop.CoCreateInstance(&clsidShell, 0, Interop.CLSCTX_INPROC_SERVER, &iidShell, out shellLink));
            fixed (char* exe = exePath)
            {
                // IShellLinkW::SetPath is method 17 after IUnknown's first 3 slots = slot 20.
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, char*, int>)Interop.VTableEntry(shellLink, 20))(shellLink, exe));
            }
            store = Interop.QueryInterface(shellLink, Interop.IID_IPropertyStore);
            var appKey = new PropertyKey(PropertyFormatId, 5); // System.AppUserModel.ID
            var activatorKey = new PropertyKey(PropertyFormatId, 26); // ToastActivatorCLSID
            fixed (char* app = appId)
            {
                PropVariant prop = new()
                {
                    Type = VT_LPWSTR,
                    Value = app
                };
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, PropertyKey*, PropVariant*, int>)Interop.VTableEntry(store, 6))
                    (store, &appKey, &prop));
            }
            PropVariant guidProp = new()
            {
                Type = VT_CLSID,
                Value = &clsid
            };
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, PropertyKey*, PropVariant*, int>)Interop.VTableEntry(store, 6))
                (store, &activatorKey, &guidProp));
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, int>)Interop.VTableEntry(store, 7))(store));
            file = Interop.QueryInterface(shellLink, Interop.IID_IPersistFile);
            fixed (char* path = destination)
            {
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, char*, int, int>)Interop.VTableEntry(file, 6))(file, path, 1));
            }
        }
        finally
        {
            Interop.Release(file);
            Interop.Release(store);
            Interop.Release(shellLink);
            if (uninitialize)
            {
                Interop.CoUninitialize();
            }
        }
    }
}
