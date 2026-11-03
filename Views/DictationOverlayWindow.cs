using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipVoice.Services;

namespace ClipVoice.Views;

internal sealed class DictationOverlayWindow : Window
{
    private readonly Border[] _bars = new Border[19];
    private readonly double[] _levels = new double[19];
    private readonly DispatcherTimer _timer;
    private readonly MicrophoneMeter _meter = new();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public DictationOverlayWindow()
    {
        Width = 236; Height = 64;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = false;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 24;
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 32, 32, 46)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 61, 88)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(30), Padding = new Thickness(12, 10, 12, 10)
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "\uE720", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 23, Foreground = new SolidColorBrush(Color.FromRgb(184, 149, 245)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        row.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(137, 235, 190)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(179, 139, 244)) };
            row.Children.Add(_bars[i]);
        }
        root.Child = row; Content = root;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x00000020 | 0x00000080);
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => UpdateBars();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _meter.Dispose(); };
    }

    private void UpdateBars()
    {
        // Every bar responds to the current voice level in place; no scrolling history.
        var peak = _meter.ReadPeak();
        var level = peak < 0.008 ? 0 : Math.Clamp(Math.Sqrt(peak) * 1.6, 0, 1);
        for (var i = 0; i < _bars.Length; i++)
        {
            var distanceFromCenter = Math.Abs(i - (_bars.Length - 1) / 2.0) / ((_bars.Length - 1) / 2.0);
            var profile = (1 - 0.45 * distanceFromCenter) * (0.82 + 0.18 * Math.Cos(i * 2.3));
            var target = level * profile;
            // Quick rise on syllables, gentle fall between them.
            var smoothing = target > _levels[i] ? 0.8 : 0.3;
            _levels[i] += (target - _levels[i]) * smoothing;
            _bars[i].Height = 4 + 30 * _levels[i];
        }
    }
}
