using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using ClipVoice.Models;

namespace ClipVoice.Services;

public static class ClipboardService
{
    private const int Attempts = 8;
    private const int DelayMs = 20;

    public static ClipboardItem? ReadCurrentItem()
    {
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text)) return new ClipboardItem { Kind = ClipboardItemKind.Text, Text = text };
                }
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                    if (files.Count > 0) return new ClipboardItem { Kind = ClipboardItemKind.Files, Files = files };
                }
                return null;
            }
            catch (COMException) { Thread.Sleep(DelayMs); }
            catch (ExternalException) { Thread.Sleep(DelayMs); }
        }
        return null;
    }


    public static bool HasAnyData()
    {
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                return data is not null && data.GetFormats(false).Length > 0;
            }
            catch (COMException) { Thread.Sleep(DelayMs); }
            catch (ExternalException) { Thread.Sleep(DelayMs); }
        }
        return false;
    }

    public static void Clear() => ExecuteWithRetry(Clipboard.Clear);

    public static string ReadText()
    {
        for (var i = 0; i < Attempts; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
            catch (COMException) { Thread.Sleep(DelayMs); }
            catch (ExternalException) { Thread.Sleep(DelayMs); }
        }
        return string.Empty;
    }

    public static void SetText(string text, bool excludeFromSystemHistory = false)
    {
        ExecuteWithRetry(() =>
        {
            if (!excludeFromSystemHistory)
            {
                Clipboard.SetDataObject(text ?? string.Empty, true);
                return;
            }

            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text ?? string.Empty);
            // Windows recognizes this registered clipboard format by name. Any payload
            // suppresses Clipboard History and Cloud Clipboard processing.
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 1 });
            Clipboard.SetDataObject(data, true);
        });
    }

    public static void SetFiles(IEnumerable<string> files)
    {
        var list = new StringCollection();
        list.AddRange(files.ToArray());
        ExecuteWithRetry(() => Clipboard.SetFileDropList(list));
    }

    private static void ExecuteWithRetry(Action action)
    {
        Exception? last = null;
        for (var i = 0; i < Attempts; i++)
        {
            try { action(); return; }
            catch (COMException ex) { last = ex; Thread.Sleep(DelayMs); }
            catch (ExternalException ex) { last = ex; Thread.Sleep(DelayMs); }
        }
        throw last ?? new InvalidOperationException("Не удалось получить доступ к буферу обмена.");
    }
}
