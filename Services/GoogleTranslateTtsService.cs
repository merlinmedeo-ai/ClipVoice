using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Diagnostics;

namespace ClipVoice.Services;

/// <summary>
/// Zero-config emergency TTS fallback. This is an unofficial consumer endpoint,
/// so Edge Read Aloud remains the primary provider.
/// </summary>
public sealed class GoogleTranslateTtsService
{
    private readonly StorageService _storage;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _synthesisGate = new(1, 1);

    public GoogleTranslateTtsService(StorageService storage)
    {
        _storage = storage;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://translate.google.com/");
    }


    public async Task ProbeAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var data = await FetchChunkAsync("ok", "en", 1.0, timeout.Token);
        if (data.Length < 64) throw new InvalidOperationException("Google TTS вернул пустой ответ.");
    }

    public async Task<string> SynthesizeAsync(string text, string languageCode, int speechRatePercent = 0, CancellationToken ct = default)
    {
        var waitClock = Stopwatch.StartNew();
        await _synthesisGate.WaitAsync(ct);
        try
        {
            LogService.Info($"Google synthesis slot acquired: wait_ms={waitClock.ElapsedMilliseconds}");
            return await SynthesizeCoreAsync(text, languageCode, speechRatePercent, ct);
        }
        finally { _synthesisGate.Release(); }
    }

    private async Task<string> SynthesizeCoreAsync(string text, string languageCode, int speechRatePercent, CancellationToken ct)
    {
        var totalClock = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Нет текста для озвучки.");
        if (text.Length > 8_000) throw new InvalidOperationException("Резервная озвучка не предназначена для очень длинного текста.");

        var lang = NormalizeLanguage(languageCode);
        var speed = Math.Clamp(1.0 + Math.Clamp(speechRatePercent, -50, 100) / 100.0, 0.5, 2.0);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"google|{lang}|{speed.ToString("0.00", CultureInfo.InvariantCulture)}|{text}")));
        var path = Path.Combine(_storage.CacheDirectory, hash + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 128)
        {
            LogService.Info($"Google TTS cache hit: language={lang}; bytes={new FileInfo(path).Length}; elapsed_ms={totalClock.ElapsedMilliseconds}");
            return path;
        }

        // Isolate in-flight cache writes, including when another ClipVoice process
        // requests the same text before this request has completed.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var chunks = Split(text, 180).ToList();
        LogService.Info($"Google TTS cache miss: language={lang}; chunks={chunks.Count}; speed={speed:0.00}");
        try
        {
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read);
            var chunkNumber = 0;
            foreach (var chunk in chunks)
            {
                chunkNumber++;
                var chunkClock = Stopwatch.StartNew();
                var audio = await FetchChunkAsync(chunk, lang, speed, ct);
                await output.WriteAsync(audio, ct);
                LogService.Info($"Google TTS chunk complete: chunk={chunkNumber}/{chunks.Count}; chars={chunk.Length}; bytes={audio.Length}; elapsed_ms={chunkClock.ElapsedMilliseconds}");
                if (chunkNumber < chunks.Count) await Task.Delay(80, ct);
            }
            await output.FlushAsync(ct);
            if (output.Length < 128) throw new InvalidOperationException("Резервный сервис не вернул аудио.");
            output.Close();
            File.Move(temp, path, true);
            _storage.PruneAudioCache(path);
            LogService.Info($"Google TTS file complete: chunks={chunks.Count}; bytes={new FileInfo(path).Length}; elapsed_ms={totalClock.ElapsedMilliseconds}");
            return path;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private async Task<byte[]> FetchChunkAsync(string text, string lang, double speed, CancellationToken ct)
    {
        Exception? last = null;
        var bases = new[]
        {
            "https://translate.googleapis.com/translate_tts",
            "https://translate.google.com/translate_tts"
        };
        foreach (var endpoint in bases)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var client = endpoint.Contains("googleapis", StringComparison.OrdinalIgnoreCase) ? "gtx" : "tw-ob";
                    var url = endpoint + "?ie=UTF-8&client=" + client + "&tl=" + Uri.EscapeDataString(lang)
                        + "&ttsspeed=" + speed.ToString("0.00", CultureInfo.InvariantCulture)
                        + "&q=" + Uri.EscapeDataString(text);
                    using var response = await _http.GetAsync(url, ct);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode >= 500)
                    {
                        await Task.Delay(300 * (attempt + 1), ct);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    var data = await response.Content.ReadAsByteArrayAsync(ct);
                    if (data.Length < 64) throw new InvalidOperationException("Пустой ответ резервной озвучки.");
                    return data;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
        }
        throw new InvalidOperationException("Резервная онлайн-озвучка тоже недоступна.", last);
    }

    private static string NormalizeLanguage(string? lang)
    {
        lang = (lang ?? "en").ToLowerInvariant();
        if (lang.StartsWith("zh")) return "zh-CN";
        return lang.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "en";
    }

    private static IEnumerable<string> Split(string text, int max)
    {
        var p = 0;
        while (p < text.Length)
        {
            var len = Math.Min(max, text.Length - p);
            if (p + len < text.Length)
            {
                var cut = text.LastIndexOfAny(new[] { '\n', '.', '!', '?', ';', ',', ' ' }, p + len - 1, len);
                if (cut > p + max / 2) len = cut - p + 1;
            }
            var chunk = text.Substring(p, len).Trim();
            if (chunk.Length > 0) yield return chunk;
            p += len;
        }
    }
}
