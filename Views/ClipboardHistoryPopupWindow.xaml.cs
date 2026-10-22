using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ClipVoice.Models;
using ClipVoice.Native;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class ClipboardHistoryPopupWindow : Window
{
    private readonly IReadOnlyList<ClipboardItem> _items;
    private readonly InputTarget _target;
    private readonly int _anchorRightX;
    private readonly int _anchorTopY;
    private readonly int _anchorLeftX;
    private readonly Func<ClipboardItem, InputTarget, Task> _selectAsync;
    private bool _closing;

    public ClipboardHistoryPopupWindow(
        IEnumerable<ClipboardItem> items,
        InputTarget target,
        int anchorRightX,
        int anchorTopY,
        int anchorLeftX,
        Func<ClipboardItem, InputTarget, Task> selectAsync)
    {
        InitializeComponent();
        LocalizationService.Apply(this, ((App)Application.Current).Controller.Settings.Language);
        _items = items.ToList();
        _target = target;
        _anchorRightX = anchorRightX;
        _anchorTopY = anchorTopY;
        _anchorLeftX = anchorLeftX;
        _selectAsync = selectAsync;

        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        Loaded += (_, _) =>
        {
            BuildItems();
            Dispatcher.BeginInvoke(new Action(PlaceNextToAnchor));
        };
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
        if (_items.Count == 0)
        {
            ItemsHost.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("No clipboard items", ((App)Application.Current).Controller.Settings.Language),
                Foreground = new SolidColorBrush(Color.FromRgb(164, 171, 188)),
                Margin = new Thickness(11, 12, 11, 12),
                FontSize = 12
            });
            return;
        }

        foreach (var item in _items)
            ItemsHost.Children.Add(CreateItemButton(item));
    }

    private Button CreateItemButton(ClipboardItem item)
    {
        var button = new Button { Style = (Style)FindResource("ClipboardItemButton") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = CompactPreview(item.Preview),
            Foreground = new SolidColorBrush(Color.FromRgb(244, 245, 248)),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var time = new TextBlock
        {
            Text = item.TimeText,
            Foreground = new SolidColorBrush(Color.FromRgb(151, 159, 178)),
            FontSize = 9.5,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);
        button.Content = grid;

        button.Click += async (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            button.IsEnabled = false;
            await _selectAsync(item, _target);
        };
        return button;
    }

    private static string CompactPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var oneLine = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 84 ? oneLine : oneLine[..84] + "…";
    }

    private void PlaceNextToAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.GetWindowRect(hwnd, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_anchorRightX, _anchorTopY));
        var area = screen.WorkingArea;

        const int gap = 4;
        var left = _anchorRightX + gap;
        if (left + width > area.Right - 8)
            left = Math.Max(area.Left + 8, _anchorLeftX - width - gap);

        var top = _anchorTopY - 8;
        if (top + height > area.Bottom - 8)
            top = Math.Max(area.Top + 8, area.Bottom - height - 8);
        if (top < area.Top + 8) top = area.Top + 8;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        IsHitTestVisible = true;
        Opacity = 1;
    }

    public void Dismiss()
    {
        _closing = true;
        if (IsVisible) Close();
    }
}
