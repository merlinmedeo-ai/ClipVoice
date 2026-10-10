using System.ComponentModel;
using System.Runtime.InteropServices;
using ClipVoice.Native;

namespace ClipVoice.Services;

internal static class DpapiService
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] data) => Transform(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (data.Length == 0) return Array.Empty<byte>();
        var inputPtr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, inputPtr, data.Length);
            var input = new NativeMethods.DATA_BLOB { cbData = data.Length, pbData = inputPtr };
            NativeMethods.DATA_BLOB output;
            var ok = protect
                ? NativeMethods.CryptProtectData(ref input, "ClipVoice", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : NativeMethods.CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { NativeMethods.LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(inputPtr); }
    }
}
