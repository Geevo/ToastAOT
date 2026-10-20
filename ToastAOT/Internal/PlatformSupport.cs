using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ToastAOT;

internal static unsafe partial class PlatformSupport
{
    internal static bool IsSupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return false;
        }

        uint length = 0;
        if (Interop.GetCurrentPackageFullName(ref length, null) != 15700)
        {
            return false;
        }

        if (OpenProcessToken(GetCurrentProcess(), 8 /* TOKEN_QUERY */, out var token) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            if (GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, sizeof(int), out _) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return elevated == 0;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetTokenInformation(SafeAccessTokenHandle token, uint informationClass, out int information, int length, out uint returnedLength);
}
