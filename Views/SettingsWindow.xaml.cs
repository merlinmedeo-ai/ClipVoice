using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text;
using Microsoft.Win32;
using ClipVoice.Models;
using ClipVoice.Native;
using ClipVoice.Services;

namespace ClipVoice.Views;

public partial class SettingsWindow : Window
{
    private App AppInstance => (App)Application.Current;
    private readonly ObservableCollection<TemplateFolder> _folders;
    private Point _folderDragStart;
    private TemplateFolder? _folderDragCandidate;
    private Point _templateDragStart;
    private TemplateDragPayload? _templateDragCandidate;

    private sealed record TemplateDragPayload(TextTemplate Template, TemplateFolder SourceFolder);

    public SettingsWindow(int initialTab = 0, bool createTemplateImmediately = false)
    {
        InitializeComponent();
        Title = $"Настройки ClipVoice {AppInfo.DisplayVersion}";
        SettingsTitleText.Text = $"Настройки ClipVoice {AppInfo.DisplayVersion}";
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
        var s = AppInstance.Controller.Settings;
        PanelHotkey.Text = s.HotkeyPanel;
        SpeakHotkey.Text = s.HotkeySpeak;
        TranslateHotkey.Text = s.HotkeyTranslate;
        TranslateSpeakHotkey.Text = s.HotkeyTranslateSpeak;
        SpeechRate.Value = s.SpeechRatePercent;
        SelectComboByTag(PanelOpenMode, s.ShowEdgeHandle ? "edge" : "hotkey");
        AutoStartCheck.IsChecked = s.AutoStart;
        HideAfterPasteCheck.IsChecked = s.HidePanelAfterPaste;
        AutoHidePanelCheck.IsChecked = s.HidePanelWhenFocusLost;
        AutoPrepareLocalTtsCheck.IsChecked = s.AutoPrepareLocalTts;
        TemplateMouseGestureCheck.IsChecked = s.EnableAltRightClickTemplates;
        SelectComboByTag(TemplateMouseModifierCombo, s.TemplateMouseModifier);
        SelectComboByTag(PreferredTranslationLanguage, s.PreferredCyrillicTranslationLanguage);
        SelectCombo(RussianVoice, s.RussianVoice);
        SelectCombo(EnglishVoice, s.EnglishVoice);
        SelectCombo(UkrainianVoice, s.UkrainianVoice);

        _folders = new ObservableCollection<TemplateFolder>(AppInstance.Controller.TemplateFolders.Select(CloneFolder));
        FolderList.ItemsSource = _folders;
        FolderList.SelectedIndex = _folders.Count > 0 ? 0 : -1;
        SettingsTabs.SelectedIndex = Math.Clamp(initialTab, 0, 3);
        ShowExistingHotkeyIssues();
        AppInstance.Controller.TtsHealthChanged += Controller_TtsHealthChanged;
        Closed += (_, _) => AppInstance.Controller.TtsHealthChanged -= Controller_TtsHealthChanged;
        Loaded += (_, _) => UpdateTtsHealth();
        if (createTemplateImmediately)
        {
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                SettingsTabs.SelectedIndex = 3;
                AddTemplate_Click(this, new RoutedEventArgs());
            }));
        }
    }

    private static TemplateFolder CloneFolder(TemplateFolder source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Templates = new ObservableCollection<TextTemplate>(source.Templates.Select(t => new TextTemplate
        {
            Id = t.Id, Name = t.Name, Content = t.Content, Hotkey = t.Hotkey, PasteOnHotkey = t.PasteOnHotkey, IsSecret = t.IsSecret
        }))
    };

    private static void SelectCombo(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if ((item.Content?.ToString() ?? "") == value) { combo.SelectedItem = item; return; }
        combo.SelectedIndex = 0;
    }

    private static string ComboValue(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private static void SelectComboByTag(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        combo.SelectedIndex = 0;
    }

    private static string ComboTagValue(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private AppSettings BuildSettings() => new()
    {
        HotkeyPanel = PanelHotkey.Text.Trim(),
        HotkeySpeak = SpeakHotkey.Text.Trim(),
        HotkeyTranslate = TranslateHotkey.Text.Trim(),
        HotkeyTranslateSpeak = TranslateSpeakHotkey.Text.Trim(),
        AutoStart = AutoStartCheck.IsChecked == true,
        ShowEdgeHandle = string.Equals(ComboTagValue(PanelOpenMode, "hotkey"), "edge", StringComparison.OrdinalIgnoreCase),
        AutoCaptureSelection = AppInstance.Controller.Settings.AutoCaptureSelection,
        HidePanelAfterPaste = HideAfterPasteCheck.IsChecked == true,
        HidePanelWhenFocusLost = AutoHidePanelCheck.IsChecked == true,
        AutoPrepareLocalTts = AutoPrepareLocalTtsCheck.IsChecked == true,
        EnableAltRightClickTemplates = TemplateMouseGestureCheck.IsChecked == true,
        TemplateMouseModifier = ComboTagValue(TemplateMouseModifierCombo, "Alt"),
        HistoryLimit = 25,
        SpeechRatePercent = (int)SpeechRate.Value,
        PreferredCyrillicTranslationLanguage = ComboTagValue(PreferredTranslationLanguage, "ru"),
        RussianVoice = ComboValue(RussianVoice),
        EnglishVoice = ComboValue(EnglishVoice),
        UkrainianVoice = ComboValue(UkrainianVoice)
    };

    private void FitToMonitorWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                FitToWorkArea(SystemParameters.WorkArea);
                return;
            }

            var source = HwndSource.FromHwnd(hwnd);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
            var bottomRight = transform.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
            FitToWorkArea(new Rect(topLeft, bottomRight));
        }
        catch
        {
            FitToWorkArea(SystemParameters.WorkArea);
        }
    }

    private void FitToWorkArea(Rect workArea)
    {
        const double edgeGap = 8;
        var availableWidth = Math.Max(720, workArea.Width - edgeGap * 2);
        var availableHeight = Math.Max(560, workArea.Height - edgeGap * 2);

        // Сначала уменьшаем минимумы, и только потом задаём максимумы: это важно
        // для небольших экранов, где рабочая область может быть ниже исходного MinHeight.
        MinWidth = Math.Min(900, availableWidth);
        MinHeight = Math.Min(620, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;

        Width = Math.Min(1120, availableWidth);
        // Используем почти всю рабочую высоту, чтобы списки папок/шаблонов оставались высокими,
        // но окно никогда не заходило под панель задач.
        Height = availableHeight;

        Left = workArea.Left + Math.Max(edgeGap, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(edgeGap, (workArea.Height - Height) / 2);
    }

    private void Controller_TtsHealthChanged() => Dispatcher.Invoke(UpdateTtsHealth);

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!AppInstance.Controller.CopyDiagnosticLogsToClipboard())
            {
                MessageBox.Show(this, "Ошибок пока не записано.",
                    "Журнал ошибок", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show(this, "Журнал ошибок скопирован.",
                "Журнал ошибок", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogService.Write("Could not copy diagnostic logs", ex);
            MessageBox.Show(this, "Не удалось скопировать логи: " + ex.Message,
                "Диагностика озвучки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenAudioCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInstance.Controller.AudioCacheDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Write("Could not open audio cache folder", ex);
            MessageBox.Show(this, "Не удалось открыть папку аудиокэша: " + ex.Message,
                "Аудиокэш", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTtsHealth()
    {
        var health = AppInstance.Controller.GetTtsHealthSnapshot();
        ActiveTtsText.Text = "Последний: " + health.ActiveProvider;
        EdgeTtsHealthText.Text = FormatProvider(health.Edge);
        GoogleTtsHealthText.Text = FormatProvider(health.Google);
        PiperTtsHealthText.Text = FormatProvider(health.Piper);
        EdgeTtsHealthText.Foreground = StateBrush(health.Edge.State);
        GoogleTtsHealthText.Foreground = StateBrush(health.Google.State);
        PiperTtsHealthText.Foreground = StateBrush(health.Piper.State);
        var piperPart = health.PiperMemoryMb is long p ? $" + Piper {p} МБ" : string.Empty;
        TtsMemoryText.Text = $"Память сейчас: ClipVoice {health.AppMemoryMb} МБ{piperPart} = {health.TotalMemoryMb} МБ / лимит {health.MemoryLimitMb} МБ";
        TtsMemoryText.Foreground = health.TotalMemoryMb >= health.MemoryLimitMb - 10
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 106, 122))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(154, 164, 181));
    }

    private static string FormatProvider(TtsProviderSnapshot provider)
    {
        var prefix = provider.State switch
        {
            TtsProviderState.Ready => "Работает — ",
            TtsProviderState.Checking => "Проверяется — ",
            TtsProviderState.Preparing => "Подготовка — ",
            TtsProviderState.Installed => "Установлен — ",
            TtsProviderState.Missing => "Не готов — ",
            TtsProviderState.Failed => "Недоступен — ",
            _ => "Не проверен — "
        };
        var checkedAt = provider.LastCheckedUtc is DateTime time ? $" • {time.ToLocalTime():HH:mm}" : string.Empty;
        return prefix + provider.Detail + checkedAt;
    }

    private static System.Windows.Media.Brush StateBrush(TtsProviderState state)
        => state switch
        {
            TtsProviderState.Ready => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 201, 157)),
            TtsProviderState.Failed => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 106, 122)),
            TtsProviderState.Checking or TtsProviderState.Preparing or TtsProviderState.Installed => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 196, 102)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(154, 164, 181))
        };

    private async void CheckTts_Click(object sender, RoutedEventArgs e)
    {
        SaveStatusText.Text = "Проверяю Edge, Google и запускаю реальный офлайн-тест Piper…";
        try
        {
            await AppInstance.Controller.CheckTtsHealthAsync();
            SaveStatusText.Text = "Проверка озвучки завершена.";
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "Ошибка проверки: " + ex.Message;
        }
        UpdateTtsHealth();
    }

    private async void MeasureTtsSpeed_Click(object sender, RoutedEventArgs e)
    {
        SaveStatusText.Text = "Озвучиваю одинаковую тестовую фразу Edge, Google и Piper и измеряю длительность…";
        try
        {
            var results = await AppInstance.Controller.DiagnoseTtsSpeechSpeedAsync();
            SaveStatusText.Text = "Сравнение скорости завершено.";
            MessageBox.Show(this, results, "Скорость озвучки", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "Ошибка сравнения: " + ex.Message;
        }
        UpdateTtsHealth();
    }

    private async void PrepareLocalTts_Click(object sender, RoutedEventArgs e)
    {
        PrepareLocalTtsButton.IsEnabled = false;
        SaveStatusText.Text = "Подготавливаю локальный резерв. Первый раз нужно скачать движок и три голоса…";
        try
        {
            await AppInstance.Controller.PrepareLocalTtsAsync(background: false);
            SaveStatusText.Text = "Piper и голоса установлены. Нажми «Проверить все, включая офлайн», чтобы подтвердить реальный синтез.";
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "Не удалось подготовить Piper: " + ex.Message;
        }
        finally
        {
            PrepareLocalTtsButton.IsEnabled = true;
            UpdateTtsHealth();
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            box.Clear(); e.Handled = true; return;
        }
        if (HotkeyParser.IsModifierKey(key)) { e.Handled = true; return; }
        var combo = HotkeyParser.FromKeyEvent(key, Keyboard.Modifiers);
        if (!string.IsNullOrWhiteSpace(combo)) box.Text = combo;
        e.Handled = true;
        ValidateHotkeysLive();
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        PanelHotkey.Text = "Ctrl+Alt+Space";
        SpeakHotkey.Text = "Ctrl+Alt+S";
        TranslateHotkey.Text = "Ctrl+Alt+T";
        TranslateSpeakHotkey.Text = "Ctrl+Alt+Y";
        ValidateHotkeysLive();
    }

    private void ValidateHotkeysLive()
    {
        var issues = AppInstance.Controller.ValidateHotkeyConfiguration(BuildSettings(), _folders);
        HotkeyIssueText.Text = issues.Count == 0 ? "" : string.Join(Environment.NewLine, issues);
    }

    private void ShowExistingHotkeyIssues()
    {
        if (AppInstance.Controller.LastHotkeyErrors.Count > 0)
            HotkeyIssueText.Text = string.Join(Environment.NewLine, AppInstance.Controller.LastHotkeyErrors);
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TemplateList.ItemsSource = FolderList.SelectedItem is TemplateFolder folder ? folder.Templates : null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static bool DragThresholdExceeded(Point start, Point current)
        => Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
           || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private static int GetInsertionIndex(ListBox list, DragEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null) return list.Items.Count;
        var index = list.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0) return list.Items.Count;
        if (e.GetPosition(item).Y > item.ActualHeight / 2) index++;
        return index;
    }

    private static T? GetDropTarget<T>(DragEventArgs e) where T : class
        => FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as T;

    private void FolderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _folderDragStart = e.GetPosition(null);
        _folderDragCandidate = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as TemplateFolder;
    }

    private void FolderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _folderDragCandidate is null) return;
        if (!DragThresholdExceeded(_folderDragStart, e.GetPosition(null))) return;

        var folder = _folderDragCandidate;
        _folderDragCandidate = null;
        DragDrop.DoDragDrop(FolderList, new DataObject(typeof(TemplateFolder), folder), DragDropEffects.Move);
    }

    private void FolderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TemplateFolder)) || e.Data.GetDataPresent(typeof(TemplateDragPayload))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(TemplateFolder)) && e.Data.GetData(typeof(TemplateFolder)) is TemplateFolder draggedFolder)
        {
            var oldIndex = _folders.IndexOf(draggedFolder);
            if (oldIndex < 0) return;

            var insertionIndex = GetInsertionIndex(FolderList, e);
            if (oldIndex < insertionIndex) insertionIndex--;
            var newIndex = Math.Clamp(insertionIndex, 0, Math.Max(0, _folders.Count - 1));
            if (newIndex != oldIndex) _folders.Move(oldIndex, newIndex);
            FolderList.SelectedItem = draggedFolder;
            SaveStatusText.Text = "Порядок папок изменён. Нажми «Сохранить изменения».";
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(TemplateDragPayload)) && e.Data.GetData(typeof(TemplateDragPayload)) is TemplateDragPayload payload)
        {
            var targetFolder = GetDropTarget<TemplateFolder>(e);
            if (targetFolder is null || ReferenceEquals(targetFolder, payload.SourceFolder)) return;

            if (payload.SourceFolder.Templates.Remove(payload.Template))
            {
                targetFolder.Templates.Add(payload.Template);
                FolderList.SelectedItem = targetFolder;
                TemplateList.SelectedItem = payload.Template;
                SaveStatusText.Text = $"Шаблон «{payload.Template.Name}» перемещён в «{targetFolder.Name}». Нажми «Сохранить изменения».";
                ValidateHotkeysLive();
            }
            e.Handled = true;
        }
    }

    private void TemplateList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _templateDragStart = e.GetPosition(null);
        var template = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as TextTemplate;
        _templateDragCandidate = template is not null && FolderList.SelectedItem is TemplateFolder folder
            ? new TemplateDragPayload(template, folder)
            : null;
    }

    private void TemplateList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _templateDragCandidate is null) return;
        if (!DragThresholdExceeded(_templateDragStart, e.GetPosition(null))) return;

        var payload = _templateDragCandidate;
        _templateDragCandidate = null;
        DragDrop.DoDragDrop(TemplateList, new DataObject(typeof(TemplateDragPayload), payload), DragDropEffects.Move);
    }

    private void TemplateList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TemplateDragPayload)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TemplateList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TemplateDragPayload))
            || e.Data.GetData(typeof(TemplateDragPayload)) is not TemplateDragPayload payload
            || FolderList.SelectedItem is not TemplateFolder targetFolder) return;

        var insertionIndex = GetInsertionIndex(TemplateList, e);
        if (ReferenceEquals(payload.SourceFolder, targetFolder))
        {
            var oldIndex = targetFolder.Templates.IndexOf(payload.Template);
            if (oldIndex < 0) return;
            if (oldIndex < insertionIndex) insertionIndex--;
            var newIndex = Math.Clamp(insertionIndex, 0, Math.Max(0, targetFolder.Templates.Count - 1));
            if (newIndex != oldIndex) targetFolder.Templates.Move(oldIndex, newIndex);
        }
        else
        {
            if (!payload.SourceFolder.Templates.Remove(payload.Template)) return;
            insertionIndex = Math.Clamp(insertionIndex, 0, targetFolder.Templates.Count);
            targetFolder.Templates.Insert(insertionIndex, payload.Template);
        }

        TemplateList.SelectedItem = payload.Template;
        SaveStatusText.Text = "Порядок шаблонов изменён. Нажми «Сохранить изменения».";
        ValidateHotkeysLive();
        e.Handled = true;
    }

    private void TemplateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TemplateList.SelectedItem is TextTemplate) EditTemplate_Click(sender, new RoutedEventArgs());
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptWindow("Новая папка", "Название папки:", "Новая папка", "Создать папку") { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            var folder = new TemplateFolder { Name = dialog.Value.Trim() };
            _folders.Add(folder);
            FolderList.SelectedItem = folder;
        }
    }

    private void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not TemplateFolder folder) return;
        var dialog = new TextPromptWindow("Переименовать папку", "Новое название:", folder.Name, "Переименовать") { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            folder.Name = dialog.Value.Trim();
            FolderList.Items.Refresh();
        }
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not TemplateFolder folder) return;
        if (_folders.Count <= 1) { SaveStatusText.Text = "Нельзя удалить последнюю папку."; return; }
        if (folder.Templates.Count > 0 && MessageBox.Show(this, $"Удалить папку «{folder.Name}» и все шаблоны внутри?", "ClipVoice", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var index = _folders.IndexOf(folder);
        _folders.Remove(folder);
        FolderList.SelectedIndex = Math.Max(0, Math.Min(index, _folders.Count - 1));
    }

    private void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not TemplateFolder folder) { SaveStatusText.Text = "Сначала выбери папку."; return; }
        var editor = new TemplateEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            folder.Templates.Add(editor.Result);
            TemplateList.SelectedItem = editor.Result;
            SaveStatusText.Text = "Шаблон создан. Нажми «Сохранить изменения», чтобы применить его и горячую клавишу.";
            ValidateHotkeysLive();
        }
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not TemplateFolder folder || TemplateList.SelectedItem is not TextTemplate template) return;
        var editor = new TemplateEditorWindow(template) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            var index = folder.Templates.IndexOf(template);
            folder.Templates[index] = editor.Result;
            TemplateList.SelectedIndex = index;
            ValidateHotkeysLive();
        }
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not TemplateFolder folder || TemplateList.SelectedItem is not TextTemplate template) return;
        if (MessageBox.Show(this, $"Удалить шаблон «{template.Name}»?", "ClipVoice", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            folder.Templates.Remove(template);
            ValidateHotkeysLive();
        }
    }

    private void ExportTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "TXT-файл будет содержать названия и полный текст всех шаблонов, включая помеченные как секретные. Продолжить экспорт?",
                "Экспорт шаблонов", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт шаблонов ClipVoice",
            Filter = "Текстовые файлы (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = "ClipVoice-шаблоны.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, TemplateTextTransferService.Export(_folders), new UTF8Encoding(false));
            SaveStatusText.Text = $"Экспортировано папок: {_folders.Count}. Файл сохранён.";
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "Не удалось экспортировать шаблоны: " + ex.Message;
        }
    }

    private void ImportTemplates_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт шаблонов ClipVoice",
            Filter = "Текстовые файлы (*.txt)|*.txt",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var imported = TemplateTextTransferService.Import(File.ReadAllText(dialog.FileName, Encoding.UTF8));
            var templateCount = imported.Sum(folder => folder.Templates.Count);
            var firstImported = imported[0];
            foreach (var folder in imported) _folders.Add(folder);
            FolderList.SelectedItem = firstImported;
            SaveStatusText.Text = $"Добавлено папок: {imported.Count}, шаблонов: {templateCount}. Нажми «Сохранить изменения», чтобы применить импорт.";
            ValidateHotkeysLive();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось импортировать TXT-файл. Проверь, что это экспорт ClipVoice.\n\n" + ex.Message,
                "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveStatusText.Text = "";
        var settings = BuildSettings();
        var validation = AppInstance.Controller.ValidateHotkeyConfiguration(settings, _folders);
        if (validation.Count > 0)
        {
            SaveStatusText.Text = string.Join(Environment.NewLine, validation);
            SettingsTabs.SelectedIndex = validation.Any(x => x.StartsWith("Шаблон", StringComparison.OrdinalIgnoreCase)) ? 3 : 1;
            return;
        }
        AppInstance.Controller.ReplaceTemplateFolders(_folders);
        var registration = AppInstance.Controller.UpdateSettings(settings);
        if (registration.Count > 0)
        {
            SaveStatusText.Text = string.Join(Environment.NewLine, registration);
            SettingsTabs.SelectedIndex = registration.Any(x => x.StartsWith("Шаблон", StringComparison.OrdinalIgnoreCase)) ? 3 : 1;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
