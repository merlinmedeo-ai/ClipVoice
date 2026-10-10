using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ClipVoice.Models;
using ClipVoice.Native;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class TemplateFolderPopupWindow : Window
{
    private readonly TemplateFolder _folder;
    private readonly InputTarget _target;
    private readonly int _anchorRightX;
    private readonly int _anchorTopY;
    private readonly int _anchorLeftX;
    private readonly Func<TextTemplate, InputTarget, Task> _selectAsync;
    private bool _closing;

    public TemplateFolderPopupWindow(
        TemplateFolder folder,
        InputTarget target,
        int anchorRightX,
        int anchorTopY,
        int anchorLeftX,
        Func<TextTemplate, InputTarget, Task> selectAsync)
    {
        InitializeComponent();
        _folder = folder;
        _target = target;
        _anchorRightX = anchorRightX;
        _anchorTopY = anchorTopY;
        _anchorLeftX = anchorLeftX;
        _selectAsync = selectAsync;


        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        Loaded += (_, _) =>
        {
            BuildItems();
            Dispatcher.BeginInvoke(new Action(PlaceNextToFolder));
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
        foreach (var template in _folder.Templates)
            ItemsHost.Children.Add(CreateTemplateButton(template));
    }

    private Button CreateTemplateButton(TextTemplate template)
    {
        var button = new Button
        {
            Style = (Style)FindResource("TemplateItemButton"),
            Tag = template
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = template.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 245, 248)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var preview = template.IsSecret ? "Content hidden" : CompactPreview(template.Content);
        if (!string.IsNullOrWhiteSpace(preview))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = preview,
                Foreground = new SolidColorBrush(Color.FromRgb(151, 159, 178)),
                FontSize = 9.5,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        grid.Children.Add(textStack);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        if (template.IsSecret)
        {
            right.Children.Add(new TextBlock
            {
                Text = "●",
                Foreground = new SolidColorBrush(Color.FromRgb(170, 163, 255)),
                FontSize = 8,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Secret template"
            });
        }

        if (!string.IsNullOrWhiteSpace(template.Hotkey))
        {
            right.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 40, 52)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = template.Hotkey,
                    Foreground = new SolidColorBrush(Color.FromRgb(174, 181, 198)),
                    FontSize = 9
                }
            });
        }

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        button.Content = grid;

        button.Click += async (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            button.IsEnabled = false;
            await _selectAsync(template, _target);
        };

        return button;
    }

    private static string CompactPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var oneLine = string.Join(" ", text.Replace("\r", " ").Replace("\n", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 58 ? oneLine : oneLine[..58] + "…";
    }

    private void PlaceNextToFolder()
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
    }

    public void Dismiss()
    {
        if (_closing)
        {
            if (IsVisible) Close();
            return;
        }
        _closing = true;
        Close();
    }
}
