using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClipVoice.Services;

public sealed class AudioPlaybackService
{
    private readonly MediaPlayer _player = new();
    private DispatcherTimer? _traceTimer;
    private Stopwatch? _playbackClock;
    private Uri? _pendingSource;
    private bool _playWhenOpened;
    private int _playbackId;
    private int _traceSamples;
    private string? _operationId;
    private TaskCompletionSource<bool>? _playbackCompletion;
    private string _playbackProvider = "неизвестно";
    private int _playbackChars;
    private int _playbackWords;
    private int _playbackRatePercent;
    private double _playbackSpeedRatio = 1.0;

    public AudioPlaybackService()
    {
        _player.MediaOpened += (_, _) =>
        {
            if (!_playWhenOpened || !IsPendingSourceCurrent()) return;
            _playWhenOpened = false;
            var duration = _player.NaturalDuration.HasTimeSpan
                ? $"{_player.NaturalDuration.TimeSpan.TotalMilliseconds:F0}ms"
                : "unknown";
            var durationMs = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            try
            {
                _player.SpeedRatio = _playbackSpeedRatio;
            }
            catch (Exception ex)
            {
                LogService.Write("Audio playback speed could not be applied; using normal speed", ex);
                _playbackSpeedRatio = 1.0;
                _player.SpeedRatio = 1.0;
            }
            var estimatedWpm = durationMs > 0 ? _playbackWords * 60_000d * _playbackSpeedRatio / durationMs : 0;
            LogService.InfoForOperation(_operationId, $"Audio media opened: id={_playbackId}; provider={_playbackProvider}; elapsed_ms={_playbackClock?.ElapsedMilliseconds}; duration={duration}; text_chars={_playbackChars}; text_words={_playbackWords}; rate_setting_percent={_playbackRatePercent}; media_speed_ratio={_playbackSpeedRatio:F2}; estimated_playback_wpm={estimatedWpm:F1}; position_ms={_player.Position.TotalMilliseconds:F0}");
            try
            {
                // Set speed and position only after MediaOpened. Applying either
                // before decoding finishes can advance the initial seek position.
                _player.Position = TimeSpan.Zero;
                LogService.InfoForOperation(_operationId, $"Audio ready to play: id={_playbackId}; position_ms={_player.Position.TotalMilliseconds:F0}; media_speed_ratio={_playbackSpeedRatio:F2}");
                _player.Play();
                LogService.InfoForOperation(_operationId, $"Audio Play() called after MediaOpened: id={_playbackId}; position_ms={_player.Position.TotalMilliseconds:F0}");
                StartTrace(_playbackId);
            }
            catch (Exception ex)
            {
                LogService.Write("Audio playback could not start after media opened", ex);
            }
        };
        _player.MediaEnded += (_, _) =>
        {
            LogService.InfoForOperation(_operationId, $"Audio media ended: id={_playbackId}; elapsed_ms={_playbackClock?.ElapsedMilliseconds}; position_ms={_player.Position.TotalMilliseconds:F0}");
            _playbackCompletion?.TrySetResult(true);
        };
        _player.MediaFailed += (_, e) =>
        {
            if (!IsPendingSourceCurrent()) return;
            _playWhenOpened = false;
            _pendingSource = null;
            var failure = e.ErrorException ?? new InvalidOperationException("Не удалось открыть аудио.");
            LogService.Write("Audio media failed", failure);
            _playbackCompletion?.TrySetException(failure);
        };
    }

    public void Play(string path, string provider = "неизвестно", string speechText = "", int ratePercent = 0)
    {
        BeginPlayback(path, null, provider, speechText, ratePercent);
    }

    public async Task PlayAndWaitAsync(string path, string provider, string speechText, int ratePercent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackId = BeginPlayback(path, completion, provider, speechText, ratePercent);
        try
        {
            await completion.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await _player.Dispatcher.InvokeAsync(() => StopPlaybackIfCurrent(playbackId, completion));
            throw;
        }
    }

    private int BeginPlayback(string path, TaskCompletionSource<bool>? completion, string provider, string speechText, int ratePercent)
    {
        StopTrace();
        _playbackId++;
        _operationId = LogService.CurrentOperationId;
        var playbackId = _playbackId;
        _playbackClock = Stopwatch.StartNew();
        _playbackProvider = provider;
        _playbackChars = speechText.Length;
        _playbackWords = CountWords(speechText);
        _playbackRatePercent = ratePercent;
        _playbackSpeedRatio = GetPlaybackSpeedRatio(provider, ratePercent);
        _playbackCompletion?.TrySetCanceled();
        _playbackCompletion = completion;
        _playWhenOpened = false;
        _pendingSource = null;
        _player.Stop();
        // Stop() leaves the previous media source attached. Reopening the same cached
        // URI sometimes does not raise MediaOpened, so reset it before every play.
        _player.Close();
        var fullPath = Path.GetFullPath(path);
        var audioBytes = 0L;
        try { audioBytes = new FileInfo(fullPath).Length; } catch { }
        LogService.Info($"Audio play requested: id={playbackId}; provider={provider}; extension={Path.GetExtension(fullPath)}; bytes={audioBytes}; rate_setting_percent={ratePercent}; media_speed_ratio={_playbackSpeedRatio:F2}");
        var source = new Uri(fullPath, UriKind.Absolute);
        _pendingSource = source;
        _playWhenOpened = true;
        _player.Volume = 1.0;
        LogService.Info($"Audio open requested: id={playbackId}");
        _player.Open(source);
        return playbackId;
    }

    private static double GetPlaybackSpeedRatio(string provider, int ratePercent)
    {
        if (!provider.Contains("Google", StringComparison.OrdinalIgnoreCase)) return 1.0;

        // Google Translate TTS's ttsspeed value did not approach the selected
        // speaking rate in measured Russian audio. At +80%, its 41-word sample
        // took 24.1 s (~102 WPM), versus Edge's 11.1 s (~222 WPM). Compensate
        // for the observed ~1.21x base gap, then apply the user's rate multiplier.
        var selectedRate = Math.Clamp(ratePercent, -50, 100);
        var ratio = 1.21 * (1.0 + selectedRate / 100.0);
        return Math.Clamp(ratio, 0.6, 2.4);
    }

    private static int CountWords(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static async Task<TimeSpan> MeasureDurationAsync(string path, CancellationToken ct = default)
    {
        var player = new MediaPlayer();
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalDuration.HasTimeSpan) completion.TrySetResult(player.NaturalDuration.TimeSpan);
            else completion.TrySetException(new InvalidOperationException("Движок не сообщил длительность аудио."));
        };
        player.MediaFailed += (_, e) => completion.TrySetException(e.ErrorException ?? new InvalidOperationException("Не удалось прочитать аудиофайл."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
            return await completion.Task;
        }
        finally
        {
            player.Close();
        }
    }

    public void Stop()
    {
        LogService.Info($"Audio stop requested: id={_playbackId}; elapsed_ms={_playbackClock?.ElapsedMilliseconds}; position_ms={_player.Position.TotalMilliseconds:F0}");
        StopTrace();
        _playWhenOpened = false;
        _pendingSource = null;
        _player.Stop();
        _playbackCompletion?.TrySetCanceled();
        _playbackCompletion = null;
    }

    private void StopPlaybackIfCurrent(int playbackId, TaskCompletionSource<bool> completion)
    {
        if (playbackId != _playbackId || !ReferenceEquals(_playbackCompletion, completion)) return;
        _playWhenOpened = false;
        _pendingSource = null;
        _player.Stop();
        completion.TrySetCanceled();
        _playbackCompletion = null;
    }

    private bool IsPendingSourceCurrent()
    {
        var current = _player.Source;
        return _pendingSource is not null && current is not null
            && string.Equals(current.AbsoluteUri, _pendingSource.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    private void StartTrace(int playbackId)
    {
        if (!LogService.DetailedLoggingEnabled) return;
        _traceSamples = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _traceTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (playbackId != _playbackId || !ReferenceEquals(timer, _traceTimer))
            {
                timer.Stop();
                return;
            }

            _traceSamples++;
            LogService.InfoForOperation(_operationId, $"Audio progress: id={playbackId}; elapsed_ms={_playbackClock?.ElapsedMilliseconds}; position_ms={_player.Position.TotalMilliseconds:F0}");
            if (_traceSamples >= 20) StopTrace();
        };
        timer.Start();
    }

    private void StopTrace()
    {
        _traceTimer?.Stop();
        _traceTimer = null;
    }
}
