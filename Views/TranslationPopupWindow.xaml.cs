using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipVoice.Native;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class TranslationPopupWindow : Window
{
    private readonly int _screenX;
    private readonly int _screenY;
    private bool _hasBeenActivated;
    private bool _isClosing;
    private readonly DispatcherTimer _deactivationTimer;

    public TranslationPopupWindow(string text, string languagePair, int screenX, int screenY)
    {
        InitializeComponent();
        LocalizationService.Apply(this, ((App)Application.Current).Controller.Settings.Language);
        TranslationText.Text = text;
        LanguagePairText.Text = languagePair;
        _screenX = screenX;
        _screenY = screenY;
        _deactivationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _deactivationTimer.Tick += (_, _) =>
        {
            _deactivationTimer.Stop();
            if (!_isClosing && IsVisible && !IsActive) RequestClose();
        };

        Activated += (_, _) =>
        {
            if (_isClosing) return;
            _hasBeenActivated = true;
            _deactivationTimer.Stop();
        };
        Deactivated += (_, _) =>
        {
            if (_hasBeenActivated && !_isClosing) _deactivationTimer.Start();
        };
        Closing += (_, _) =>
        {
            _isClosing = true;
            _deactivationTimer.Stop();
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(PlaceNearPointer));
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void PlaceNearPointer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.GetWindowRect(hwnd, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_screenX, _screenY));
        var area = screen.WorkingArea;
        const int gap = 14;

        var left = _screenX + gap;
        var top = _screenY + gap;
        if (left + width > area.Right - 8) left = _screenX - width - gap;
        if (top + height > area.Bottom - 8) top = _screenY - height - gap;
        left = Math.Clamp(left, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8));
        top = Math.Clamp(top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - height - 8));

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        IsHitTestVisible = true;
        Opacity = 1;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void RequestClose()
    {
        if (_isClosing || !IsVisible) return;
        _isClosing = true;
        _deactivationTimer.Stop();
        Close();
    }

    public void Dismiss()
    {
        RequestClose();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        RequestClose();
    }
}
