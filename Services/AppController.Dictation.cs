using System.Windows;
using System.Windows.Threading;
using ClipVoice.Models;

namespace ClipVoice.Services;

public sealed partial class AppController
{
    private readonly GroqDictationService _dictation = new();
    private readonly CancellationTokenSource _dictationCts = new();
    private DispatcherTimer? _dictationTimer;
    private InputTarget _dictationTarget;
    private bool _dictationBusy;
    private string _dictationKey = "";
    private string _dictationLanguage = "ru";
    private ClipVoice.Views.DictationOverlayWindow? _dictationOverlay;

    private void HideDictationOverlay()
    {
        _dictationOverlay?.Close();
        _dictationOverlay = null;
    }

    public async Task ToggleDictationAsync()
    {
        if (_dictationBusy) return;
        try
        {
            if (!_dictation.Recording)
            {
                _dictationBusy = true;
                _dictationKey = GroqDictationService.DecodeKey(Settings.GroqKeyProtected);
                if (string.IsNullOrWhiteSpace(_dictationKey))
                {
                    MessageBox.Show("Добавьте личный ключ Groq: Настройки → SPEECH & TRANSLATION. Нажмите ★ для инструкции.", "Диктовка");
                    return;
                }
                _dictationLanguage = Settings.DictationLanguage;
                _dictationTarget = _system.CaptureForegroundInputTarget();
                _dictation.Start();
                try
                {
                    _dictationOverlay = new ClipVoice.Views.DictationOverlayWindow();
                    _dictationOverlay.Show();
                }
                catch { HideDictationOverlay(); }
                _dictationTimer ??= CreateDictationTimer();
                _dictationTimer.Start();
                SetStatus("● Запись микрофона — нажмите " + Settings.HotkeyDictation + " для распознавания (до 60 секунд)");
                await DictationSoundService.PlayStartAsync();
                return;
            }
            _dictationBusy = true;
            _dictationTimer?.Stop();
            HideDictationOverlay();
            SetStatus("Распознавание речи — Groq Whisper large-v3…");
            var text = await _dictation.FinishAsync(_dictationKey, _dictationLanguage, _dictationCts.Token);
            if (string.IsNullOrWhiteSpace(text)) { SetStatus("Речь не распознана. Повторите фразу."); return; }
            var item = new ClipboardItem { Kind = ClipboardItemKind.Text, Text = text };
            SetClipboardItem(item);
            AddHistoryItem(item);
            var pasted = await _system.SendPasteShortcutAsync(_dictationTarget, _dictationCts.Token);
            SetStatus(pasted ? "Диктовка вставлена и сохранена в буфере" : "Текст в буфере. Нажмите Ctrl+V в нужном поле.");
        }
        catch (OperationCanceledException) { SetStatus("Диктовка отменена или превышено время ожидания."); }
        catch (Exception ex)
        {
            HideDictationOverlay();
            _dictationTimer?.Stop();
            _dictation.Dispose();
            var message = ex is HttpRequestException ? "Нет соединения с Groq. Проверьте интернет." : ex.Message;
            SetStatus("Диктовка: " + message);
            MessageBox.Show(message, "Диктовка", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { _dictationBusy = false; if (!_dictation.Recording) _dictationKey = ""; }
    }

    private DispatcherTimer CreateDictationTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += async (_, _) => { timer.Stop(); await ToggleDictationAsync(); };
        return timer;
    }
}
