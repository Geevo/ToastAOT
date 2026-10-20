using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ToastAOT;

/// <summary>
/// Native COM class factory + INotificationActivationCallback, implemented with raw ABI pointers.
/// No Marshal.GetIUnknownForObject, runtime CCWs or generated managed COM proxies are involved.
/// </summary>
internal static unsafe class ToastComActivator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public void** VTable;
        public int ReferenceCount;
        public nint HostHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInputData
    {
        public char* Key;
        public char* Value;
    }

    private static int _liveInstances;

    /// <summary>Native COM objects allocated by this class and not yet freed. Zero when nothing leaked.</summary>
    internal static int LiveInstances
    {
        get
        {
            return Volatile.Read(ref _liveInstances);
        }
    }

    private static readonly void** FactoryTable = AllocateFactoryTable();
    private static readonly void** CallbackTable = AllocateCallbackTable();

    private static void** AllocateFactoryTable()
    {
        void** p = (void**)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ToastComActivator), 5 * sizeof(void*));
        p[0] = (void*)(delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&FactoryQueryInterface;
        p[1] = (void*)(delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        p[2] = (void*)(delegate* unmanaged[Stdcall]<void*, uint>)&Release;
        p[3] = (void*)(delegate* unmanaged[Stdcall]<void*, void*, Guid*, void**, int>)&CreateInstance;
        p[4] = (void*)(delegate* unmanaged[Stdcall]<void*, int, int>)&LockServer;
        return p;
    }

    private static void** AllocateCallbackTable()
    {
        void** p = (void**)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ToastComActivator), 4 * sizeof(void*));
        p[0] = (void*)(delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&CallbackQueryInterface;
        p[1] = (void*)(delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        p[2] = (void*)(delegate* unmanaged[Stdcall]<void*, uint>)&Release;
        p[3] = (void*)(delegate* unmanaged[Stdcall]<void*, char*, char*, UserInputData*, uint, int>)&Activate;
        return p;
    }

    internal static void* CreateFactory(ToastHost host)
    {
        return Allocate(FactoryTable, host);
    }

    private static Instance* Allocate(void** table, ToastHost host)
    {
        Instance* instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        bool callbackRetained = false;
        try
        {
            if (table == CallbackTable)
            {
                host.RetainComCallback();
                callbackRetained = true;
            }
            instance->HostHandle = (nint)GCHandle.ToIntPtr(GCHandle.Alloc(host));
            instance->VTable = table;
            instance->ReferenceCount = 1;
            _ = Interlocked.Increment(ref _liveInstances);
            return instance;
        }
        catch
        {
            if (instance->HostHandle != 0)
            {
                GCHandle.FromIntPtr(instance->HostHandle).Free();
            }

            if (callbackRetained)
            {
                host.ReleaseComCallback();
            }

            NativeMemory.Free(instance);
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryQueryInterface(void* self, Guid* riid, void** output)
    {
        return QueryInterfaceCore(self, riid, output, Interop.IID_IClassFactory);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CallbackQueryInterface(void* self, Guid* riid, void** output)
    {
        return QueryInterfaceCore(self, riid, output, Interop.IID_INotificationActivationCallback);
    }

    private static int QueryInterfaceCore(void* self, Guid* riid, void** output, Guid ownInterface)
    {
        if (output == null)
        {
            return Interop.E_POINTER;
        }

        *output = null;
        if (riid == null)
        {
            return Interop.E_POINTER;
        }

        if (*riid != Interop.IID_IUnknown && *riid != ownInterface)
        {
            return Interop.E_NOINTERFACE;
        }

        _ = AddRefCore((Instance*)self);
        *output = self;
        return Interop.S_OK;
    }

    private static uint AddRefCore(Instance* self)
    {
        return (uint)Interlocked.Increment(ref self->ReferenceCount);
    }

    private static uint ReleaseCore(Instance* self)
    {
        int count = Interlocked.Decrement(ref self->ReferenceCount);
        if (count == 0)
        {
            var handle = GCHandle.FromIntPtr(self->HostHandle);
            if (self->VTable == CallbackTable)
            {
                ((ToastHost)handle.Target!).ReleaseComCallback();
            }

            handle.Free();
            NativeMemory.Free(self);
            _ = Interlocked.Decrement(ref _liveInstances);
        }
        return (uint)count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self)
    {
        return AddRefCore((Instance*)self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self)
    {
        return ReleaseCore((Instance*)self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateInstance(void* self, void* outer, Guid* iid, void** output)
    {
        if (output == null)
        {
            return Interop.E_POINTER;
        }

        *output = null;
        if (iid == null)
        {
            return Interop.E_POINTER;
        }

        if (outer != null)
        {
            return Interop.CLASS_E_NOAGGREGATION;
        }

        try
        {
            var host = (ToastHost)GCHandle.FromIntPtr((IntPtr)((Instance*)self)->HostHandle).Target!;
            Instance* callback = Allocate(CallbackTable, host);
            int hr = QueryInterfaceCore(callback, iid, output, Interop.IID_INotificationActivationCallback);
            _ = ReleaseCore(callback);
            return hr;
        }
        catch
        {
            return Interop.E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LockServer(void* self, int locked)
    {
        return Interop.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Activate(void* self, char* appId, char* invokedArgs, UserInputData* data, uint count)
    {
        try
        {
            // Never allow a managed exception across an unmanaged ABI boundary.
            if (count > 1024 || (count != 0 && data == null))
            {
                return Interop.E_FAIL;
            }

            var host = (ToastHost)GCHandle.FromIntPtr((IntPtr)((Instance*)self)->HostHandle).Target!;
            int budget = 1024 * 1024;
            string identity = ReadString(appId, 128, ref budget);
            if (!StringComparer.Ordinal.Equals(identity, host.AppId))
            {
                return Interop.E_FAIL;
            }

            string arguments = ReadString(invokedArgs, 32768, ref budget);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (uint i = 0; i < count; ++i)
            {
                var item = data[i];
                if (item.Key != null)
                {
                    values[ReadString(item.Key, 4096, ref budget)] = ReadString(item.Value, 65536, ref budget);
                }
            }
            host.DispatchActivation(new ToastActivation(identity, arguments, values));
            return Interop.S_OK;
        }
        catch
        {
            return Interop.E_FAIL;
        }
    }

    private static string ReadString(char* text, int limit, ref int budget)
    {
        if (text == null)
        {
            return "";
        }

        int length = 0;

        int maximum = Math.Min(limit, budget);
        while (length <= maximum && text[length] != '\0')
        {
            ++length;
        }

        if (length > maximum)
        {
            throw new ArgumentException("Activation data exceeds the allowed size.");
        }

        budget -= length;
        return new string(text, 0, length);
    }

    internal static void ReleaseFactory(void* factory)
    {
        _ = ReleaseCore((Instance*)factory);
    }
}
