using System.Windows.Input;
using ClipVoice.Native;

namespace ClipVoice.Services;

public static class HotkeyParser
{
    public static bool TryParse(string? value, out uint modifiers, out uint virtualKey, out string normalized, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Комбинация не задана";
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Key key = Key.None;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.MOD_CONTROL;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.MOD_ALT;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.MOD_SHIFT;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.MOD_WIN;
            else
            {
                if (key != Key.None)
                {
                    error = "В комбинации должна быть одна основная клавиша";
                    return false;
                }
                if (!TryParseKey(part, out key))
                {
                    error = $"Неизвестная клавиша: {part}";
                    return false;
                }
            }
        }

        if (key == Key.None)
        {
            error = "Нужна основная клавиша";
            return false;
        }
        if (modifiers == 0)
        {
            error = "Добавь Ctrl, Alt, Shift или Win, чтобы не перехватывать обычную клавишу во всей системе";
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = "Эту клавишу нельзя использовать как глобальную";
            return false;
        }
        normalized = Format(modifiers, key);
        return true;
    }

    public static string FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key)) return string.Empty;

        uint nativeModifiers = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) nativeModifiers |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) nativeModifiers |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) nativeModifiers |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) nativeModifiers |= NativeMethods.MOD_WIN;
        if (nativeModifiers == 0) return string.Empty;
        return Format(nativeModifiers, key);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static bool TryParseKey(string value, out Key key)
    {
        key = Key.None;
        if (value.Length == 1 && char.IsLetter(value[0])) return Enum.TryParse(value.ToUpperInvariant(), out key);
        if (value.Length == 1 && char.IsDigit(value[0]))
        {
            key = (Key)((int)Key.D0 + (value[0] - '0'));
            return true;
        }
        return Enum.TryParse(value, true, out key) && !IsModifierKey(key) && key != Key.None;
    }

    private static string Format(uint modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        return key.ToString();
    }
}
