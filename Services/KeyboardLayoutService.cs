using ClipVoice.Native;

namespace ClipVoice.Services;

public static class KeyboardLayoutService
{
    public static string GetTranslationTarget()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        var hkl = NativeMethods.GetKeyboardLayout(threadId);
        var langId = (ushort)((long)hkl & 0xFFFF);
        return langId == 0x0419 ? "ru" : "en";
    }
}
