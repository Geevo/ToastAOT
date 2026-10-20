using Microsoft.Win32;

namespace ToastAOT;

/// <summary>
/// Diagnoses a process whose HKCU\Software\Classes writes never reach the COM service control manager.
/// This happens when a process runs inside a packaged (MSIX) host's registry virtualisation: the process
/// reads back what it wrote, but SCM cannot see it, so cold-start activation silently does nothing.
/// </summary>
internal static unsafe class ColdStartProbe
{
    internal const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    /// <summary>
    /// Registers a throw-away LocalServer32 CLSID that points at a harmless system executable which never registers
    /// the class, and asks SCM to activate it. SCM reporting "class not registered" for a key this process just wrote
    /// means SCM cannot see this process's HKCU\Software\Classes writes. Any other result (SCM launched the program
    /// and reported a server failure or timeout) means it can. SCM may wait several seconds (about 30 s was observed)
    /// when it can see the key. A missing executable is not usable: SCM then reports "class not registered" even when
    /// it can see the key. The temporary key is always removed.
    /// </summary>
    internal static bool IsClassRegistrationVisibleToServiceControl(out int activationResult)
    {
        if (!OperatingSystem.IsWindows())
        {
            activationResult = 0;
            return true;
        }

        Guid clsid = Guid.NewGuid();
        string path = $@"Software\Classes\CLSID\{{{clsid:D}}}";
        string server = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path + @"\LocalServer32", writable: true))
            {
                key.SetValue("", "\"" + server + "\"");
            }

            int init = Interop.CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
            try
            {
                Guid iid = Interop.IID_IUnknown;
                activationResult = Interop.CoCreateInstance(&clsid, 0, Interop.CLSCTX_LOCAL_SERVER, &iid, out void* instance);
                Interop.Release(instance);
            }
            finally
            {
                if (init >= 0)
                {
                    Interop.CoUninitialize();
                }
            }

            return activationResult != REGDB_E_CLASSNOTREG;
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
