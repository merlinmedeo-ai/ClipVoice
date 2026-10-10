using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using ClipVoice.Native;

namespace ClipVoice.Services;

public readonly record struct InputTarget(IntPtr Window, IntPtr FocusWindow)
{
    public bool IsValid => Window != IntPtr.Zero;
}

public sealed record HotkeyRegistrationError(string Action, string Hotkey, string Reason);

public sealed class SystemIntegrationService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _hotkeys = new();
    private readonly NativeMethods.HookProc _mouseHookProc;
    private IntPtr _mouseHook;
    private bool _suppressAltRightButtonUp;

    public event Action? ClipboardChanged;
    public event Action<string>? HotkeyPressed;
    public event Action<InputTarget, int, int>? TemplateMenuRequested;
    public event Action<int, int>? GlobalMouseDown;

    public bool TemplateMouseHookAvailable => _mouseHook != IntPtr.Zero;
    public bool TemplateMouseGestureEnabled { get; set; } = true;
    public string TemplateMouseGestureModifier { get; set; } = "Alt";

    public SystemIntegrationService()
    {
        var p = new HwndSourceParameters("ClipVoice.NativeMessageWindow")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000)
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(_source.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не разрешила подписаться на изменения буфера обмена.");

        _mouseHookProc = MouseHookProc;
        try
        {
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseHookProc, NativeMethods.GetModuleHandle(null), 0);
        }
        catch
        {
            _mouseHook = IntPtr.Zero;
        }
    }

    public IReadOnlyList<HotkeyRegistrationError> RegisterHotkeys(IReadOnlyDictionary<string, string> actions)
    {
        ClearHotkeys();
        var errors = new List<HotkeyRegistrationError>();
        var id = 100;
        foreach (var pair in actions)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            if (!HotkeyParser.TryParse(pair.Value, out var modifiers, out var virtualKey, out _, out var parseError))
            {
                errors.Add(new HotkeyRegistrationError(pair.Key, pair.Value, parseError));
                continue;
            }
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                errors.Add(new HotkeyRegistrationError(pair.Key, pair.Value, code == 1409 ? "сочетание уже занято другой программой" : $"Windows отклонила сочетание (код {code})"));
            }
            else
            {
                _hotkeys[id] = pair.Key;
                id++;
            }
        }
        return errors;
    }

    public IntPtr ForegroundWindow => NativeMethods.GetForegroundWindow();
    public uint ClipboardSequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public InputTarget CaptureForegroundInputTarget()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return default;

        var threadId = NativeMethods.GetWindowThreadProcessId(window, IntPtr.Zero);
        var info = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        var focus = threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
        return new InputTarget(window, focus);
    }

    public string DescribeInputTarget(InputTarget target)
        => $"valid={target.IsValid}; window_class={GetWindowClassName(target.Window)}; focus_class={GetWindowClassName(target.FocusWindow)}; window=0x{target.Window.ToInt64():X}; focus=0x{target.FocusWindow.ToInt64():X}";

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return "none";
        var name = new StringBuilder(128);
        return NativeMethods.GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "unknown";
    }

    public async Task<bool> SendCopyShortcutAsync(CancellationToken ct = default)
    {
        if (!await WaitForModifiersReleasedAsync(ct, 2500)) return false;
        return SendCtrlKey(NativeMethods.VK_C);
    }

    public async Task<bool> SendCopyShortcutAsync(InputTarget target, CancellationToken ct = default)
    {
        if (!target.IsValid || !NativeMethods.IsWindow(target.Window)) return false;
        if (!await EnsureInputTargetAsync(target, ct))
            return SendTargetEditCommand(target, NativeMethods.WM_COPY);
        if (await WaitForModifiersReleasedAsync(ct, 1200) && SendCtrlKey(NativeMethods.VK_C)) return true;

        // Some browser and Electron edit surfaces ignore injected key events. WM_COPY
        // targets the focused renderer control directly and also works while Alt remains
        // pressed for the template mouse gesture.
        return SendTargetEditCommand(target, NativeMethods.WM_COPY);
    }

    public bool SendCopyMessage(InputTarget target)
        => target.IsValid && SendTargetEditCommand(target, NativeMethods.WM_COPY);

    public bool TryReadSelectedText(InputTarget target, out string text)
    {
        text = string.Empty;
        var focusWindow = target.FocusWindow;
        if (focusWindow == IntPtr.Zero || !NativeMethods.IsWindow(focusWindow)) return false;

        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(focusWindow, className, className.Capacity) <= 0) return false;
        var name = className.ToString();
        if (!name.Contains("Edit", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;

        var startPtr = Marshal.AllocHGlobal(sizeof(int));
        var endPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(startPtr, 0);
            Marshal.WriteInt32(endPtr, 0);
            NativeMethods.SendMessage(focusWindow, NativeMethods.EM_GETSEL, startPtr, endPtr);
            var start = Marshal.ReadInt32(startPtr);
            var end = Marshal.ReadInt32(endPtr);
            if (end <= start) return false;

            var length = NativeMethods.SendMessage(focusWindow, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (length <= 0 || start < 0 || start >= length) return false;
            var buffer = new StringBuilder(length + 1);
            NativeMethods.SendMessage(focusWindow, NativeMethods.WM_GETTEXT, new IntPtr(buffer.Capacity), buffer);
            var all = buffer.ToString();
            end = Math.Min(end, all.Length);
            if (end <= start) return false;
            text = all[start..end];
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            text = string.Empty;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(startPtr);
            Marshal.FreeHGlobal(endPtr);
        }
    }

    public bool IsBrowserTarget(InputTarget target)
    {
        static string ClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return string.Empty;
            var name = new StringBuilder(128);
            return NativeMethods.GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
        }

        var topLevelClass = ClassName(target.Window);
        var focusClass = ClassName(target.FocusWindow);
        return topLevelClass.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
            || focusClass.StartsWith("Chrome_RenderWidgetHost", StringComparison.OrdinalIgnoreCase)
            || topLevelClass.Contains("MozillaWindowClass", StringComparison.OrdinalIgnoreCase)
            || focusClass.Contains("MozillaWindowClass", StringComparison.OrdinalIgnoreCase)
            || topLevelClass.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> SendPasteShortcutAsync(IntPtr targetWindow, CancellationToken ct = default)
    {
        if (!await WaitForModifiersReleasedAsync(ct, 3500)) return false;
        if (targetWindow != IntPtr.Zero && NativeMethods.IsWindow(targetWindow) && NativeMethods.GetForegroundWindow() != targetWindow)
        {
            NativeMethods.SetForegroundWindow(targetWindow);
            await Task.Delay(90, ct);
        }
        return SendCtrlKey(NativeMethods.VK_V);
    }

    public async Task<bool> SendPasteShortcutAsync(InputTarget target, CancellationToken ct = default)
    {
        if (!target.IsValid || !NativeMethods.IsWindow(target.Window)) return false;
        if (!await EnsureInputTargetAsync(target, ct))
            return SendTargetEditCommand(target, NativeMethods.WM_PASTE);
        if (await WaitForModifiersReleasedAsync(ct, 1200) && SendCtrlKey(NativeMethods.VK_V)) return true;

        // Fallback for Chromium/Electron text controls when Ctrl+V injection is ignored.
        return SendTargetEditCommand(target, NativeMethods.WM_PASTE);
    }

    private static bool SendTargetEditCommand(InputTarget target, uint message)
    {
        var hwnd = target.FocusWindow != IntPtr.Zero && NativeMethods.IsWindow(target.FocusWindow)
            ? target.FocusWindow
            : target.Window;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        try
        {
            NativeMethods.SendMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    public Task<bool> ActivateInputTargetAsync(InputTarget target, CancellationToken ct = default)
        => EnsureInputTargetAsync(target, ct);

    public async Task<bool> SendUnicodeTextAsync(InputTarget target, string text, CancellationToken ct = default)
    {
        if (!target.IsValid || !NativeMethods.IsWindow(target.Window)) return false;
        if (string.IsNullOrEmpty(text)) return true;

        // Alt/Ctrl/Shift from the gesture or template hotkey must be physically released first.
        // SendInput does not reset keyboard state, so injecting while Alt is still down can make
        // the target application treat the text as menu commands instead of typing it.
        if (!await WaitForModifiersReleasedAsync(ct, 5000)) return false;

        if (!await EnsureInputTargetAsync(target, ct)) return false;

        // Standard Win32/RichEdit controls (including current Notepad builds) can accept the
        // replacement directly at the caret. This is faster and does not touch the clipboard.
        if (TryDirectEditInsert(target.FocusWindow, text)) return true;

        const int charsPerChunk = 128;
        for (var offset = 0; offset < text.Length; offset += charsPerChunk)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(charsPerChunk, text.Length - offset);
            var inputs = new NativeMethods.INPUT[length * 2];
            var inputIndex = 0;
            for (var i = 0; i < length; i++)
            {
                var ch = text[offset + i];
                inputs[inputIndex++] = UnicodeInput(ch, false);
                inputs[inputIndex++] = UnicodeInput(ch, true);
            }

            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent != (uint)inputs.Length) return false;
            if (offset + length < text.Length) await Task.Delay(2, ct);
        }
        return true;
    }


    private static bool TryDirectEditInsert(IntPtr focusWindow, string text)
    {
        if (focusWindow == IntPtr.Zero || !NativeMethods.IsWindow(focusWindow)) return false;
        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(focusWindow, className, className.Capacity) <= 0) return false;
        var name = className.ToString();
        if (!name.Contains("Edit", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            NativeMethods.SendMessage(focusWindow, NativeMethods.EM_REPLACESEL, new IntPtr(1), text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> EnsureInputTargetAsync(InputTarget target, CancellationToken ct)
    {
        if (!NativeMethods.IsWindow(target.Window)) return false;

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == target.Window) return true;

        var targetThread = NativeMethods.GetWindowThreadProcessId(target.Window, IntPtr.Zero);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = false;

        try
        {
            if (targetThread != 0 && currentThread != targetThread)
                attached = NativeMethods.AttachThreadInput(currentThread, targetThread, true);

            // Restore the owning top-level window. Do not force SetFocus to a saved child
            // HWND: browsers keep their own focus state inside renderer widgets.
            NativeMethods.SetForegroundWindow(target.Window);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }

        await Task.Delay(45, ct);
        return NativeMethods.GetForegroundWindow() == target.Window;
    }

    private static async Task<bool> WaitForModifiersReleasedAsync(CancellationToken ct, int timeoutMs)
    {
        var loops = Math.Max(1, timeoutMs / 20);
        for (var i = 0; i < loops; i++)
        {
            if (!IsDown(NativeMethods.VK_CONTROL) && !IsDown(NativeMethods.VK_SHIFT) && !IsDown(NativeMethods.VK_MENU) &&
                !IsDown(NativeMethods.VK_LWIN) && !IsDown(NativeMethods.VK_RWIN)) return true;
            await Task.Delay(20, ct);
        }
        return false;
    }

    private bool IsTemplateMouseModifierPressed()
    {
        var ctrl = IsDown(NativeMethods.VK_CONTROL);
        var shift = IsDown(NativeMethods.VK_SHIFT);
        var alt = IsDown(NativeMethods.VK_MENU);
        var win = IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN);

        // Match the configured gesture exactly so Ctrl+ПКМ does not also fire while
        // Alt/Shift/Win are held for another application shortcut.
        var parts = (TemplateMouseGestureModifier ?? "Alt")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wantCtrl = parts.Any(x => x.Equals("Ctrl", StringComparison.OrdinalIgnoreCase));
        var wantShift = parts.Any(x => x.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var wantAlt = parts.Any(x => x.Equals("Alt", StringComparison.OrdinalIgnoreCase));

        return ctrl == wantCtrl && shift == wantShift && alt == wantAlt && !win;
    }

    private static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool SendCtrlKey(ushort key)
    {
        var inputs = new[]
        {
            KeyboardInput(NativeMethods.VK_CONTROL, false),
            KeyboardInput(key, false),
            KeyboardInput(key, true),
            KeyboardInput(NativeMethods.VK_CONTROL, true)
        };
        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == (uint)inputs.Length;
    }

    private static NativeMethods.INPUT KeyboardInput(ushort key, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = key, dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0 }
        }
    };

    private static NativeMethods.INPUT UnicodeInput(char ch, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0)
            }
        }
    };

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var isMouseDown = message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_RBUTTONDOWN ||
                              message == NativeMethods.WM_MBUTTONDOWN || message == NativeMethods.WM_XBUTTONDOWN;

            if (message == NativeMethods.WM_RBUTTONDOWN && TemplateMouseGestureEnabled && IsTemplateMouseModifierPressed())
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var target = CaptureForegroundInputTarget();
                _suppressAltRightButtonUp = true;

                // Leave the hook immediately. The popup is created on the WPF dispatcher.
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => TemplateMenuRequested?.Invoke(target, info.pt.X, info.pt.Y)));
                return new IntPtr(1);
            }

            if (message == NativeMethods.WM_RBUTTONUP && _suppressAltRightButtonUp)
            {
                _suppressAltRightButtonUp = false;
                return new IntPtr(1);
            }

            if (isMouseDown && GlobalMouseDown is not null)
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => GlobalMouseDown?.Invoke(info.pt.X, info.pt.Y)));
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
            handled = true;
        }
        else if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_hotkeys.TryGetValue(id, out var action)) HotkeyPressed?.Invoke(action);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ClearHotkeys()
    {
        foreach (var id in _hotkeys.Keys.ToList()) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _hotkeys.Clear();
    }

    public void Dispose()
    {
        ClearHotkeys();
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
