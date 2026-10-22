using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ClipVoice.Services;
using ClipVoice.Views;
using Forms = System.Windows.Forms;

namespace ClipVoice;

public partial class App : Application
{
    public AppController Controller { get; private set; } = null!;
    public MainWindow MainPanel { get; private set; } = null!;
    public EdgeHandleWindow EdgeHandle { get; private set; } = null!;
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\ClipVoice.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("ClipVoice is already running. Find its icon in the system tray or use the panel hotkey.", "ClipVoice", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            Controller = new AppController();
            await Controller.InitializeAsync();
            MainPanel = new MainWindow();
            EdgeHandle = new EdgeHandleWindow();
            ApplyLocalization();
            Controller.BindWindows(MainPanel, EdgeHandle);

            CreateTray();
            if (Controller.Settings.ShowEdgeHandle) EdgeHandle.Show();
            MainPanel.Show();
        }
        catch (Exception ex)
        {
            LogService.Write("Startup failure", ex);
            MessageBox.Show("ClipVoice could not start.\n\n" + ex.Message + "\n\nDetails were saved to %LocalAppData%\\ClipVoice\\logs", "ClipVoice", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    public void ApplyLocalization()
    {
        var language = LocalizationService.NormalizeLanguage(Controller?.Settings.Language);
        if (MainPanel is not null) MainPanel.ApplyLocalization();
        if (EdgeHandle is not null) LocalizationService.Apply(EdgeHandle, language);
        if (_tray?.ContextMenuStrip is Forms.ContextMenuStrip menu)
        {
            var labels = language switch
            {
                "ru" => new[] { "Открыть ClipVoice", "Настройки", "Состояние озвучивания", "Приостановить / продолжить буфер", "Выход" },
                "uk" => new[] { "Відкрити ClipVoice", "Налаштування", "Стан озвучення", "Призупинити / відновити буфер", "Вихід" },
                _ => new[] { "Open ClipVoice", "Settings", "Speech status", "Pause / resume clipboard", "Exit" }
            };
            var labelIndex = 0;
            foreach (Forms.ToolStripItem item in menu.Items)
                if (item is not Forms.ToolStripSeparator && labelIndex < labels.Length) item.Text = labels[labelIndex++];
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Write("UI error", e.Exception);
        MessageBox.Show("An error occurred, but ClipVoice will try to continue.\n\n" + e.Exception.Message, "ClipVoice", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogService.Write("Unhandled error", ex);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ClipVoice", null, (_, _) => Dispatcher.Invoke(() => Controller.ShowPanel()));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(() => new SettingsWindow(0) { Owner = MainPanel }.ShowDialog()));
        menu.Items.Add("Speech status", null, (_, _) => Dispatcher.Invoke(() => new SettingsWindow(2) { Owner = MainPanel }.ShowDialog()));
        menu.Items.Add("Pause / resume clipboard", null, (_, _) => Controller.ToggleClipboardMonitoring());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = LoadTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Text = "ClipVoice",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => Controller.ShowPanel());
    }


    private static Icon LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TrayIcon.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using (resource.Stream)
                using (var icon = new Icon(resource.Stream))
                    return (Icon)icon.Clone();
            }
        }
        catch { }

        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null) return extracted;
            }
        }
        catch { }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void ExitApplication()
    {
        try { Controller?.Dispose(); } catch { }
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (EdgeHandle is not null) { EdgeHandle.AllowClose = true; EdgeHandle.Close(); }
        if (MainPanel is not null) { MainPanel.AllowClose = true; MainPanel.Close(); }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        Shutdown();
    }
}
