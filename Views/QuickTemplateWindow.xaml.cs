using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipVoice.Models;
using ClipVoice.Native;

namespace ClipVoice.Views;

public partial class QuickTemplateWindow : Window
{
    private sealed class FolderChoice
    {
        public Guid? Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsNew { get; init; }
        public override string ToString() => Name;
    }

    private readonly string _content;
    private readonly IReadOnlyList<TemplateFolder> _folders;

    public Guid? SelectedFolderId { get; private set; }
    public string NewFolderName { get; private set; } = string.Empty;
    public string TemplateName { get; private set; } = string.Empty;

    public QuickTemplateWindow(string content, IEnumerable<TemplateFolder> folders)
    {
        InitializeComponent();
        _content = content ?? string.Empty;
        _folders = folders.ToList();

        PreviewText.Text = _content;
        NameBox.Text = BuildDefaultName(_content);

        foreach (var folder in _folders)
            FolderList.Items.Add(new FolderChoice { Id = folder.Id, Name = folder.Name });
        FolderList.Items.Add(new FolderChoice { Name = "+ Create a new folder…", IsNew = true });
        FolderList.SelectedIndex = _folders.Count > 0 ? 0 : FolderList.Items.Count - 1;

        Loaded += (_, _) =>
        {
            // This dialog is opened from a no-activate popup. Windows can therefore keep
            // the browser/editor as the foreground app. Explicitly take foreground focus
            // for this short modal editor so it never appears behind other windows.
            BringDialogToFront();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Activate();
                NameBox.Focus();
                Keyboard.Focus(NameBox);
                NameBox.SelectAll();
            }), DispatcherPriority.Input);
        };
    }

    private void BringDialogToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero)
            : 0;
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static string BuildDefaultName(string text)
    {
        var line = (text ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        while (line.Contains("  ")) line = line.Replace("  ", " ");
        if (line.Length > 42) line = line[..42].TrimEnd() + "…";
        return string.IsNullOrWhiteSpace(line) ? "New template" : line;
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isNew = FolderList.SelectedItem is FolderChoice choice && choice.IsNew;
        NewFolderPanel.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        if (isNew)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                NewFolderBox.Focus();
                NewFolderBox.SelectAll();
            }));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "Enter a template name.";
            NameBox.Focus();
            return;
        }

        if (FolderList.SelectedItem is not FolderChoice choice)
        {
            ErrorText.Text = "Select a folder.";
            return;
        }

        if (choice.IsNew)
        {
            var folderName = NewFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folderName))
            {
                ErrorText.Text = "Enter a name for the new folder.";
                NewFolderBox.Focus();
                return;
            }
            if (_folders.Any(x => string.Equals(x.Name, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                ErrorText.Text = "A folder with this name already exists. Select it from the list above.";
                NewFolderBox.Focus();
                return;
            }
            NewFolderName = folderName;
            SelectedFolderId = null;
        }
        else
        {
            SelectedFolderId = choice.Id;
            NewFolderName = string.Empty;
        }

        TemplateName = name;
        DialogResult = true;
        Close();
    }
}
