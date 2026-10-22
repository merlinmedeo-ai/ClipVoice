using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using ClipVoice.Models;
using ClipVoice.Views;

namespace ClipVoice.Services;

public sealed class AppController : IDisposable
{
    public const string ActionPanel = "panel";
    public const string ActionSpeak = "speak";
    public const string ActionTranslate = "translate";
    public const string ActionTranslateSpeak = "translateSpeak";
    private const string TemplatePrefix = "template:";
    private const int MaxHistoryTextChars = 1_000_000;

    private readonly StorageService _storage = new();
    private readonly DispatcherTimer _stateSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly SystemIntegrationService _system = new();
    private readonly TranslationService _translation = new();
    private readonly AudioPlaybackService _audio = new();
    private readonly EdgeTtsService _tts;
    private readonly GoogleTranslateTtsService _fallbackTts;
    private readonly PiperTtsService _localTts;
    private readonly object _ttsHealthLock = new();
    private TtsProviderSnapshot _edgeHealth = new("edge", "Edge online", TtsProviderState.Unknown, "Not checked yet");
    private TtsProviderSnapshot _googleHealth = new("google", "Google fallback", TtsProviderState.Unknown, "Not checked yet");
    private TtsProviderSnapshot _piperHealth = new("piper", "Piper local", TtsProviderState.Unknown, "Not checked yet");
    private string _activeTtsProvider = "—";
    private long? _piperMemoryMb;
    private bool _piperVerified;
    private CancellationTokenSource? _backgroundCts;
    private bool _backgroundStarted;
    private bool _globalMouseTracking;
    public const int TtsMemoryLimitMb = 1000;
    private uint _lastOwnClipboardSequence;
    private bool _monitoring = true;
    private MainWindow? _main;
    private EdgeHandleWindow? _handle;
    private IntPtr _lastTargetWindow;
    private InputTarget _lastInputTarget;
    private CancellationTokenSource? _workCts;

    public AppSettings Settings { get; private set; }
    public ObservableCollection<ClipboardItem> History { get; private set; } = new();
    public ObservableCollection<TemplateFolder> TemplateFolders { get; private set; } = new();
    public IReadOnlyList<string> LastHotkeyErrors { get; private set; } = Array.Empty<string>();
    public bool TemplateMouseHookAvailable => _system.TemplateMouseHookAvailable;
    public Guid? ActiveHistoryItemId { get; private set; }
    public Guid? ActiveTemplateId { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action? ActiveSelectionChanged;
    public event Action<InputTarget, int, int>? TemplateMenuRequested;
    public event Action<int, int>? GlobalMouseDown;
    public event Action? TtsHealthChanged;

    public AppController()
    {
        _tts = new EdgeTtsService(_storage);
        _fallbackTts = new GoogleTranslateTtsService(_storage);
        _localTts = new PiperTtsService(_storage);
        _localTts.ProgressChanged += OnPiperProgress;
        _localTts.ProcessMemoryChanged += OnPiperMemoryChanged;
        _stateSaveTimer.Tick += (_, _) =>
        {
            _stateSaveTimer.Stop();
            SaveState();
        };
        Settings = _storage.LoadSettings();
        RefreshLocalTtsHealth();
    }

    public Task InitializeAsync()
    {
        Settings.HistoryLimit = 25;
        var state = _storage.LoadState();
        History = new ObservableCollection<ClipboardItem>(state.History.Take(Settings.HistoryLimit));
        TemplateFolders = StorageService.ToFolders(state);
        History.CollectionChanged += (_, _) => ScheduleStateSave();
        TemplateFolders.CollectionChanged += FoldersChanged;
        foreach (var f in TemplateFolders) f.Templates.CollectionChanged += TemplatesChanged;

        try { SyncClipboardFromSystem(addToHistory: true); }
        catch { }

        _system.TemplateMouseGestureEnabled = Settings.EnableAltRightClickTemplates;
        _system.TemplateMouseGestureModifier = Settings.TemplateMouseModifier;
        _system.ClipboardChanged += OnClipboardChanged;
        _system.HotkeyPressed += OnHotkey;
        _system.TemplateMenuRequested += OnTemplateMenuRequested;
        RefreshHotkeys();
        ApplyAutoStart(Settings.AutoStart);
        return Task.CompletedTask;
    }

    public void BindWindows(MainWindow main, EdgeHandleWindow handle)
    {
        _main = main;
        _handle = handle;
    }

    public void StartBackgroundServices()
    {
        if (_backgroundStarted) return;
        _backgroundStarted = true;
        _backgroundCts = new CancellationTokenSource();
        _ = TtsHealthLoopAsync(_backgroundCts.Token);
        _ = RunDeferredMaintenanceAsync(_backgroundCts.Token);
    }

    private async Task RunDeferredMaintenanceAsync(CancellationToken ct)
    {
        try
        {
            // Cache enumeration can touch hundreds of megabytes. Keep it out of
            // the startup path and wait until the UI has settled.
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            await Task.Run(() => _storage.PruneAudioCache(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Write("Audio cache cleanup failed", ex); }
    }

    public TtsHealthSnapshot GetTtsHealthSnapshot()
    {
        TtsProviderSnapshot edge, google, piper;
        string active;
        long? piperMemory;
        lock (_ttsHealthLock)
        {
            edge = _edgeHealth;
            google = _googleHealth;
            piper = _piperHealth;
            active = _activeTtsProvider;
            piperMemory = _piperMemoryMb;
        }
        long appMb;
        try { appMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024); }
        catch { appMb = 0; }
        var total = appMb + (piperMemory ?? 0);
        return new TtsHealthSnapshot(edge, google, piper, active, appMb, piperMemory, total, TtsMemoryLimitMb);
    }

    public Task CheckTtsHealthAsync(CancellationToken ct = default)
        => CheckTtsHealthInternalAsync(includeLocalProbe: true, ct);

    private async Task CheckTtsHealthInternalAsync(bool includeLocalProbe, CancellationToken ct)
    {
        var edgeTask = CheckEdgeHealthAsync(ct);
        var googleTask = CheckGoogleHealthAsync(ct);
        Task? piperTask = null;
        if (includeLocalProbe && _localTts.IsCoreReady)
            piperTask = CheckPiperHealthAsync(ct);
        else
            RefreshLocalTtsHealth();

        if (piperTask is null)
            await Task.WhenAll(edgeTask, googleTask);
        else
            await Task.WhenAll(edgeTask, googleTask, piperTask);

        if (!includeLocalProbe) RefreshLocalTtsHealth();
    }

    public async Task PrepareLocalTtsAsync(bool background = false, CancellationToken ct = default)
    {
        UpdatePiperHealth(TtsProviderState.Preparing, "Preparing the RU / UK / EN offline fallback…");
        try
        {
            await _localTts.PrepareCoreAsync(ct);
            _piperVerified = false;
            RefreshLocalTtsHealth();
            if (!background) SetStatus("Piper and its voices are installed. Click “Check all, including offline” to run a real offline test.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RefreshLocalTtsHealth(ex);
            LogService.Write("Piper preparation failed", ex);
            if (!background)
            {
                SetStatus("Could not prepare the offline fallback: " + ShortTtsError(ex));
                throw;
            }
        }
    }

    private async Task TtsHealthLoopAsync(CancellationToken ct)
    {
        try
        {
            // Do not compete with WPF startup for CPU and network resources.
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            while (!ct.IsCancellationRequested)
            {
                try { await CheckTtsHealthInternalAsync(includeLocalProbe: false, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { LogService.Write("TTS health check failed", ex); }
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckEdgeHealthAsync(CancellationToken ct)
    {
        UpdateEdgeHealth(TtsProviderState.Checking, "Checking…");
        try
        {
            await _tts.ProbeAsync(ct);
            UpdateEdgeHealth(TtsProviderState.Ready, "Ready");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            UpdateEdgeHealth(TtsProviderState.Failed, ShortTtsError(ex));
            LogService.Write("Edge TTS health probe failed", ex);
        }
    }

    private async Task CheckGoogleHealthAsync(CancellationToken ct)
    {
        UpdateGoogleHealth(TtsProviderState.Checking, "Checking…");
        try
        {
            await _fallbackTts.ProbeAsync(ct);
            UpdateGoogleHealth(TtsProviderState.Ready, "Ready as an online fallback");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            UpdateGoogleHealth(TtsProviderState.Failed, ShortTtsError(ex));
            LogService.Write("Google TTS health probe failed", ex);
        }
    }

    private async Task CheckPiperHealthAsync(CancellationToken ct)
    {
        if (!_localTts.IsCoreReady)
        {
            _piperVerified = false;
            RefreshLocalTtsHealth();
            return;
        }

        UpdatePiperHealth(TtsProviderState.Checking, "Real offline test: starting local synthesis…");
        try
        {
            await _localTts.ProbeAsync(ct);
            _piperVerified = true;
            UpdatePiperHealth(TtsProviderState.Ready, "Offline synthesis works • RU / UK / EN installed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _piperVerified = false;
            UpdatePiperHealth(TtsProviderState.Failed, ShortTtsError(ex));
            LogService.Write("Piper offline health probe failed", ex);
        }
    }

    private void QueueLocalVoicePreparation(string language)
    {
        if (!Settings.AutoPrepareLocalTts || string.IsNullOrWhiteSpace(language)) return;
        var token = _backgroundCts?.Token ?? CancellationToken.None;
        LogService.Info($"Piper background preparation queued: language={language}");
        _ = PrepareLanguageInBackgroundAsync(language, token);
    }

    private async Task PrepareLanguageInBackgroundAsync(string language, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await _localTts.PrepareLanguageAsync(language, ct);
            RefreshLocalTtsHealth();
            LogService.Info($"Piper background preparation complete: language={language}; elapsed_ms={clock.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogService.Info($"Piper background preparation cancelled: language={language}; elapsed_ms={clock.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            LogService.Info($"Piper background preparation failed: language={language}; elapsed_ms={clock.ElapsedMilliseconds}");
            LogService.Write($"Piper voice preparation failed ({language})", ex);
            RefreshLocalTtsHealth(ex);
        }
    }

    private void OnPiperProgress(string message)
    {
        UpdatePiperHealth(TtsProviderState.Preparing, message);
    }

    private void OnPiperMemoryChanged(long? value)
    {
        lock (_ttsHealthLock) _piperMemoryMb = value;
        RaiseTtsHealthChanged();
    }

    private void RefreshLocalTtsHealth(Exception? lastError = null)
    {
        var installed = _localTts.InstalledLanguages();
        if (lastError is not null)
        {
            _piperVerified = false;
            UpdatePiperHealth(TtsProviderState.Failed, ShortTtsError(lastError));
            return;
        }

        if (_localTts.IsCoreReady)
        {
            if (_piperVerified)
                UpdatePiperHealth(TtsProviderState.Ready, "Offline synthesis works • RU / UK / EN installed");
            else
                UpdatePiperHealth(TtsProviderState.Installed, "RU / UK / EN installed • synthesis not yet verified");
        }
        else if (_localTts.IsRuntimeReady || installed.Count > 0)
        {
            var detail = installed.Count == 0 ? "Runtime installed; voices not downloaded yet" : "Partially ready: " + string.Join(", ", installed.Select(x => x.ToUpperInvariant()));
            UpdatePiperHealth(TtsProviderState.Missing, detail);
        }
        else
            UpdatePiperHealth(TtsProviderState.Missing, "Not prepared — download once to set it up");
    }

    private void UpdateEdgeHealth(TtsProviderState state, string detail)
    {
        lock (_ttsHealthLock) _edgeHealth = new TtsProviderSnapshot("edge", "Edge online", state, detail, DateTime.UtcNow);
        RaiseTtsHealthChanged();
    }

    private void UpdateGoogleHealth(TtsProviderState state, string detail)
    {
        lock (_ttsHealthLock) _googleHealth = new TtsProviderSnapshot("google", "Google fallback", state, detail, DateTime.UtcNow);
        RaiseTtsHealthChanged();
    }

    private void UpdatePiperHealth(TtsProviderState state, string detail)
    {
        lock (_ttsHealthLock) _piperHealth = new TtsProviderSnapshot("piper", "Piper local", state, detail, DateTime.UtcNow);
        RaiseTtsHealthChanged();
    }

    private void SetActiveTtsProvider(string name)
    {
        lock (_ttsHealthLock) _activeTtsProvider = name;
        RaiseTtsHealthChanged();
    }

    private void RaiseTtsHealthChanged() => TtsHealthChanged?.Invoke();

    private static string ShortTtsError(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null && current.Message.Length < 8) current = current.InnerException;
        var text = current.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 115) text = text[..115] + "…";
        return string.IsNullOrWhiteSpace(text) ? current.GetType().Name : text;
    }

    private void FoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (TemplateFolder f in e.NewItems) f.Templates.CollectionChanged += TemplatesChanged;
        RefreshHotkeys();
        ScheduleStateSave();
    }

    private void TemplatesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshHotkeys();
        ScheduleStateSave();
    }

    private void ScheduleStateSave()
    {
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    public IReadOnlyList<string> RefreshHotkeys()
    {
        var errors = _system.RegisterHotkeys(BuildHotkeyMap(Settings));
        LastHotkeyErrors = errors.Select(e => $"{DescribeAction(e.Action)} — {e.Hotkey}: {e.Reason}").ToList();
        if (LastHotkeyErrors.Count > 0) SetStatus("Some hotkeys are unavailable. Open Settings → Hotkeys.");
        return LastHotkeyErrors;
    }

    private Dictionary<string, string> BuildHotkeyMap(AppSettings settings, IEnumerable<TemplateFolder>? folders = null)
    {
        var map = new Dictionary<string, string>
        {
            [ActionPanel] = settings.HotkeyPanel,
            [ActionSpeak] = settings.HotkeySpeak,
            [ActionTranslate] = settings.HotkeyTranslate,
            [ActionTranslateSpeak] = settings.HotkeyTranslateSpeak
        };
        foreach (var template in (folders ?? TemplateFolders).SelectMany(f => f.Templates).Where(t => !string.IsNullOrWhiteSpace(t.Hotkey)))
            map[TemplatePrefix + template.Id.ToString("N")] = template.Hotkey;
        return map;
    }

    public IReadOnlyList<string> ValidateHotkeyConfiguration(AppSettings settings, IEnumerable<TemplateFolder>? folders = null)
    {
        var issues = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in BuildHotkeyMap(settings, folders))
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                if (!pair.Key.StartsWith(TemplatePrefix, StringComparison.Ordinal)) issues.Add($"{DescribeAction(pair.Key, folders)}: no shortcut is assigned.");
                continue;
            }
            if (!HotkeyParser.TryParse(pair.Value, out _, out _, out var normalized, out var error))
            {
                issues.Add($"{DescribeAction(pair.Key, folders)}: {error}.");
                continue;
            }
            if (seen.TryGetValue(normalized, out var other))
                issues.Add($"{DescribeAction(pair.Key, folders)} and {DescribeAction(other, folders)} use the same shortcut {normalized}.");
            else seen[normalized] = pair.Key;
        }
        return issues;
    }

    private string DescribeAction(string action, IEnumerable<TemplateFolder>? folders = null)
    {
        if (action == ActionPanel) return "Open panel";
        if (action == ActionSpeak) return "Read aloud";
        if (action == ActionTranslate) return "Translate";
        if (action == ActionTranslateSpeak) return "Translate and speak";
        if (action.StartsWith(TemplatePrefix, StringComparison.Ordinal) && Guid.TryParseExact(action[TemplatePrefix.Length..], "N", out var id))
        {
            var t = (folders ?? TemplateFolders).SelectMany(f => f.Templates).FirstOrDefault(x => x.Id == id);
            if (t is not null) return $"Template “{t.Name}”";
        }
        return action;
    }

    private void OnHotkey(string action)
    {
        var dispatchClock = Stopwatch.StartNew();
        var pointer = System.Windows.Forms.Cursor.Position;
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            LogService.Info($"Hotkey dispatched: action={action}; dispatcher_delay_ms={dispatchClock.ElapsedMilliseconds}");
            if (action == ActionPanel) TogglePanel();
            else if (action == ActionSpeak) await SpeakCurrentAsync(captureSelection: true, entryPoint: "global-hotkey");
            else if (action == ActionTranslate) await TranslateCurrentAsync(false, captureSelection: true, entryPoint: "global-hotkey", popupScreenX: pointer.X, popupScreenY: pointer.Y);
            else if (action == ActionTranslateSpeak) await TranslateCurrentAsync(true, captureSelection: true, entryPoint: "global-hotkey", popupScreenX: pointer.X, popupScreenY: pointer.Y);
            else if (action.StartsWith(TemplatePrefix, StringComparison.Ordinal)) await UseTemplateHotkeyAsync(action);
        });
    }

    private void OnTemplateMenuRequested(InputTarget target, int x, int y)
    {
        if (!Settings.EnableAltRightClickTemplates) return;
        TemplateMenuRequested?.Invoke(target, x, y);
    }

    private void OnGlobalMouseDown(int x, int y) => GlobalMouseDown?.Invoke(x, y);

    public void SetGlobalMouseTracking(bool enabled)
    {
        if (_globalMouseTracking == enabled) return;
        _globalMouseTracking = enabled;
        if (enabled) _system.GlobalMouseDown += OnGlobalMouseDown;
        else _system.GlobalMouseDown -= OnGlobalMouseDown;
    }

    private async Task UseTemplateHotkeyAsync(string action)
    {
        if (!Guid.TryParseExact(action[TemplatePrefix.Length..], "N", out var id)) return;
        var template = TemplateFolders.SelectMany(f => f.Templates).FirstOrDefault(t => t.Id == id);
        if (template is null) return;
        var target = _system.CaptureForegroundInputTarget();
        await InsertTemplateIntoWindowAsync(template, target);
    }

    private void OnClipboardChanged()
    {
        var sequence = _system.ClipboardSequenceNumber;
        if (!_monitoring || sequence == 0 || sequence == _lastOwnClipboardSequence)
        {
            LogService.Info($"Clipboard notification ignored: monitoring={_monitoring}; sequence={sequence}; own_sequence={_lastOwnClipboardSequence}");
            return;
        }
        // Clipboard notifications are asynchronous. A queued notification for the
        // temporary Ctrl+C selection can arrive after we restored the user's clipboard.
        Application.Current.Dispatcher.BeginInvoke(new Action(() => CaptureClipboard(sequence)));
    }

    private void CaptureClipboard(uint observedSequence)
    {
        var currentSequence = _system.ClipboardSequenceNumber;
        if (!_monitoring || currentSequence != observedSequence || observedSequence == _lastOwnClipboardSequence)
        {
            LogService.Info($"Clipboard capture dropped as stale/internal: observed_sequence={observedSequence}; current_sequence={currentSequence}; own_sequence={_lastOwnClipboardSequence}; monitoring={_monitoring}");
            return;
        }
        try
        {
            var item = ClipboardService.ReadCurrentItem();
            if (item is null)
            {
                ActiveHistoryItemId = null;
                ActiveTemplateId = null;
                ActiveSelectionChanged?.Invoke();
                return;
            }
            if (item.Kind == ClipboardItemKind.Text && item.Text.Length > MaxHistoryTextChars)
            {
                ActiveHistoryItemId = null;
                ActiveTemplateId = null;
                ActiveSelectionChanged?.Invoke();
                SetStatus("The copied text is too large for ClipVoice history.");
                return;
            }
            AddHistoryItem(item);
            SetStatus(item.Kind == ClipboardItemKind.Text ? "Text added to history" : "Files added to history");
        }
        catch (Exception ex) { SetStatus("Clipboard is temporarily unavailable: " + ex.Message); }
    }

    public void SyncClipboardFromSystem(bool addToHistory = true)
    {
        try
        {
            var current = ClipboardService.ReadCurrentItem();
            if (current is null)
            {
                ActiveHistoryItemId = null;
                ActiveTemplateId = null;
                ActiveSelectionChanged?.Invoke();
                return;
            }

            var existing = History.FirstOrDefault(x => x.HasSameContentAs(current));
            if (addToHistory)
            {
                if (current.Kind == ClipboardItemKind.Text && current.Text.Length > MaxHistoryTextChars)
                {
                    ActiveHistoryItemId = null;
                    ActiveTemplateId = null;
                    ActiveSelectionChanged?.Invoke();
                    return;
                }

                // Opening the panel should not fabricate a new copy event or timestamp.
                // If the real clipboard is already in history, only move that existing entry to the top.
                if (existing is not null)
                {
                    if (!ReferenceEquals(History.FirstOrDefault(), existing))
                    {
                        History.Remove(existing);
                        History.Insert(0, existing);
                    }
                    SetActiveHistory(existing);
                }
                else AddHistoryItem(current);
                return;
            }

            if (existing is not null) SetActiveHistory(existing);
            else
            {
                ActiveHistoryItemId = null;
                ActiveTemplateId = null;
                ActiveSelectionChanged?.Invoke();
            }
        }
        catch { }
    }

    private void AddHistoryItem(ClipboardItem item)
    {
        var existing = History.FirstOrDefault(x => x.HasSameContentAs(item));
        if (existing is not null) History.Remove(existing);
        History.Insert(0, item);
        while (History.Count > Settings.HistoryLimit) History.RemoveAt(History.Count - 1);
        SetActiveHistory(item);
    }

    private void AddTextToHistory(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxHistoryTextChars) return;
        AddHistoryItem(new ClipboardItem { Kind = ClipboardItemKind.Text, Text = text });
    }

    private void MarkOwnClipboardWrite()
    {
        _lastOwnClipboardSequence = _system.ClipboardSequenceNumber;
    }

    public void ActivateClipboardItem(ClipboardItem item)
    {
        try
        {
            SetClipboardItem(item);
            SetStatus("The item is selected. Ctrl+V will paste it.");
        }
        catch (Exception ex) { SetStatus("Clipboard error: " + ex.Message); }
    }

    private void SetClipboardItem(ClipboardItem item)
    {
        if (item.Kind == ClipboardItemKind.Text) ClipboardService.SetText(item.Text);
        else ClipboardService.SetFiles(item.Files);
        MarkOwnClipboardWrite();
        SetActiveHistory(item);
    }

    public async Task PasteClipboardItemAsync(ClipboardItem item)
    {
        ActivateClipboardItem(item);
        await PasteToLastTargetAsync();
    }

    public async Task PasteClipboardItemAsync(ClipboardItem item, InputTarget target)
    {
        try
        {
            SetClipboardItem(item);
            if (Settings.HidePanelAfterPaste) _main?.Hide();
            var ok = await _system.SendPasteShortcutAsync(target);
            SetStatus(ok ? "Pasted" : "The item is on the clipboard. If the target app is running as administrator, press Ctrl+V manually.");
        }
        catch (Exception ex)
        {
            SetStatus("Clipboard error: " + ex.Message);
        }
    }

    public async Task PasteTemplateAsync(TextTemplate template)
    {
        await InsertTemplateIntoWindowAsync(template, _lastInputTarget);
    }

    public async Task InsertTemplateIntoWindowAsync(TextTemplate template, InputTarget target)
    {
        if (!target.IsValid)
        {
            SetStatus($"Could not find a field for inserting template “{template.Name}”.");
            return;
        }

        try
        {
            if (Settings.HidePanelAfterPaste) _main?.Hide();
            var ok = _system.IsBrowserTarget(target)
                ? await PasteTemplateThroughClipboardAsync(target, template.Content ?? string.Empty)
                : await _system.SendUnicodeTextAsync(target, template.Content ?? string.Empty);
            SetStatus(ok
                ? $"Inserted template “{template.Name}” • clipboard unchanged"
                : $"Could not insert template “{template.Name}”. Release Alt/Ctrl/Shift and check that ClipVoice and the target app run at the same privilege level.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Write("Template insertion failed", ex);
            SetStatus("Could not insert the template: " + ex.Message);
        }
    }

    private async Task<bool> PasteTemplateThroughClipboardAsync(InputTarget target, string text, CancellationToken ct = default)
    {
        var previous = ClipboardService.ReadCurrentItem();
        var hadAnyClipboardData = ClipboardService.HasAnyData();
        if (previous is null && hadAnyClipboardData) return false;

        var wasMonitoring = _monitoring;
        _monitoring = false;
        try
        {
            ClipboardService.SetText(text, excludeFromSystemHistory: true);
            MarkOwnClipboardWrite();
            if (!await _system.SendPasteShortcutAsync(target, ct)) return false;
            // Give Chromium/Firefox time to read the clipboard after the paste event.
            await Task.Delay(180, ct);
            return true;
        }
        finally
        {
            try
            {
                if (previous is not null)
                {
                    if (previous.Kind == ClipboardItemKind.Text) ClipboardService.SetText(previous.Text);
                    else ClipboardService.SetFiles(previous.Files);
                    MarkOwnClipboardWrite();
                }
                else if (!hadAnyClipboardData)
                {
                    ClipboardService.Clear();
                    MarkOwnClipboardWrite();
                }
            }
            catch (Exception ex) { LogService.Write("Failed to restore clipboard after browser template paste", ex); }
            _monitoring = wasMonitoring;
        }
    }

    private async Task PasteToLastTargetAsync()
    {
        if (_lastTargetWindow == IntPtr.Zero)
        {
            SetStatus("The item is on the clipboard. Return to the target window and press Ctrl+V.");
            return;
        }
        if (Settings.HidePanelAfterPaste) _main?.Hide();
        var ok = await _system.SendPasteShortcutAsync(_lastTargetWindow);
        SetStatus(ok ? "Pasted" : "The item is on the clipboard. If the target app is running as administrator, press Ctrl+V manually.");
    }

    public string GetCurrentText() => ClipboardService.ReadText();

    public bool CopyDiagnosticLogsToClipboard()
    {
        var logs = LogService.ReadRecentLogs();
        if (string.IsNullOrWhiteSpace(logs)) return false;
        var wasMonitoring = _monitoring;
        _monitoring = false;
        try
        {
            ClipboardService.SetText(logs, excludeFromSystemHistory: true);
            MarkOwnClipboardWrite();
            return true;
        }
        finally { _monitoring = wasMonitoring; }
    }

    public Task<bool> RestoreInputTargetAsync(InputTarget target, CancellationToken ct = default)
        => _system.ActivateInputTargetAsync(target, ct);

    public string DescribeInputTarget(InputTarget target) => _system.DescribeInputTarget(target);

    public async Task<string?> CaptureSelectedTextForTemplateAsync(InputTarget target, CancellationToken ct = default, bool reportErrors = true)
    {
        var captureClock = Stopwatch.StartNew();
        LogService.Info($"Selection capture started: route=template-popup; target={_system.DescribeInputTarget(target)}");
        if (!target.IsValid)
        {
            if (reportErrors) SetStatus("Could not find a field to capture the selected text from.");
            return null;
        }

        // Native Edit/RichEdit controls expose their selection directly, so the clipboard is not touched at all.
        if (_system.TryReadSelectedText(target, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            LogService.Info($"Selection capture complete: method=direct-control; chars={direct.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return direct;
        }
        LogService.Info($"Selection capture method fallback: method=copy-shortcut; elapsed_ms={captureClock.ElapsedMilliseconds}");

        // Browsers, VS Code and many custom editors do not expose selection through Win32 Edit messages.
        // For them we temporarily use Ctrl+C, suppress ClipVoice history, and restore the previous supported clipboard item.
        var previous = ClipboardService.ReadCurrentItem();
        var hadAnyClipboardData = ClipboardService.HasAnyData();
        if (previous is null && hadAnyClipboardData)
        {
            if (reportErrors) SetStatus("Cannot safely capture the selection: the clipboard currently contains unsupported data (such as an image). Copy some text or clear the clipboard and try again.");
            return null;
        }

        var wasMonitoring = _monitoring;
        _monitoring = false;
        try
        {
            var before = _system.ClipboardSequenceNumber;
            var shortcutClock = Stopwatch.StartNew();
            if (!await _system.SendCopyShortcutAsync(target, ct))
            {
                LogService.Info($"Selection copy shortcut failed: elapsed_ms={shortcutClock.ElapsedMilliseconds}");
                if (reportErrors) SetStatus("Could not copy the selected text. Check that some text is selected.");
                return null;
            }

            string selected = string.Empty;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(25, ct);
                if (_system.ClipboardSequenceNumber == before) continue;
                selected = ClipboardService.ReadText();
                LogService.Info($"Selection capture complete: method=copy-shortcut; chars={selected.Length}; wait_ms={shortcutClock.ElapsedMilliseconds}; elapsed_ms={captureClock.ElapsedMilliseconds}");
                break;
            }

            if (string.IsNullOrWhiteSpace(selected) && _system.ClipboardSequenceNumber == before && _system.SendCopyMessage(target))
            {
                for (var i = 0; i < 16; i++)
                {
                    await Task.Delay(25, ct);
                    if (_system.ClipboardSequenceNumber == before) continue;
                    selected = ClipboardService.ReadText();
                    LogService.Info($"Selection capture complete: method=copy-message-fallback; chars={selected.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(selected))
            {
                LogService.Info($"Selection capture empty: elapsed_ms={captureClock.ElapsedMilliseconds}");
                if (reportErrors) SetStatus("No selected text was found. Select text and reopen the template menu.");
                return null;
            }
            return selected;
        }
        finally
        {
            try
            {
                if (previous is not null)
                {
                    if (previous.Kind == ClipboardItemKind.Text) ClipboardService.SetText(previous.Text);
                    else ClipboardService.SetFiles(previous.Files);
                    MarkOwnClipboardWrite();
                }
                else if (!hadAnyClipboardData)
                {
                    ClipboardService.Clear();
                    MarkOwnClipboardWrite();
                }
            }
            catch (Exception ex)
            {
                LogService.Write("Failed to restore clipboard after selection capture", ex);
            }
            _monitoring = wasMonitoring;
        }
    }

    public async Task<string> CaptureSelectionOrClipboardAsync(InputTarget target, CancellationToken ct = default)
    {
        var captureClock = Stopwatch.StartNew();
        LogService.Info($"Selection-or-clipboard requested: target={_system.DescribeInputTarget(target)}");
        // Snapshot the clipboard before trying Ctrl+C. CaptureSelectedTextForTemplateAsync
        // restores it after the attempt, so this remains the user's copied text when no
        // selection exists.
        var clipboardText = GetCurrentText();
        var selected = await CaptureSelectedTextForTemplateAsync(target, ct, reportErrors: false);
        var text = string.IsNullOrWhiteSpace(selected) ? clipboardText : selected;
        LogTextInput(string.IsNullOrWhiteSpace(selected) ? "clipboard" : "selection", text);
        LogService.Info($"Selection-or-clipboard complete: source={(string.IsNullOrWhiteSpace(selected) ? "clipboard" : "selection")}; chars={text.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
        return text;
    }

    public async Task SpeakSelectedTextAsync(string text)
    {
        using var operation = LogService.BeginOperation("speak", "context-menu");
        if (string.IsNullOrWhiteSpace(text)) return;
        LogTextInput("context-menu", text);
        CancelWork();
        _workCts = new CancellationTokenSource();
        try { await SpeakTextAsync(text, _workCts.Token); }
        catch (OperationCanceledException) { SetStatus("Stopped"); }
        catch (Exception ex)
        {
            LogService.Write("Selected text speech failed", ex);
            SetStatus("Speech error: " + FriendlyOnlineError(ex));
        }
    }

    public async Task TranslateAndSpeakSelectedTextAsync(string text)
    {
        using var operation = LogService.BeginOperation("translate-speak", "context-menu");
        if (string.IsNullOrWhiteSpace(text)) return;
        LogTextInput("context-menu-translate", text);
        CancelWork();
        _workCts = new CancellationTokenSource();
        try
        {
            var ct = _workCts.Token;
            var detectedLanguage = _translation.DetectLanguageLocally(text);
            var targetLanguage = TranslationDirectionService.ResolveTargetLanguage(
                detectedLanguage, Settings.PreferredCyrillicTranslationLanguage);
            var sourceName = string.IsNullOrWhiteSpace(detectedLanguage)
                ? "Foreign text"
                : LocalizationService.Translate(TranslationDirectionService.DisplayName(detectedLanguage), Settings.Language);
            var targetName = LocalizationService.Translate(TranslationDirectionService.DisplayName(targetLanguage), Settings.Language);
            SetStatus($"{sourceName} → {targetName} • translating…");

            var translateClock = Stopwatch.StartNew();
            var result = await _translation.TranslateDetailedAsync(text, targetLanguage, ct);
            LogService.Info($"Translation complete: source={detectedLanguage}; target={targetLanguage}; input_chars={text.Length}; output_chars={result.Text?.Length ?? 0}; elapsed_ms={translateClock.ElapsedMilliseconds}");
            if (string.IsNullOrWhiteSpace(result.Text))
                throw new InvalidOperationException("The translator returned empty text.");

            detectedLanguage = string.IsNullOrWhiteSpace(result.DetectedLanguage)
                ? detectedLanguage
                : result.DetectedLanguage;
            sourceName = LocalizationService.Translate(TranslationDirectionService.DisplayName(detectedLanguage), Settings.Language);
            ClipboardService.SetText(result.Text);
            MarkOwnClipboardWrite();
            AddTextToHistory(result.Text);

            SetStatus($"{sourceName} → {targetName} • translated, reading aloud…");
            await SpeakTextAsync(result.Text, ct, targetLanguage);
            SetStatus($"{sourceName} → {targetName} • translation read aloud");
        }
        catch (OperationCanceledException) { SetStatus("Stopped"); }
        catch (Exception ex)
        {
            LogService.Write("Selected text translate-and-speak failed", ex);
            SetStatus("Translation or speech error: " + FriendlyOnlineError(ex));
        }
    }

    public TextTemplate AddTemplateFromSelection(string content, string templateName, Guid? folderId, string? newFolderName)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Template text is empty.", nameof(content));
        if (string.IsNullOrWhiteSpace(templateName)) throw new ArgumentException("Template name is empty.", nameof(templateName));

        TemplateFolder? folder = null;
        if (folderId is Guid id) folder = TemplateFolders.FirstOrDefault(x => x.Id == id);

        if (folder is null)
        {
            var requestedName = string.IsNullOrWhiteSpace(newFolderName) ? "My templates" : newFolderName.Trim();
            folder = TemplateFolders.FirstOrDefault(x => string.Equals(x.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new TemplateFolder { Name = requestedName };
                TemplateFolders.Add(folder);
            }
        }

        var template = new TextTemplate
        {
            Name = templateName.Trim(),
            Content = content,
            Hotkey = string.Empty,
            PasteOnHotkey = true,
            IsSecret = false
        };
        folder.Templates.Add(template);
        SetStatus($"Created template “{template.Name}” in folder “{folder.Name}”.");
        return template;
    }

    private async Task<string> GetActionTextAsync(bool captureSelection, CancellationToken ct)
    {
        var captureClock = Stopwatch.StartNew();
        var clipboardText = GetCurrentText();
        if (!captureSelection || !Settings.AutoCaptureSelection)
        {
            LogTextInput("clipboard", clipboardText);
            LogService.Info($"Action text source resolved: source=clipboard; chars={clipboardText.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return clipboardText;
        }
        var target = _system.CaptureForegroundInputTarget();
        if (_main is not null && target.Window == new WindowInteropHelper(_main).Handle)
            target = _lastInputTarget; // The panel remembers the window that was active before it opened.
        if (!target.IsValid)
        {
            LogTextInput("clipboard-no-target", clipboardText);
            LogService.Info($"Action text source resolved: source=clipboard; reason=no-valid-target; target={_system.DescribeInputTarget(target)}; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return clipboardText;
        }
        LogService.Info($"Action selection capture target: {_system.DescribeInputTarget(target)}");

        try
        {
            var selected = await CaptureSelectedTextForActionAsync(target, ct);
            var text = string.IsNullOrWhiteSpace(selected) ? clipboardText : selected;
            LogTextInput(string.IsNullOrWhiteSpace(selected) ? "clipboard-no-selection" : "selection", text);
            LogService.Info($"Action text source resolved: source={(string.IsNullOrWhiteSpace(selected) ? "clipboard" : "selection")}; chars={text.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Write("Failed to capture selected text for speech", ex);
            LogTextInput("clipboard-selection-error", clipboardText);
            LogService.Info($"Action text source resolved: source=clipboard; reason=selection-error; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return clipboardText;
        }
    }

    private static void LogTextInput(string source, string text)
    {
        var leadingCodes = string.Join(",", text.Take(8).Select(c => $"U+{(int)c:X4}"));
        LogService.Info($"Action text captured: source={source}; chars={text.Length}; leading_char_codes=[{leadingCodes}]");
    }

    private static void LogSynthesizedAudio(string provider, string path, Stopwatch clock)
    {
        long bytes = 0;
        try { bytes = new FileInfo(path).Length; } catch { }
        LogService.Info($"TTS synthesis ready: provider={provider}; elapsed_ms={clock.ElapsedMilliseconds}; extension={Path.GetExtension(path)}; bytes={bytes}");
    }

    private async Task<string?> CaptureSelectedTextForActionAsync(InputTarget target, CancellationToken ct)
    {
        var captureClock = Stopwatch.StartNew();
        // Standard Edit/RichEdit controls expose the selection without touching the clipboard.
        if (_system.TryReadSelectedText(target, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            LogService.Info($"Selection capture complete: method=direct-control; chars={direct.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return direct;
        }
        LogService.Info($"Selection capture method fallback: method=copy-shortcut");

        // Other applications (browsers, office apps, custom editors) expose selection only
        // through Ctrl+C. Save and restore supported clipboard content so speech does not
        // replace the user's clipboard or add the selected text to ClipVoice history.
        var previous = ClipboardService.ReadCurrentItem();
        var hadAnyClipboardData = ClipboardService.HasAnyData();
        if (previous is null && hadAnyClipboardData)
        {
            LogService.Info($"Selection capture skipped: clipboard=unsupported-data; elapsed_ms={captureClock.ElapsedMilliseconds}");
            return null;
        }

        var wasMonitoring = _monitoring;
        _monitoring = false;
        try
        {
            var before = _system.ClipboardSequenceNumber;
            var shortcutClock = Stopwatch.StartNew();
            if (!await _system.SendCopyShortcutAsync(target, ct))
            {
                LogService.Info($"Selection copy shortcut failed: elapsed_ms={shortcutClock.ElapsedMilliseconds}");
                return null;
            }
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(25, ct);
                if (_system.ClipboardSequenceNumber == before) continue;
                var selected = ClipboardService.ReadText();
                LogService.Info($"Selection capture complete: method=copy-shortcut; chars={selected.Length}; wait_ms={shortcutClock.ElapsedMilliseconds}; elapsed_ms={captureClock.ElapsedMilliseconds}");
                return string.IsNullOrWhiteSpace(selected) ? null : selected;
            }

            if (_system.ClipboardSequenceNumber == before && _system.SendCopyMessage(target))
            {
                for (var i = 0; i < 16; i++)
                {
                    await Task.Delay(25, ct);
                    if (_system.ClipboardSequenceNumber == before) continue;
                    var selected = ClipboardService.ReadText();
                    LogService.Info($"Selection capture complete: method=copy-message-fallback; chars={selected.Length}; elapsed_ms={captureClock.ElapsedMilliseconds}");
                    return string.IsNullOrWhiteSpace(selected) ? null : selected;
                }
            }
            LogService.Info($"Selection capture empty: elapsed_ms={captureClock.ElapsedMilliseconds}");
            return null;
        }
        finally
        {
            try
            {
                if (previous is not null)
                {
                    if (previous.Kind == ClipboardItemKind.Text) ClipboardService.SetText(previous.Text);
                    else ClipboardService.SetFiles(previous.Files);
                    MarkOwnClipboardWrite();
                }
                else if (!hadAnyClipboardData)
                {
                    ClipboardService.Clear();
                    MarkOwnClipboardWrite();
                }
            }
            catch (Exception ex) { LogService.Write("Failed to restore clipboard after speech selection capture", ex); }
            _monitoring = wasMonitoring;
        }
    }

    public async Task SpeakCurrentAsync(bool captureSelection = false, string entryPoint = "direct")
    {
        using var operation = LogService.BeginOperation("speak", entryPoint);
        CancelWork();
        _workCts = new CancellationTokenSource();
        try
        {
            var text = await GetActionTextAsync(captureSelection, _workCts.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(captureSelection && Settings.AutoCaptureSelection
                    ? "Could not retrieve the selected text. Keep it selected and try again."
                    : "There is no text. Select some text or copy it to the clipboard.");
                return;
            }
            await SpeakTextAsync(text, _workCts.Token);
        }
        catch (OperationCanceledException) { SetStatus("Stopped"); }
        catch (Exception ex)
        {
            LogService.Write("Speech action failed", ex);
            SetStatus("Speech error: " + FriendlyOnlineError(ex));
        }
    }

    private async Task SpeakTextAsync(string text, CancellationToken ct, string? knownLanguage = null, bool preferFastOnline = false)
    {
        var requestClock = Stopwatch.StartNew();
        LogTextInput("tts-request", text);
        SetStatus("Preparing speech…");
        var languageClock = Stopwatch.StartNew();
        var language = string.IsNullOrWhiteSpace(knownLanguage)
            ? _translation.DetectLanguageLocally(text)
            : TranslationDirectionService.NormalizeLanguage(knownLanguage);
        if (string.IsNullOrWhiteSpace(language)) language = "en";
        LogService.Info($"TTS request prepared: language={language}; language_source={(string.IsNullOrWhiteSpace(knownLanguage) ? "local-detection" : "known")}; language_elapsed_ms={languageClock.ElapsedMilliseconds}; chars={text.Length}; prefer_fast_online={preferFastOnline}");

        if (text.Length >= 700)
        {
            await SpeakLongTextStreamingAsync(text, language, requestClock, ct);
            return;
        }

        Exception? edgeError = null;
        Exception? googleError = null;
        Exception? piperError = null;
        string? file = null;
        string provider = string.Empty;

        if (preferFastOnline)
        {
            var fastest = await SynthesizeFastestOnlineAsync(text, language, ct);
            file = fastest.File;
            provider = fastest.Provider;
            edgeError = fastest.EdgeError;
            googleError = fastest.GoogleError;
        }

        if (file is null && !preferFastOnline)
        {
            UpdateEdgeHealth(TtsProviderState.Checking, "Generating speech…");
            try
            {
                var synthClock = Stopwatch.StartNew();
                LogService.Info($"TTS synthesis started: provider=Edge; language={language}; chars={text.Length}; rate_percent={Settings.SpeechRatePercent}");
                file = await _tts.SynthesizeAsync(text, Settings, language, ct);
                provider = "Edge online";
                LogSynthesizedAudio("Edge", file, synthClock);
                UpdateEdgeHealth(TtsProviderState.Ready, "Ready");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                edgeError = ex;
                UpdateEdgeHealth(TtsProviderState.Failed, ShortTtsError(ex));
                LogService.Write("Edge TTS failed", ex);
            }
        }

        if (file is null && !preferFastOnline)
        {
            SetStatus("Edge is unavailable — trying online speech fallback…");
            UpdateGoogleHealth(TtsProviderState.Checking, "Generating speech…");
            try
            {
                var synthClock = Stopwatch.StartNew();
                LogService.Info($"TTS synthesis started: provider=Google; language={language}; chars={text.Length}; rate_percent={Settings.SpeechRatePercent}");
                file = await _fallbackTts.SynthesizeAsync(text, language, Settings.SpeechRatePercent, ct);
                provider = "Google fallback";
                LogSynthesizedAudio("Google", file, synthClock);
                UpdateGoogleHealth(TtsProviderState.Ready, "Ready as an online fallback");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                googleError = ex;
                UpdateGoogleHealth(TtsProviderState.Failed, ShortTtsError(ex));
                LogService.Write("Google fallback TTS failed", ex);
            }
        }

        if (file is null)
        {
            SetStatus("Online speech is unavailable — switching to local Piper…");
            UpdatePiperHealth(_localTts.IsRuntimeReady ? TtsProviderState.Checking : TtsProviderState.Preparing,
                _localTts.IsRuntimeReady ? "Generating speech offline…" : "Preparing the offline fallback…");
            try
            {
                var synthClock = Stopwatch.StartNew();
                LogService.Info($"TTS synthesis started: provider=Piper; language={language}; chars={text.Length}; rate_percent={Settings.SpeechRatePercent}");
                // Free managed memory before starting the short-lived neural process.
                GC.Collect(1, GCCollectionMode.Optimized, blocking: false, compacting: false);
                file = await _localTts.SynthesizeAsync(text, language, Settings.SpeechRatePercent, ct);
                provider = "Piper local";
                LogSynthesizedAudio("Piper", file, synthClock);
                _piperVerified = true;
                UpdatePiperHealth(TtsProviderState.Ready, $"Offline synthesis works • {language.ToUpperInvariant()}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                piperError = ex;
                RefreshLocalTtsHealth(ex);
                LogService.Write("Piper local TTS failed", ex);
            }
        }

        if (file is null)
        {
            var parts = new[]
            {
                edgeError is null ? null : "Edge: " + ShortTtsError(edgeError),
                googleError is null ? null : "Google: " + ShortTtsError(googleError),
                piperError is null ? null : "Piper: " + ShortTtsError(piperError)
            }.Where(x => x is not null);
            throw new InvalidOperationException("All speech engines are unavailable. " + string.Join(" • ", parts));
        }

        if (!string.Equals(provider, "Piper local", StringComparison.OrdinalIgnoreCase))
            QueueLocalVoicePreparation(language);
        LogService.Info($"TTS synthesis complete: provider={provider}; elapsed_ms={requestClock.ElapsedMilliseconds}; rate_percent={Settings.SpeechRatePercent}");
        LogService.Info($"TTS playback requested: provider={provider}; synth_elapsed_ms={requestClock.ElapsedMilliseconds}");
        _audio.Play(file, provider, text, Settings.SpeechRatePercent);
        SetActiveTtsProvider(provider);
        SetStatus($"Speaking • {provider} • {language.ToUpperInvariant()}");
    }

    private async Task SpeakLongTextStreamingAsync(string text, string language, Stopwatch requestClock, CancellationToken ct)
    {
        var parts = SplitIntoSpeechParts(text, 500);
        LogService.Info($"TTS streaming started: chars={text.Length}; parts={parts.Count}; max_part_chars=500; prefetch=2");
        var channel = Channel.CreateBounded<(int Index, string Text, string Path, string Provider)>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        var producer = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var partClock = Stopwatch.StartNew();
                    LogService.Info($"TTS streaming part synthesis started: part={i + 1}/{parts.Count}; chars={parts[i].Length}");
                    var result = await SynthesizeSpeechPartAsync(parts[i], language, ct);
                    LogService.Info($"TTS streaming part ready: part={i + 1}/{parts.Count}; provider={result.Provider}; elapsed_ms={partClock.ElapsedMilliseconds}; bytes={new FileInfo(result.Path).Length}");
                    await channel.Writer.WriteAsync((i + 1, parts[i], result.Path, result.Provider), ct);
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        var firstProvider = string.Empty;
        var played = 0;
        try
        {
            await foreach (var part in channel.Reader.ReadAllAsync(ct))
            {
                if (played == 0) firstProvider = part.Provider;
                played++;
                SetStatus($"Speaking in parts • {played}/{parts.Count}");
                LogService.Info($"TTS streaming playback started: part={part.Index}/{parts.Count}; provider={part.Provider}");
                await _audio.PlayAndWaitAsync(part.Path, part.Provider, part.Text, Settings.SpeechRatePercent, ct);
                LogService.Info($"TTS streaming playback completed: part={part.Index}/{parts.Count}");
            }
            await producer;
        }
        catch
        {
            try { await producer; } catch { }
            throw;
        }

        SetActiveTtsProvider(string.IsNullOrWhiteSpace(firstProvider) ? "—" : firstProvider + " (streaming)");
        LogService.Info($"TTS streaming complete: played_parts={played}/{parts.Count}; elapsed_ms={requestClock.ElapsedMilliseconds}");
        SetStatus($"Speech complete • {firstProvider} • {language.ToUpperInvariant()}");
    }

    private async Task<(string Path, string Provider)> SynthesizeSpeechPartAsync(string text, string language, CancellationToken ct)
    {
        var fastest = await SynthesizeFastestOnlineAsync(text, language, ct);
        if (fastest.File is not null) return (fastest.File, fastest.Provider);

        Exception? piperError = null;
        try
        {
            LogService.Info($"TTS streaming using Piper fallback: chars={text.Length}");
            var path = await _localTts.SynthesizeAsync(text, language, Settings.SpeechRatePercent, ct);
            _piperVerified = true;
            UpdatePiperHealth(TtsProviderState.Ready, $"Offline synthesis works • {language.ToUpperInvariant()}");
            return (path, "Piper local");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            piperError = ex;
            RefreshLocalTtsHealth(ex);
            LogService.Write("Piper streaming TTS fallback failed", ex);
        }

        var errors = new[]
        {
            fastest.EdgeError is null ? null : "Edge: " + ShortTtsError(fastest.EdgeError),
            fastest.GoogleError is null ? null : "Google: " + ShortTtsError(fastest.GoogleError),
            piperError is null ? null : "Piper: " + ShortTtsError(piperError)
        }.Where(x => x is not null);
        throw new InvalidOperationException("Could not read the text passage aloud. " + string.Join(" • ", errors));
    }

    private static List<string> SplitIntoSpeechParts(string text, int maxChars)
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?…])\s+")
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var parts = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            var value = current.ToString().Trim();
            if (value.Length > 0) parts.Add(value);
            current.Clear();
        }

        foreach (var sentence in sentences)
        {
            var remaining = sentence.Trim();
            while (remaining.Length > maxChars)
            {
                var cut = remaining.LastIndexOf(' ', maxChars - 1, maxChars);
                if (cut < maxChars / 2) cut = maxChars;
                var fragment = remaining[..cut].Trim();
                if (fragment.Length > 0)
                {
                    if (current.Length > 0) Flush();
                    parts.Add(fragment);
                }
                remaining = remaining[cut..].TrimStart();
            }

            if (remaining.Length == 0) continue;
            if (current.Length > 0 && current.Length + 1 + remaining.Length > maxChars) Flush();
            if (current.Length > 0) current.Append(' ');
            current.Append(remaining);
        }
        Flush();
        return parts;
    }

    public async Task<string> DiagnoseTtsSpeechSpeedAsync(CancellationToken ct = default)
    {
        using var operation = LogService.BeginOperation("tts-speed-diagnostic", "settings");
        const string sample = "This is a sample sentence for measuring speech synthesis speed. ClipVoice reads the same English text with identical settings in each engine. Compare the generated audio duration and the estimated number of words spoken per minute. This sentence is used only for testing.";
        var words = sample.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var rate = Math.Clamp(Settings.SpeechRatePercent, -50, 100);
        var ratesToTest = rate == 0 ? new[] { 0 } : new[] { 0, rate };
        var results = new List<string>();
        LogService.Info($"TTS speed diagnostic started: language=ru; chars={sample.Length}; words={words}; requested_rate_percent={rate}; tested_rates_percent={string.Join(",", ratesToTest)}; same_sample_for_all_providers=true");

        foreach (var testRate in ratesToTest)
        {
            ct.ThrowIfCancellationRequested();
            results.Add($"Speed {testRate}%:");
            LogService.Info($"TTS speed diagnostic rate started: rate_setting_percent={testRate}");

            var edgeSettings = new AppSettings
            {
                SpeechRatePercent = testRate,
                EnglishVoice = Settings.EnglishVoice
            };
            results.Add(await MeasureProviderSpeedAsync("Edge", "ssml_rate", $"{(testRate >= 0 ? "+" : string.Empty)}{testRate}%", sample,
                token => _tts.SynthesizeAsync(sample, edgeSettings, "en", token), words, testRate, ct) ?? "Edge: failed — see error log for details");
            results.Add(await MeasureProviderSpeedAsync("Google", "ttsspeed", (1.0 + testRate / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), sample,
                token => _fallbackTts.SynthesizeAsync(sample, "en", testRate, token), words, testRate, ct) ?? "Google: failed — see error log for details");
            if (_localTts.IsCoreReady)
            {
                results.Add(await MeasureProviderSpeedAsync("Piper", "length_scale", (1.0 / Math.Clamp(1.0 + testRate / 100.0, 0.5, 2.0)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), sample,
                    token => _localTts.SynthesizeAsync(sample, "en", testRate, token), words, testRate, ct) ?? "Piper: failed — see error log for details");
            }
            else
            {
                results.Add("Piper: skipped — runtime is not prepared");
                LogService.Info($"TTS speed diagnostic skipped: provider=Piper local; rate_setting_percent={testRate}; reason=runtime_or_English_voice_not_prepared");
            }
        }

        LogService.Info($"TTS speed diagnostic finished");
        SetStatus("Speed comparison completed");
        return string.Join(Environment.NewLine, results);
    }

    private async Task<string?> MeasureProviderSpeedAsync(string provider, string rateParameter, string rateValue, string sample,
        Func<CancellationToken, Task<string>> synthesize, int words, int ratePercent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        try
        {
            LogService.Info($"TTS speed diagnostic provider started: provider={provider}; parameter={rateParameter}; value={rateValue}");
            var path = await synthesize(timeout.Token);
            var duration = await AudioPlaybackService.MeasureDurationAsync(path, timeout.Token);
            var wpm = duration.TotalSeconds > 0 ? words * 60d / duration.TotalSeconds : 0;
            LogService.Info($"TTS speed diagnostic result: provider={provider}; chars={sample.Length}; words={words}; rate_setting_percent={ratePercent}; rate_parameter={rateParameter}; rate_value={rateValue}; audio_duration_ms={duration.TotalMilliseconds:F0}; estimated_audio_wpm={wpm:F1}; synthesis_and_measurement_ms={clock.ElapsedMilliseconds}");
            return $"{provider}: {wpm:F0} words/min ({duration.TotalSeconds:F1} s)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Write($"TTS speed diagnostic failed for {provider}", ex);
            return null;
        }
    }

    private async Task<(string? File, string Provider, Exception? EdgeError, Exception? GoogleError)>
        SynthesizeFastestOnlineAsync(string text, string language, CancellationToken ct)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        UpdateEdgeHealth(TtsProviderState.Checking, "Starting in parallel…");
        UpdateGoogleHealth(TtsProviderState.Checking, "Starting in parallel…");

        var edgeTask = _tts.SynthesizeAsync(text, Settings, language, raceCts.Token);
        var googleTask = _fallbackTts.SynthesizeAsync(text, language, Settings.SpeechRatePercent, raceCts.Token);
        var pending = new Dictionary<Task<string>, string>
        {
            [edgeTask] = "Edge online",
            [googleTask] = "Google fallback"
        };
        Exception? edgeError = null;
        Exception? googleError = null;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var completed = await Task.WhenAny(pending.Keys);
            var completedProvider = pending[completed];
            pending.Remove(completed);

            try
            {
                var path = await completed;
                raceCts.Cancel();
                if (completedProvider == "Edge online") UpdateEdgeHealth(TtsProviderState.Ready, "Ready");
                else UpdateGoogleHealth(TtsProviderState.Ready, "Ready as an online fallback");

                // Observe cancellation/errors from the slower request without making
                // the user wait for it before playback begins.
                foreach (var pair in pending) _ = ObserveSynthesisTaskAsync(pair.Key, pair.Value);
                return (path, completedProvider, edgeError, googleError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                raceCts.Cancel();
                foreach (var pair in pending) _ = ObserveSynthesisTaskAsync(pair.Key, pair.Value);
                throw;
            }
            catch (Exception ex)
            {
                if (completedProvider == "Edge online")
                {
                    edgeError = ex;
                    UpdateEdgeHealth(TtsProviderState.Failed, ShortTtsError(ex));
                    LogService.Write("Edge TTS failed during fast selected-text speech", ex);
                }
                else
                {
                    googleError = ex;
                    UpdateGoogleHealth(TtsProviderState.Failed, ShortTtsError(ex));
                    LogService.Write("Google TTS failed during fast selected-text speech", ex);
                }
            }
        }

        raceCts.Cancel();
        return (null, string.Empty, edgeError, googleError);
    }

    private async Task ObserveSynthesisTaskAsync(Task<string> task, string provider)
    {
        try
        {
            await task;
            if (provider == "Edge online") UpdateEdgeHealth(TtsProviderState.Ready, "Ready");
            else UpdateGoogleHealth(TtsProviderState.Ready, "Ready as an online fallback");
        }
        catch (OperationCanceledException)
        {
            if (provider == "Edge online") UpdateEdgeHealth(TtsProviderState.Unknown, "Request cancelled after a faster engine started");
            else UpdateGoogleHealth(TtsProviderState.Unknown, "Request cancelled after a faster engine started");
        }
        catch (Exception ex)
        {
            if (provider == "Edge online") UpdateEdgeHealth(TtsProviderState.Failed, ShortTtsError(ex));
            else UpdateGoogleHealth(TtsProviderState.Failed, ShortTtsError(ex));
        }
    }

    public Task TranslateSelectedTextAsync(string text, int screenX, int screenY) =>
        TranslateCurrentAsync(false, entryPoint: "context-menu", selectedText: text, popupScreenX: screenX, popupScreenY: screenY);

    public async Task TranslateCurrentAsync(
        bool speakAfter,
        bool captureSelection = false,
        string entryPoint = "direct",
        string? selectedText = null,
        int? popupScreenX = null,
        int? popupScreenY = null)
    {
        using var operation = LogService.BeginOperation(speakAfter ? "translate-speak" : "translate", entryPoint);
        var pointer = System.Windows.Forms.Cursor.Position;
        var resultScreenX = popupScreenX ?? pointer.X;
        var resultScreenY = popupScreenY ?? pointer.Y;
        CancelWork();
        _workCts = new CancellationTokenSource();
        try
        {
            var text = selectedText ?? await GetActionTextAsync(captureSelection, _workCts.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(captureSelection && Settings.AutoCaptureSelection
                    ? "Could not retrieve the selected text. Keep it selected and try again."
                    : "There is no text. Select some text or copy it to the clipboard.");
                return;
            }

            var detectedLanguage = _translation.DetectLanguageLocally(text);
            var targetLanguage = TranslationDirectionService.ResolveTargetLanguage(
                detectedLanguage, Settings.PreferredCyrillicTranslationLanguage);

            var sourceName = string.IsNullOrWhiteSpace(detectedLanguage)
                ? "Foreign text"
                : LocalizationService.Translate(TranslationDirectionService.DisplayName(detectedLanguage), Settings.Language);
            var targetName = LocalizationService.Translate(TranslationDirectionService.DisplayName(targetLanguage), Settings.Language);
            SetStatus($"{sourceName} → {targetName} • translating…");

            var translateClock = Stopwatch.StartNew();
            var result = await _translation.TranslateDetailedAsync(text, targetLanguage, _workCts.Token);
            LogService.Info($"Translation complete: source={detectedLanguage}; target={targetLanguage}; input_chars={text.Length}; output_chars={result.Text?.Length ?? 0}; elapsed_ms={translateClock.ElapsedMilliseconds}");
            if (!string.IsNullOrWhiteSpace(result.DetectedLanguage))
            {
                detectedLanguage = result.DetectedLanguage;
                sourceName = LocalizationService.Translate(TranslationDirectionService.DisplayName(detectedLanguage), Settings.Language);
            }

            var translated = result.Text;
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException("The translator returned empty text.");
            ClipboardService.SetText(translated);
            MarkOwnClipboardWrite();
            AddTextToHistory(translated);
            if (!speakAfter)
                _main?.ShowTranslationPopup(translated, $"{sourceName} → {targetName}", resultScreenX, resultScreenY);

            if (speakAfter)
            {
                SetStatus($"{sourceName} → {targetName} • translation ready, generating speech…");
                await SpeakTextAsync(translated, _workCts.Token, targetLanguage);
                SetStatus($"{sourceName} → {targetName} • speaking • ready for Ctrl+V");
            }
            else
            {
                SetStatus($"{sourceName} → {targetName} • ready for Ctrl+V");
            }
        }
        catch (OperationCanceledException) { SetStatus("Stopped"); }
        catch (Exception ex)
        {
            LogService.Write("Translation failed", ex);
            SetStatus("Translation error: " + FriendlyOnlineError(ex));
        }
    }

    private static string FriendlyOnlineError(Exception ex)
    {
        if (ex is HttpRequestException) return "Cannot connect to the online service. Check your internet connection.";
        return ex.Message;
    }

    public void StopSpeech()
    {
        CancelWork();
        _audio.Stop();
        SetStatus("Stopped");
    }

    private void CancelWork()
    {
        try { _workCts?.Cancel(); } catch { }
        _workCts?.Dispose();
        _workCts = null;
    }

    public void ClearHistory()
    {
        History.Clear();
        ActiveHistoryItemId = null;
        ActiveTemplateId = null;
        // Clearing ClipVoice history must not pretend that the Windows clipboard itself is empty.
        // Keep the current clipboard as the first item, if it contains supported data.
        SyncClipboardFromSystem(addToHistory: true);
        SetStatus(History.Count > 0 ? "History cleared • current clipboard item retained" : "History cleared");
    }

    private void SetActiveHistory(ClipboardItem item)
    {
        ActiveHistoryItemId = item.Id;
        ActiveTemplateId = null;
        ActiveSelectionChanged?.Invoke();
    }

    private void SetActiveTemplate(TextTemplate template)
    {
        ActiveTemplateId = template.Id;
        ActiveHistoryItemId = null;
        ActiveSelectionChanged?.Invoke();
    }

    public void ShowPanel()
    {
        if (_main is null) return;
        var inputTarget = _system.CaptureForegroundInputTarget();
        var foreground = inputTarget.Window;
        var mainHandle = new WindowInteropHelper(_main).Handle;
        var edgeHandle = _handle is null ? IntPtr.Zero : new WindowInteropHelper(_handle).Handle;
        if (foreground != IntPtr.Zero && foreground != mainHandle && foreground != edgeHandle)
        {
            _lastTargetWindow = foreground;
            _lastInputTarget = inputTarget;
        }

        // Re-read the real Windows clipboard every time the panel opens. This prevents
        // a stale "active" card if an update notification was missed or intentionally ignored.
        SyncClipboardFromSystem(addToHistory: true);

        var area = SystemParameters.WorkArea;
        _main.Left = area.Right - _main.Width - 18;
        _main.Top = Math.Max(area.Top + 30, area.Top + (area.Height - _main.Height) / 2);
        _main.Show();
        _main.Activate();
    }

    public void TogglePanel()
    {
        if (_main is null) return;
        if (_main.IsVisible && _main.IsActive) _main.Hide(); else ShowPanel();
    }

    public void ToggleClipboardMonitoring()
    {
        _monitoring = !_monitoring;
        SetStatus(_monitoring ? "Clipboard monitoring enabled" : "Clipboard monitoring paused");
    }

    public void ReplaceTemplateFolders(IEnumerable<TemplateFolder> folders)
    {
        TemplateFolders.CollectionChanged -= FoldersChanged;
        foreach (var old in TemplateFolders) old.Templates.CollectionChanged -= TemplatesChanged;
        TemplateFolders.Clear();
        foreach (var source in folders)
        {
            var folder = new TemplateFolder
            {
                Id = source.Id,
                Name = source.Name,
                Templates = new ObservableCollection<TextTemplate>(source.Templates.Select(t => new TextTemplate
                {
                    Id = t.Id, Name = t.Name, Content = t.Content, Hotkey = t.Hotkey, PasteOnHotkey = t.PasteOnHotkey, IsSecret = t.IsSecret
                }))
            };
            folder.Templates.CollectionChanged += TemplatesChanged;
            TemplateFolders.Add(folder);
        }
        if (TemplateFolders.Count == 0)
        {
            var folder = new TemplateFolder { Name = "My templates" };
            folder.Templates.CollectionChanged += TemplatesChanged;
            TemplateFolders.Add(folder);
        }
        TemplateFolders.CollectionChanged += FoldersChanged;
        if (ActiveTemplateId is Guid activeTemplateId && !TemplateFolders.SelectMany(f => f.Templates).Any(t => t.Id == activeTemplateId))
        {
            ActiveTemplateId = null;
            ActiveSelectionChanged?.Invoke();
        }
        SaveState();
        RefreshHotkeys();
    }

    public IReadOnlyList<string> UpdateSettings(AppSettings settings)
    {
        settings.HistoryLimit = 25;
        var validation = ValidateHotkeyConfiguration(settings);
        if (validation.Count > 0) return validation;
        Settings = settings;
        _system.TemplateMouseGestureEnabled = Settings.EnableAltRightClickTemplates;
        _system.TemplateMouseGestureModifier = Settings.TemplateMouseModifier;
        _storage.SaveSettings(Settings);
        if (Settings.AutoPrepareLocalTts && !_localTts.IsCoreReady)
            _ = PrepareLocalTtsAsync(background: true, _backgroundCts?.Token ?? CancellationToken.None);
        var registration = RefreshHotkeys();
        ApplyAutoStart(Settings.AutoStart);
        if (_handle is not null)
        {
            if (Settings.ShowEdgeHandle && !_handle.IsVisible) _handle.Show();
            else if (!Settings.ShowEdgeHandle && _handle.IsVisible) _handle.Hide();
        }
        while (History.Count > Settings.HistoryLimit) History.RemoveAt(History.Count - 1);
        SaveState();
        SetStatus(registration.Count == 0 ? "Settings saved" : "Settings saved, but there is a hotkey conflict");
        return registration;
    }

    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (!enabled) { key.DeleteValue("ClipVoice", false); return; }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var portableRoot = FindPortableDotNetRoot();
            if (portableRoot is null)
            {
                key.SetValue("ClipVoice", $"\"{exe}\"");
                return;
            }

            var script = Path.Combine(_storage.DataDirectory, "startup.cmd");
            var content = new StringBuilder()
                .AppendLine("@echo off")
                .AppendLine($"set \"DOTNET_ROOT={portableRoot}\"")
                .AppendLine("set \"DOTNET_MULTILEVEL_LOOKUP=0\"")
                .AppendLine($"start \"\" \"{exe}\"")
                .ToString();
            File.WriteAllText(script, content, new UTF8Encoding(false));
            key.SetValue("ClipVoice", $"cmd.exe /d /c \"\"{script}\"\"");
        }
        catch (Exception ex)
        {
            LogService.Write("Could not configure Windows startup", ex);
            SetStatus("Could not configure startup: " + ex.Message);
        }
    }

    private static string? FindPortableDotNetRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".dotnet", "dotnet.exe");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }

    public void SaveState()
    {
        _stateSaveTimer.Stop();
        try { _storage.SaveState(History, TemplateFolders); }
        catch (Exception ex)
        {
            LogService.Write("Could not save local state", ex);
            SetStatus("Could not save local data: " + ex.Message);
        }
    }

    public string AudioCacheDirectory => _storage.CacheDirectory;

    public void SetStatus(string text) => StatusChanged?.Invoke(text);

    public void Dispose()
    {
        SetGlobalMouseTracking(false);
        CancelWork();
        try { _backgroundCts?.Cancel(); } catch { }
        _backgroundCts?.Dispose();
        _backgroundCts = null;
        _audio.Stop();
        SaveState();
        _localTts.Dispose();
        _system.Dispose();
    }
}
