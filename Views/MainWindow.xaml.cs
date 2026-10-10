using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipVoice.Models;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class MainWindow : Window
{
    private App AppInstance => (App)Application.Current;
    public bool AllowClose { get; set; }
    private bool _suppressAutoHide;
    private bool _syncingActiveSelection;
    private TemplatePopupWindow? _templatePopup;

    public MainWindow()
    {
        InitializeComponent();
        AppVersionText.Text = AppInfo.DisplayVersion;
        Title = $"ClipVoice {AppInfo.DisplayVersion}";
        DataContext = AppInstance.Controller;
        Loaded += (_, _) =>
        {
            FolderCombo.SelectedIndex = AppInstance.Controller.TemplateFolders.Count > 0 ? 0 : -1;
            AppInstance.Controller.StatusChanged += Controller_StatusChanged;
            AppInstance.Controller.ActiveSelectionChanged += Controller_ActiveSelectionChanged;
            AppInstance.Controller.TemplateMenuRequested += Controller_TemplateMenuRequested;
            AppInstance.Controller.GlobalMouseDown += Controller_GlobalMouseDown;
            AppInstance.Controller.TtsHealthChanged += Controller_TtsHealthChanged;
            UpdateHotkeySummary();
            SyncActiveSelection();
            UpdateTtsHealth();
            AppInstance.Controller.StartBackgroundServices();
            AppInstance.Controller.ShowPanel();
            if (AppInstance.Controller.LastHotkeyErrors.Count > 0)
                StatusText.Text = "A hotkey conflict was detected. Open Settings.";
            else if (AppInstance.Controller.Settings.EnableAltRightClickTemplates && !AppInstance.Controller.TemplateMouseHookAvailable)
                StatusText.Text = $"{FormatTemplateMouseGesture(AppInstance.Controller.Settings)} is unavailable: Windows did not allow global mouse tracking. Other features are still available.";
        };
        Deactivated += MainWindow_Deactivated;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateHotkeySummary();
                SyncActiveSelection();
            }
        };
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_suppressAutoHide && AppInstance.Controller.Settings.HidePanelWhenFocusLost)
            Hide();
    }

    private void Controller_StatusChanged(string obj) => Dispatcher.Invoke(() => StatusText.Text = obj);
    private void Controller_ActiveSelectionChanged() => Dispatcher.Invoke(SyncActiveSelection);
    private void Controller_TtsHealthChanged() => Dispatcher.Invoke(UpdateTtsHealth);

    private void UpdateTtsHealth()
    {
        var health = AppInstance.Controller.GetTtsHealthSnapshot();
        string Mark(TtsProviderSnapshot p) => p.State switch
        {
            TtsProviderState.Ready => "✓",
            TtsProviderState.Checking => "…",
            TtsProviderState.Preparing => "↓",
            TtsProviderState.Installed => "◐",
            TtsProviderState.Failed => "×",
            TtsProviderState.Missing => "○",
            _ => "?"
        };

        TtsStatusText.Text = $"TTS: Edge {Mark(health.Edge)} · Google {Mark(health.Google)} · Piper {Mark(health.Piper)}";
        var anyReady = new[] { health.Edge.State, health.Google.State, health.Piper.State }.Contains(TtsProviderState.Ready);
        var anyBusy = new[] { health.Edge.State, health.Google.State, health.Piper.State }.Any(x => x is TtsProviderState.Checking or TtsProviderState.Preparing);
        var anyInstalled = new[] { health.Edge.State, health.Google.State, health.Piper.State }.Contains(TtsProviderState.Installed);
        TtsStatusText.Foreground = anyReady ? new SolidColorBrush(Color.FromRgb(112, 221, 173))
            : anyBusy || anyInstalled ? new SolidColorBrush(Color.FromRgb(232, 196, 102))
            : new SolidColorBrush(Color.FromRgb(232, 106, 122));
        TtsStatusText.ToolTip = $"Last speech provider: {health.ActiveProvider}\n" +
            $"Edge: {health.Edge.Detail}\nGoogle: {health.Google.Detail}\nPiper: {health.Piper.Detail}\n" +
            $"ClipVoice memory: {health.AppMemoryMb} MB" +
            (health.PiperMemoryMb is long p ? $" + Piper {p} MB" : string.Empty) +
            $" / limit {health.MemoryLimitMb} MB";
    }

    private void SyncActiveSelection()
    {
        _syncingActiveSelection = true;
        try
        {
            if (AppInstance.Controller.ActiveHistoryItemId is Guid historyId)
            {
                var item = AppInstance.Controller.History.FirstOrDefault(x => x.Id == historyId);
                HistoryList.SelectedItem = item;
            }
            else
            {
                HistoryList.SelectedItem = null;
            }
        }
        finally { _syncingActiveSelection = false; }
    }

    private void UpdateHotkeySummary()
    {
        var s = AppInstance.Controller.Settings;
        var target = TranslationDirectionService.DisplayName(s.PreferredCyrillicTranslationLanguage);
        HotkeySummaryText.Text = $"{s.HotkeySpeak} — read aloud   •   {s.HotkeyTranslate} — translate   •   {s.HotkeyTranslateSpeak} — translate + speak   •   foreign text → {target}   •   {FormatTemplateMouseGesture(s)} — templates";
        TemplateMouseHintText.Text = $"{FormatTemplateMouseGesture(s)} — open templates under the cursor";
    }


    private static string FormatTemplateMouseGesture(AppSettings settings)
        => (settings.TemplateMouseModifier ?? "Alt").Replace("+", " + ") + " + right-click";

    private void Controller_TemplateMenuRequested(InputTarget target, int x, int y)
    {
        Dispatcher.Invoke(() => ShowTemplatePopup(target, x, y));
    }

    private void ShowTemplatePopup(InputTarget target, int x, int y)
    {
        LogService.Info($"Context menu opened: route=right-click-template-menu; target={AppInstance.Controller.DescribeInputTarget(target)}");
        _templatePopup?.Dismiss();

        var popup = new TemplatePopupWindow(
            AppInstance.Controller.TemplateFolders,
            target,
            x,
            y,
            (template, inputTarget) => AppInstance.Controller.InsertTemplateIntoWindowAsync(template, inputTarget),
            () => OpenSettings(3),
            CreateTemplateFromSelectionAsync,
            SpeakSelectionFromPopupAsync,
            TranslateSpeakSelectionFromPopupAsync);

        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_templatePopup, popup))
            {
                _templatePopup = null;
                AppInstance.Controller.SetGlobalMouseTracking(false);
            }
        };

        _templatePopup = popup;
        AppInstance.Controller.SetGlobalMouseTracking(true);
        popup.Show();
    }

    private async Task CreateTemplateFromSelectionAsync(InputTarget target)
    {
        string? selected;
        try
        {
            selected = await AppInstance.Controller.CaptureSelectedTextForTemplateAsync(target);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not retrieve the selected text: " + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(selected)) return;

        var dialog = new QuickTemplateWindow(selected, AppInstance.Controller.TemplateFolders);
        var wasVisible = IsVisible;
        _suppressAutoHide = true;
        try
        {
            if (wasVisible) dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;

            AppInstance.Controller.AddTemplateFromSelection(
                selected,
                dialog.TemplateName,
                dialog.SelectedFolderId,
                dialog.NewFolderName);
            RefreshTemplatesAfterSettings();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not create the template: " + ex.Message;
        }
        finally
        {
            _suppressAutoHide = false;
            // Return the caret to the field from which the template menu was opened.
            // This makes the workflow feel modal: create/cancel -> back to the same editor.
            try { await AppInstance.Controller.RestoreInputTargetAsync(target); } catch { }
        }
    }

    private async Task SpeakSelectionFromPopupAsync(InputTarget target)
    {
        using var operation = LogService.BeginOperation("speak", "context-menu");
        LogService.Info($"Context menu action selected: action=speak; target={AppInstance.Controller.DescribeInputTarget(target)}");
        string? selected;
        try
        {
            selected = await AppInstance.Controller.CaptureSelectionOrClipboardAsync(target);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not retrieve the selected text: " + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(selected)) return;
        await AppInstance.Controller.SpeakSelectedTextAsync(selected);
    }

    private async Task TranslateSpeakSelectionFromPopupAsync(InputTarget target)
    {
        using var operation = LogService.BeginOperation("translate-speak", "context-menu");
        LogService.Info($"Context menu action selected: action=translate-speak; target={AppInstance.Controller.DescribeInputTarget(target)}");
        string? selected;
        try
        {
            selected = await AppInstance.Controller.CaptureSelectionOrClipboardAsync(target);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not retrieve the selected text: " + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(selected)) return;
        await AppInstance.Controller.TranslateAndSpeakSelectedTextAsync(selected);
    }

    private void Controller_GlobalMouseDown(int x, int y)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_templatePopup is not null && !_templatePopup.ContainsScreenPoint(x, y))
                _templatePopup.Dismiss();
        }));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(0);
    private void Hotkeys_Click(object sender, RoutedEventArgs e) => OpenSettings(1);
    private void ManageTemplates_Click(object sender, RoutedEventArgs e) => OpenSettings(3);
    private void NewTemplate_Click(object sender, RoutedEventArgs e) => OpenSettings(3, createTemplateImmediately: true);

    private void OpenSettings(int tab, bool createTemplateImmediately = false)
    {
        _suppressAutoHide = true;
        try
        {
            new SettingsWindow(tab, createTemplateImmediately) { Owner = this }.ShowDialog();
            RefreshTemplatesAfterSettings();
            UpdateHotkeySummary();
            AppInstance.Controller.SyncClipboardFromSystem(addToHistory: false);
            SyncActiveSelection();
        }
        finally
        {
            _suppressAutoHide = false;
            if (IsVisible) Activate();
        }
    }

    private void RefreshTemplatesAfterSettings()
    {
        FolderCombo.Items.Refresh();
        if (FolderCombo.SelectedIndex < 0 && AppInstance.Controller.TemplateFolders.Count > 0) FolderCombo.SelectedIndex = 0;
        TemplatesList.Items.Refresh();
    }

    private void Speak_Click(object sender, RoutedEventArgs e) => _ = AppInstance.Controller.SpeakCurrentAsync(captureSelection: true, entryPoint: "panel-button");
    private void Stop_Click(object sender, RoutedEventArgs e) => AppInstance.Controller.StopSpeech();
    private void Translate_Click(object sender, RoutedEventArgs e) => _ = AppInstance.Controller.TranslateCurrentAsync(false, captureSelection: true, entryPoint: "panel-button");
    private void TranslateSpeak_Click(object sender, RoutedEventArgs e) => _ = AppInstance.Controller.TranslateCurrentAsync(true, captureSelection: true, entryPoint: "panel-button");
    private void ClearHistory_Click(object sender, RoutedEventArgs e) => AppInstance.Controller.ClearHistory();

    private void ClipboardItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardItem item) AppInstance.Controller.ActivateClipboardItem(item);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingActiveSelection) return;
        if (HistoryList.SelectedItem is ClipboardItem item) AppInstance.Controller.ActivateClipboardItem(item);
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (HistoryList.SelectedItem is ClipboardItem item) _ = AppInstance.Controller.PasteClipboardItemAsync(item);
    }

    private void ClipboardItemPaste_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipboardItem item) _ = AppInstance.Controller.PasteClipboardItemAsync(item);
    }

    private void FolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TemplatesList.ItemsSource = FolderCombo.SelectedItem is TemplateFolder folder ? folder.Templates : null;
    }

    private void TemplatesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (TemplatesList.SelectedItem is TextTemplate template) _ = AppInstance.Controller.PasteTemplateAsync(template);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void TemplatePaste_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TextTemplate template) _ = AppInstance.Controller.PasteTemplateAsync(template);
    }
}
