namespace ToastAOT;

/// <summary>Windows Runtime ABI calls. Every returned COM pointer and HSTRING is owned by a call scope.</summary>
internal static unsafe class WinRtToastSender
{
    private const string ManagerClass = "Windows.UI.Notifications.ToastNotificationManager";

    private sealed class Scope : IDisposable
    {
        private readonly List<nint> _objects = [];
        private readonly List<nint> _strings = [];

        internal Scope()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                throw new PlatformNotSupportedException("Windows 10 or later required.");
            }

            int hr = Interop.RoInitialize(1);
            if (hr == unchecked((int)0x80010106))
            {
                hr = Interop.RoInitialize(0);
            }

            Interop.Check(hr);
        }

        internal nint Text(string value)
        {
            nint text = Interop.HString(value);
            _strings.Add(text);
            return text;
        }

        internal void* Own(void* value)
        {
            if (value != null)
            {
                _objects.Add((nint)value);
            }

            return value;
        }

        internal void* Query(void* obj, Guid iid)
        {
            return Own(Interop.QueryInterface(obj, iid));
        }

        internal void* Factory(string name, Guid iid)
        {
            Interop.Check(Interop.RoGetActivationFactory(Text(name), &iid, out void* value));
            return Own(value);
        }

        internal void* Activate(string name)
        {
            Interop.Check(Interop.RoActivateInstance(Text(name), out void* value));
            return Own(value);
        }

        internal void* Notifier(string appId)
        {
            void* manager = Factory(ManagerClass, Interop.IID_IToastManager);
            void* notifier = null;
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, void**, int>)Interop.VTableEntry(manager, 7))(manager, Text(appId), &notifier));
            return Own(notifier);
        }

        internal void* History(string appId)
        {
            void* manager = Factory(ManagerClass, Interop.IID_IToastManager2);
            void* history = null;
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Interop.VTableEntry(manager, 6))(manager, &history));
            return Own(history);
        }

        public void Dispose()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
            {
                Interop.Release((void*)_objects[i]);
            }

            for (int i = _strings.Count - 1; i >= 0; --i)
            {
                _ = Interop.WindowsDeleteString(_strings[i]);
            }

            Interop.RoUninitialize();
        }
    }

    internal static void Show(string appId, ToastMessage message)
    {
        message.Validate();
        using var scope = new Scope();
        void* notifier = scope.Notifier(appId);
        void* document = scope.Activate("Windows.Data.Xml.Dom.XmlDocument");
        void* io = scope.Query(document, Interop.IID_IXmlDocumentIO);
        void* xml = scope.Query(document, Interop.IID_IXmlDocument);
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, int>)Interop.VTableEntry(io, 6))(io, scope.Text(message.Xml)));
        void* factory = scope.Factory("Windows.UI.Notifications.ToastNotification", Interop.IID_IToastFactory);
        void* notification = null;
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Interop.VTableEntry(factory, 6))(factory, xml, &notification));
        _ = scope.Own(notification);
        void* notification2 = scope.Query(notification, Interop.IID_IToastNotification2);
        // Windows rejects an empty HSTRING (E_INVALIDARG): leave unset tags and groups at their default.
        if (!string.IsNullOrEmpty(message.Tag))
        {
            SetString(scope, notification2, 6, message.Tag);
        }

        if (!string.IsNullOrEmpty(message.Group))
        {
            SetString(scope, notification2, 8, message.Group);
        }

        Interop.Check(((delegate* unmanaged[Stdcall]<void*, byte, int>)Interop.VTableEntry(notification2, 10))(notification2, message.SuppressPopup ? (byte)1 : (byte)0));
        if (message.Priority != ToastPriority.Default || message.Progress != null)
        {
            void* notification4 = scope.Query(notification, Interop.IID_IToastNotification4);
            if (message.Progress != null)
            {
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, void*, int>)Interop.VTableEntry(notification4, 7))(notification4, CreateData(scope, message.Progress)));
            }

            Interop.Check(((delegate* unmanaged[Stdcall]<void*, int, int>)Interop.VTableEntry(notification4, 9))(notification4, (int)message.Priority));
        }
        if (message.Expiration is { } expiration)
        {
            void* properties = scope.Factory("Windows.Foundation.PropertyValue", Interop.IID_IPropertyValueStatics);
            void* boxed = null;
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, long, void**, int>)Interop.VTableEntry(properties, 21))(properties, expiration.ToFileTime(), &boxed));
            _ = scope.Own(boxed);
            void* reference = scope.Query(boxed, Interop.IID_IReferenceDateTime);
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, void*, int>)Interop.VTableEntry(notification, 7))(notification, reference));
        }
        if (message.ExpiresOnReboot)
        {
            void* notification6 = scope.Query(notification, Interop.IID_IToastNotification6);
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, byte, int>)Interop.VTableEntry(notification6, 7))(notification6, 1));
        }
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, void*, int>)Interop.VTableEntry(notifier, 6))(notifier, notification));
    }

    internal static ToastSetting GetSetting(string appId)
    {
        using var scope = new Scope();
        void* notifier = scope.Notifier(appId);
        int setting;
        int hr = ((delegate* unmanaged[Stdcall]<void*, int*, int>)Interop.VTableEntry(notifier, 8))(notifier, &setting);
        // A newly registered identity has no platform settings record until its first
        // notification. Match the platform's default-enabled behavior for that case.
        if (hr == unchecked((int)0x80070490))
        {
            return ToastSetting.Enabled;
        }

        Interop.Check(hr);
        return (ToastSetting)setting;
    }

    internal static ToastUpdateResult Update(string appId, ToastProgressData progress, string tag, string? group)
    {
        ToastMessage.ValidateIdentifier(tag, nameof(tag));
        ToastMessage.ValidateIdentifier(group, nameof(group));
        progress.Validate();
        using var scope = new Scope();
        void* notifier = scope.Query(scope.Notifier(appId), Interop.IID_IToastNotifier2);
        void* data = CreateData(scope, progress);
        int result;
        if (group == null)
        {
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, void*, nint, int*, int>)Interop.VTableEntry(notifier, 7))(notifier, data, scope.Text(tag), &result));
        }
        else
        {
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, void*, nint, nint, int*, int>)Interop.VTableEntry(notifier, 6))(notifier, data, scope.Text(tag), scope.Text(group), &result));
        }

        return (ToastUpdateResult)result;
    }

    private static void* CreateData(Scope scope, ToastProgressData progress)
    {
        void* data = scope.Query(scope.Activate("Windows.UI.Notifications.NotificationData"), Interop.IID_INotificationData);
        void* map = null;
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Interop.VTableEntry(data, 6))(data, &map));
        _ = scope.Own(map);
        foreach (var pair in progress.Values())
        {
            byte replaced;
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, nint, byte*, int>)Interop.VTableEntry(map, 10))(map, scope.Text(pair.Key), scope.Text(pair.Value), &replaced));
        }
        // Set ordering after populating the mutable property map.
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, uint, int>)Interop.VTableEntry(data, 8))(data, progress.SequenceNumber));
        return data;
    }

    internal static void ClearHistory(string appId)
    {
        using var scope = new Scope();
        void* history = scope.History(appId);
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, int>)Interop.VTableEntry(history, 12))(history, scope.Text(appId)));
    }

    internal static void RemoveByGroup(string appId, string group)
    {
        using var scope = new Scope();
        void* history = scope.History(appId);
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, nint, int>)Interop.VTableEntry(history, 7))(history, scope.Text(group), scope.Text(appId)));
    }

    internal static void RemoveFromHistory(string appId, string tag, string? group)
    {
        using var scope = new Scope();
        void* history = scope.History(appId);
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, nint, nint, int>)Interop.VTableEntry(history, 8))(history, scope.Text(tag), scope.Text(group ?? ""), scope.Text(appId)));
    }

    internal static IReadOnlyList<ToastMessage> GetAll(string appId)
    {
        using var scope = new Scope();
        void* history = scope.Query(scope.History(appId), Interop.IID_IToastHistory2);
        void* view = null;
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, void**, int>)Interop.VTableEntry(history, 7))(history, scope.Text(appId), &view));
        _ = scope.Own(view);
        uint count;
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, uint*, int>)Interop.VTableEntry(view, 7))(view, &count));
        var result = new List<ToastMessage>();
        for (uint i = 0; i < count; ++i)
        {
            // Release per-item objects immediately instead of retaining the entire history.
            void* toast = null;
            // Release per-item objects immediately instead of retaining the entire history.
            void* content = null;
            // Release per-item objects immediately instead of retaining the entire history.
            void* serializer = null;
            // Release per-item objects immediately instead of retaining the entire history.
            void* properties = null;
            // Release per-item objects immediately instead of retaining the entire history.
            void* expiration = null;
            void* properties4 = null;
            void* properties6 = null;
            void* data = null;
            void* map = null;
            try
            {
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Interop.VTableEntry(view, 6))(view, i, &toast));
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Interop.VTableEntry(toast, 6))(toast, &content));
                serializer = Interop.QueryInterface(content, Interop.IID_IXmlNodeSerializer);
                string payload = GetString(serializer, 6);
                properties = Interop.QueryInterface(toast, Interop.IID_IToastNotification2);
                string tag = GetString(properties, 7);
                string group = GetString(properties, 9);
                byte suppress;
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, byte*, int>)Interop.VTableEntry(properties, 11))(properties, &suppress));
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Interop.VTableEntry(toast, 8))(toast, &expiration));
                DateTimeOffset? expires = null;
                if (expiration != null)
                {
                    long time;
                    Interop.Check(((delegate* unmanaged[Stdcall]<void*, long*, int>)Interop.VTableEntry(expiration, 6))(expiration, &time));
                    expires = DateTimeOffset.FromFileTime(time);
                }
                properties4 = Interop.QueryInterface(toast, Interop.IID_IToastNotification4);
                int priority;
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, int*, int>)Interop.VTableEntry(properties4, 8))(properties4, &priority));
                Interop.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Interop.VTableEntry(properties4, 6))(properties4, &data));
                ToastProgressData? progress = null;
                if (data != null)
                {
                    uint sequence;
                    Interop.Check(((delegate* unmanaged[Stdcall]<void*, uint*, int>)Interop.VTableEntry(data, 7))(data, &sequence));
                    Interop.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)Interop.VTableEntry(data, 6))(data, &map));
                    string value = GetMapString(scope, map, "progressValue");
                    progress = new ToastProgressData
                    {
                        SequenceNumber = sequence,
                        Title = GetMapString(scope, map, "progressTitle"),
                        Status = GetMapString(scope, map, "progressStatus"),
                        ValueStringOverride = GetMapString(scope, map, "progressValueString"),
                        IsIndeterminate = value == "indeterminate",
                        Value = double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double numeric) ? numeric : 0
                    };
                }
                var iid6 = Interop.IID_IToastNotification6;
                int hr6 = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Interop.VTableEntry(toast, 0))(toast, &iid6, &properties6);
                byte reboot = 0;
                if (hr6 != Interop.E_NOINTERFACE)
                {
                    Interop.Check(hr6);
                    Interop.Check(((delegate* unmanaged[Stdcall]<void*, byte*, int>)Interop.VTableEntry(properties6, 6))(properties6, &reboot));
                }
                result.Add(new ToastMessage(payload)
                {
                    Tag = tag,
                    Group = group,
                    SuppressPopup = suppress != 0,
                    Expiration = expires,
                    Priority = (ToastPriority)priority,
                    Progress = progress,
                    ExpiresOnReboot = reboot != 0
                });
            }
            finally
            {
                Interop.Release(map);
                Interop.Release(data);
                Interop.Release(properties6);
                Interop.Release(properties4);
                Interop.Release(expiration);
                Interop.Release(properties);
                Interop.Release(serializer);
                Interop.Release(content);
                Interop.Release(toast);
            }
        }
        return result.AsReadOnly();
    }

    private static void SetString(Scope scope, void* obj, int index, string text)
    {
        Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint, int>)Interop.VTableEntry(obj, index))(obj, scope.Text(text)));
    }

    private static string GetString(void* obj, int index)
    {
        nint value = 0;
        try
        {
            Interop.Check(((delegate* unmanaged[Stdcall]<void*, nint*, int>)Interop.VTableEntry(obj, index))(obj, &value));
            return Interop.ReadHString(value);
        }
        finally
        {
            _ = Interop.WindowsDeleteString(value);
        }
    }

    private static string GetMapString(Scope scope, void* map, string key)
    {
        nint value = 0;
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<void*, nint, nint*, int>)Interop.VTableEntry(map, 6))(map, scope.Text(key), &value);
            if (hr == unchecked((int)0x8000000B) /* E_BOUNDS: key absent */)
            {
                return "";
            }

            Interop.Check(hr);
            return Interop.ReadHString(value);
        }
        finally
        {
            _ = Interop.WindowsDeleteString(value);
        }
    }
}
