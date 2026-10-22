using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipVoice.Models;
using ClipVoice.Native;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class TemplatePopupWindow : Window
{
    private const int FolderBatchSize = 20;
    private readonly IReadOnlyList<TemplateFolder> _folders;
    private readonly IReadOnlyList<ClipboardItem> _clipboardItems;
    private readonly InputTarget _target;
    private readonly int _screenX;
    private readonly int _screenY;
    private readonly Func<TextTemplate, InputTarget, Task> _insertAsync;
    private readonly Func<ClipboardItem, InputTarget, Task> _pasteClipboardAsync;
    private readonly Action _openSettings;
    private readonly Func<InputTarget, Task> _createFromSelectionAsync;
    private readonly Func<InputTarget, Task> _speakSelectionAsync;
    private readonly Func<InputTarget, Task> _translateSpeakSelectionAsync;
    private readonly Func<InputTarget, Task> _translateSelectionAsync;
    private TemplateFolderPopupWindow? _folderPopup;
    private ClipboardHistoryPopupWindow? _clipboardPopup;
    private Guid? _openFolderId;
    private bool _clipboardOpen;
    private bool _closing;
    private int _nextFolderIndex;

    public TemplatePopupWindow(
        IEnumerable<TemplateFolder> folders,
        IEnumerable<ClipboardItem> clipboardItems,
        InputTarget target,
        int screenX,
        int screenY,
        Func<TextTemplate, InputTarget, Task> insertAsync,
        Func<ClipboardItem, InputTarget, Task> pasteClipboardAsync,
        Action openSettings,
        Func<InputTarget, Task> createFromSelectionAsync,
        Func<InputTarget, Task> speakSelectionAsync,
        Func<InputTarget, Task> translateSpeakSelectionAsync,
        Func<InputTarget, Task> translateSelectionAsync)
    {
        InitializeComponent();
        LocalizationService.Apply(this, ((App)Application.Current).Controller.Settings.Language);
        _folders = folders as IReadOnlyList<TemplateFolder> ?? folders.ToList();
        _clipboardItems = clipboardItems as IReadOnlyList<ClipboardItem> ?? clipboardItems.ToList();
        _target = target;
        _screenX = screenX;
        _screenY = screenY;
        _insertAsync = insertAsync;
        _pasteClipboardAsync = pasteClipboardAsync;
        _openSettings = openSettings;
        _createFromSelectionAsync = createFromSelectionAsync;
        _speakSelectionAsync = speakSelectionAsync;
        _translateSpeakSelectionAsync = translateSpeakSelectionAsync;
        _translateSelectionAsync = translateSelectionAsync;

        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        Loaded += (_, _) =>
        {
            BuildItems();
            Dispatcher.BeginInvoke(new Action(PlaceAtCursor));
        };
        Closed += (_, _) => CloseSubmenus();
    }

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void BuildItems()
    {
        ItemsHost.Children.Clear();
        _nextFolderIndex = 0;
        ItemsHost.Children.Add(CreateClipboardButton());
        AddNextFolderBatch();
    }

    private void AddNextFolderBatch()
    {
        var added = 0;
        while (_nextFolderIndex < _folders.Count && added < FolderBatchSize)
        {
            var folder = _folders[_nextFolderIndex++];
            if (folder.Templates.Count == 0) continue;
            ItemsHost.Children.Add(CreateFolderButton(folder));
            added++;
        }

        if (_nextFolderIndex >= _folders.Count && ItemsHost.Children.Count == 1)
        {
            ItemsHost.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("No templates yet", ((App)Application.Current).Controller.Settings.Language),
                Foreground = new SolidColorBrush(Color.FromRgb(164, 171, 188)),
                Margin = new Thickness(11, 12, 11, 12),
                FontSize = 12
            });
        }
    }

    private void ItemsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || _nextFolderIndex >= _folders.Count) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 64)
            AddNextFolderBatch();
    }

    private Button CreateClipboardButton()
    {
        var button = new Button
        {
            Style = (Style)FindResource("PopupFolderButton")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate("Clipboard", ((App)Application.Current).Controller.Settings.Language),
            Foreground = new SolidColorBrush(Color.FromRgb(244, 245, 248)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var arrow = new TextBlock
        {
            Text = "›",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 141, 255)),
            FontSize = 18,
            Margin = new Thickness(12, -2, 1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);

        button.Content = grid;
        button.MouseEnter += (_, _) => OpenClipboardPopup(button);
        button.Click += (_, _) => OpenClipboardPopup(button);
        return button;
    }

    private void OpenClipboardPopup(FrameworkElement anchor)
    {
        if (_closing) return;
        if (_clipboardOpen && _clipboardPopup is { IsVisible: true }) return;

        CloseSubmenus();
        var topRight = anchor.PointToScreen(new Point(anchor.ActualWidth - 1, 0));
        var topLeft = anchor.PointToScreen(new Point(1, 0));
        var popup = new ClipboardHistoryPopupWindow(
            _clipboardItems.ToList(),
            _target,
            (int)Math.Round(topRight.X),
            (int)Math.Round(topRight.Y),
            (int)Math.Round(topLeft.X),
            HandleClipboardItemSelectedAsync);

        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_clipboardPopup, popup))
            {
                _clipboardPopup = null;
                _clipboardOpen = false;
            }
        };
        _clipboardPopup = popup;
        _clipboardOpen = true;
        popup.Show();
    }

    private Button CreateFolderButton(TemplateFolder folder)
    {
        var button = new Button
        {
            Style = (Style)FindResource("PopupFolderButton"),
            Tag = folder
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = folder.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 245, 248)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        grid.Children.Add(name);

        var arrow = new TextBlock
        {
            Text = "›",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 141, 255)),
            FontSize = 18,
            Margin = new Thickness(12, -2, 1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);

        button.Content = grid;
        button.MouseEnter += (_, _) => OpenFolderPopup(folder, button);
        button.Click += (_, _) => OpenFolderPopup(folder, button);
        return button;
    }

    private void OpenFolderPopup(TemplateFolder folder, FrameworkElement anchor)
    {
        if (_closing) return;
        if (_openFolderId == folder.Id && _folderPopup is { IsVisible: true }) return;

        CloseSubmenus();

        var topRight = anchor.PointToScreen(new Point(anchor.ActualWidth - 1, 0));
        var topLeft = anchor.PointToScreen(new Point(1, 0));

        var popup = new TemplateFolderPopupWindow(
            folder,
            _target,
            (int)Math.Round(topRight.X),
            (int)Math.Round(topRight.Y),
            (int)Math.Round(topLeft.X),
            HandleTemplateSelectedAsync);

        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_folderPopup, popup))
            {
                _folderPopup = null;
                _openFolderId = null;
            }
        };

        _folderPopup = popup;
        _openFolderId = folder.Id;
        popup.Show();
    }

    private async Task HandleTemplateSelectedAsync(TextTemplate template, InputTarget target)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        await Task.Delay(25);
        await _insertAsync(template, target);
    }

    private async Task HandleClipboardItemSelectedAsync(ClipboardItem item, InputTarget target)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        await Task.Delay(25);
        await _pasteClipboardAsync(item, target);
    }

    private void CloseSubmenus()
    {
        var folderPopup = _folderPopup;
        var clipboardPopup = _clipboardPopup;
        _folderPopup = null;
        _clipboardPopup = null;
        _openFolderId = null;
        _clipboardOpen = false;
        folderPopup?.Dismiss();
        clipboardPopup?.Dismiss();
    }

    private void Footer_MouseEnter(object sender, MouseEventArgs e) => CloseSubmenus();


    private async void CreateFromSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        await Task.Delay(35);
        await _createFromSelectionAsync(_target);
    }

    private async void SpeakSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        await Task.Delay(35);
        await _speakSelectionAsync(_target);
    }

    private async void TranslateSpeakSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        await Task.Delay(35);
        await _translateSpeakSelectionAsync(_target);
    }

    private async void TranslateSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        await Task.Delay(35);
        await _translateSelectionAsync(_target);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
        Dispatcher.BeginInvoke(_openSettings);
    }

    private void PlaceAtCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.GetWindowRect(hwnd, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_screenX, _screenY));
        var area = screen.WorkingArea;

        var left = _screenX + 10;
        var top = _screenY + 10;
        if (left + width > area.Right - 8) left = Math.Max(area.Left + 8, _screenX - width - 10);
        if (top + height > area.Bottom - 8) top = Math.Max(area.Top + 8, area.Bottom - height - 8);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        RevealAfterPositioning();
    }

    private void RevealAfterPositioning()
    {
        IsHitTestVisible = true;
        Opacity = 1;
    }

    private static bool WindowContainsPoint(Window? window, int x, int y)
    {
        if (window is null || !window.IsVisible) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    public bool ContainsScreenPoint(int x, int y)
        => WindowContainsPoint(this, x, y) || WindowContainsPoint(_folderPopup, x, y) || WindowContainsPoint(_clipboardPopup, x, y);

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        CloseSubmenus();
        Close();
    }
}
