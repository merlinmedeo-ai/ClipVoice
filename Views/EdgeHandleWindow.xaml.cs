using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ClipVoice.Native;

namespace ClipVoice.Views;

public partial class EdgeHandleWindow : Window
{
    public bool AllowClose { get; set; }
    public EdgeHandleWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        };
        Loaded += (_, _) => Reposition();
        SystemParameters.StaticPropertyChanged += (_, _) => Reposition();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ((App)Application.Current).Controller.TogglePanel();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }
}
