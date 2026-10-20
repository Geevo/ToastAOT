using System.Runtime.InteropServices;

namespace ToastAOT;

/// <summary>Only OS-supplied DLLs. No RCW/CCW or runtime COM marshalling.</summary>
internal static unsafe partial class Interop
{
    internal const int S_OK = 0;
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    internal const uint CLSCTX_INPROC_SERVER = 1;
    internal const uint CLSCTX_LOCAL_SERVER = 4;
    internal const uint REGCLS_MULTIPLEUSE = 1;

    internal static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");
    internal static readonly Guid IID_INotificationActivationCallback = new("53E31837-6600-4A81-9395-75CFFE746F94");
    internal static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    internal static readonly Guid IID_IPersistFile = new("0000010B-0000-0000-C000-000000000046");
    internal static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    internal static readonly Guid IID_IXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    internal static readonly Guid IID_IXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    internal static readonly Guid IID_IToastManager = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    internal static readonly Guid IID_IToastManager2 = new("7AB93C52-0E48-4750-BA9D-1A4113981847");
    internal static readonly Guid IID_IToastFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    internal static readonly Guid IID_IToastNotification2 = new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");
    internal static readonly Guid IID_IToastNotification4 = new("15154935-28EA-4727-88E9-C58680E2D118");
    internal static readonly Guid IID_IToastNotification6 = new("43EBFE53-89AE-5C1E-A279-3AECFE9B6F54");
    internal static readonly Guid IID_INotificationData = new("9FFD2312-9D6A-4AAF-B6AC-FF17F0C1F280");
    internal static readonly Guid IID_IToastNotifier2 = new("354389C6-7C01-4BD5-9C20-604340CD2B74");
    internal static readonly Guid IID_IToastHistory2 = new("3BC3D253-2F31-4092-9129-8AD5ABF067DA");
    internal static readonly Guid IID_IXmlNodeSerializer = new("5CC5B382-E6DD-4991-ABEF-06D8D2E7BD0C");
    internal static readonly Guid IID_IPropertyValueStatics = new("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");
    internal static readonly Guid IID_IReferenceDateTime = new("5541D8A7-497C-5AA4-86FC-7713ADBF2A2C");

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CoRegisterClassObject(Guid* clsid, void* factory, uint clsctx, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    internal static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out void* result);

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(void* pvar);

    [LibraryImport("combase.dll")]
    internal static partial int RoInitialize(uint initType);

    [LibraryImport("combase.dll")]
    internal static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(nint classId, Guid* iid, out void* result);

    [LibraryImport("combase.dll")]
    internal static partial int RoActivateInstance(nint classId, out void* instance);

    [LibraryImport("combase.dll")]
    internal static partial int WindowsCreateString(char* source, uint length, out nint result);

    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    internal static partial char* WindowsGetStringRawBuffer(nint value, out uint length);

    [LibraryImport("kernel32.dll")]
    internal static partial int GetCurrentPackageFullName(ref uint length, char* name);

    internal static string ReadHString(nint value)
    {
        char* buffer = WindowsGetStringRawBuffer(value, out uint length);
        return length == 0 ? "" : new string(buffer, 0, checked((int)length));
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static void* VTableEntry(void* obj, int index)
    {
        return (*(void***)obj)[index];
    }

    internal static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    internal static void* QueryInterface(void* obj, Guid iid)
    {
        void* result = null;
        Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTableEntry(obj, 0))(obj, &iid, &result));
        return result;
    }

    internal static void Release(void* obj)
    {
        if (obj != null)
        {
            _ = ((delegate* unmanaged[Stdcall]<void*, uint>)VTableEntry(obj, 2))(obj);
        }
    }

    internal static nint HString(string text)
    {
        fixed (char* p = text)
        {
            Check(WindowsCreateString(p, (uint)text.Length, out nint result));
            return result;
        }
    }
}
