using System.Net;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipVoice.Models;

namespace ClipVoice.Services;

public sealed class EdgeTtsService
{
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    // Matches current edge-tts 7.2.x constants checked in September 2026.
    private const string ChromiumFullVersion = "143.0.3650.75";
    private const string SecMsGecVersion = "1-" + ChromiumFullVersion;
    private readonly StorageService _storage;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _synthesisGate = new(1, 1);
    private TimeSpan _clockSkew = TimeSpan.Zero;
    private IReadOnlyList<EdgeVoice>? _voices;

    private sealed record EdgeVoice(string ShortName, string Locale, string Gender);
    private sealed class VoiceCache { public DateTime SavedUtc { get; set; } public List<EdgeVoice> Voices { get; set; } = new(); }

    public EdgeTtsService(StorageService storage)
    {
        _storage = storage;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent());
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }


    public async Task ProbeAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt > 0) await RefreshClockSkewAsync(token);
                var url = $"https://{BaseUrl}/voices/list?trustedclienttoken={TrustedClientToken}&Sec-MS-GEC={GenerateSecMsGec()}&Sec-MS-GEC-Version={SecMsGecVersion}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Cookie", $"muid={RandomHex(16)};");
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
                using var response = await _http.SendAsync(req, token);
                if (response.IsSuccessStatusCode) return;
                last = new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("Edge Read Aloud не отвечает.", last);
    }

    public async Task<string> SynthesizeAsync(string text, AppSettings settings, string languageCode, CancellationToken ct = default)
    {
        var waitClock = Stopwatch.StartNew();
        await _synthesisGate.WaitAsync(ct);
        try
        {
            LogService.Info($"Edge synthesis slot acquired: wait_ms={waitClock.ElapsedMilliseconds}");
            return await SynthesizeCoreAsync(text, settings, languageCode, ct);
        }
        finally { _synthesisGate.Release(); }
    }

    private async Task<string> SynthesizeCoreAsync(string text, AppSettings settings, string languageCode, CancellationToken ct)
    {
        var totalClock = Stopwatch.StartNew();
        text = CleanText(text);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Нет текста для озвучки.");
        if (text.Length > 100_000) throw new InvalidOperationException("Текст слишком большой для одной онлайн-озвучки. Выдели меньший фрагмент.");

        var voiceClock = Stopwatch.StartNew();
        var voice = await SelectVoiceAsync(languageCode, settings, ct);
        LogService.Info($"Edge voice selected: language={languageCode}; voice={voice}; elapsed_ms={voiceClock.ElapsedMilliseconds}");
        // Apply the selected speed in the generated speech. MediaPlayer remains at
        // normal speed to avoid its initial seek jump when SpeedRatio is changed.
        var rate = Math.Clamp(settings.SpeechRatePercent, -50, 100);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{voice}|{rate}|{text}")));
        var path = Path.Combine(_storage.CacheDirectory, cacheKey + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 128)
        {
            LogService.Info($"Edge TTS cache hit: bytes={new FileInfo(path).Length}; voice={voice}; elapsed_ms={totalClock.ElapsedMilliseconds}");
            return path;
        }

        var chunks = SplitForTts(text, 800).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        LogService.Info($"Edge TTS cache miss: voice={voice}; chunks={chunks.Count}; rate_percent={rate}");
        // A unique temporary file also protects against another ClipVoice process using
        // the same cache key while this request is being cancelled or retried.
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var chunkNumber = 0;
            foreach (var chunk in chunks)
            {
                chunkNumber++;
                var chunkClock = Stopwatch.StartNew();
                var audio = await SynthesizeChunkWithRetryAsync(chunk, voice, rate, ct);
                await file.WriteAsync(audio, ct);
                LogService.Info($"Edge TTS chunk complete: chunk={chunkNumber}/{chunks.Count}; chars={chunk.Length}; bytes={audio.Length}; elapsed_ms={chunkClock.ElapsedMilliseconds}");
            }
            await file.FlushAsync(ct);
            if (file.Length < 128) throw new InvalidOperationException("Сервис озвучки не вернул аудио.");
            file.Close();
            File.Move(tempPath, path, true);
            _storage.PruneAudioCache(path);
            LogService.Info($"Edge TTS file complete: chunks={chunks.Count}; bytes={new FileInfo(path).Length}; elapsed_ms={totalClock.ElapsedMilliseconds}");
            return path;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private async Task<byte[]> SynthesizeChunkWithRetryAsync(string text, string voice, int ratePercent, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                {
                    await RefreshClockSkewAsync(ct);
                    await Task.Delay(250 * attempt, ct);
                }
                return await SynthesizeChunkAsync(text, voice, ratePercent, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException("Онлайн-озвучка Edge сейчас не отвечает. Проверь интернет и повтори попытку.", last);
    }

    private async Task<byte[]> SynthesizeChunkAsync(string text, string voice, int ratePercent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        using var ws = new ClientWebSocket();
        ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions { ClientMaxWindowBits = 15, ServerMaxWindowBits = 15 };
        ws.Options.SetRequestHeader("User-Agent", UserAgent());
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
        ws.Options.SetRequestHeader("Cookie", $"muid={RandomHex(16)};");

        var connectionId = Guid.NewGuid().ToString("N");
        var url = $"wss://{BaseUrl}/edge/v1?TrustedClientToken={TrustedClientToken}&ConnectionId={connectionId}&Sec-MS-GEC={GenerateSecMsGec()}&Sec-MS-GEC-Version={SecMsGecVersion}";
        try { await ws.ConnectAsync(new Uri(url), token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Превышено время подключения к озвучке."); }

        var stamp = DateToString();
        var config = $"X-Timestamp:{stamp}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                     "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
        await SendTextAsync(ws, config, token);

        var escaped = EscapeXml(text);
        var rate = ratePercent >= 0 ? $"+{ratePercent}%" : $"{ratePercent}%";
        var ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><voice name='{voice}'><prosody pitch='+0Hz' rate='{rate}' volume='+0%'>{escaped}</prosody></voice></speak>";
        var request = $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{stamp}Z\r\nPath:ssml\r\n\r\n{ssml}";
        await SendTextAsync(ws, request, token);

        using var output = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var (type, payload) = await ReceiveMessageAsync(ws, token);
                if (type == WebSocketMessageType.Close) break;
                if (type == WebSocketMessageType.Text)
                {
                    var s = Encoding.UTF8.GetString(payload);
                    if (s.Contains("Path:turn.end", StringComparison.OrdinalIgnoreCase)) break;
                    continue;
                }
                if (type != WebSocketMessageType.Binary || payload.Length < 2) continue;
                var headerLength = (payload[0] << 8) | payload[1];
                var dataStart = headerLength + 2;
                if (dataStart > payload.Length) continue;
                var headerText = Encoding.UTF8.GetString(payload, 2, Math.Max(0, Math.Min(headerLength, payload.Length - 2)));
                if (headerText.Contains("Path:audio", StringComparison.OrdinalIgnoreCase) && dataStart < payload.Length)
                    output.Write(payload, dataStart, payload.Length - dataStart);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Сервис озвучки слишком долго не отвечал."); }

        if (ws.State == WebSocketState.Open)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { ws.Abort(); }
        }
        if (output.Length == 0) throw new InvalidOperationException("Сервис озвучки не прислал аудио.");
        return output.ToArray();
    }

    private async Task<string> SelectVoiceAsync(string languageCode, AppSettings settings, CancellationToken ct)
    {
        var language = (languageCode ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "en";
        if (language == "ru") return settings.RussianVoice;
        if (language == "uk") return settings.UkrainianVoice;
        if (language == "en") return settings.EnglishVoice;

        // Use a known built-in voice immediately. Fetching the full voice catalog can
        // add several seconds to the first speech request on a slow connection.
        var fallback = FallbackVoice(language);
        if (fallback is not null) return fallback;

        try
        {
            var voices = await GetVoicesAsync(ct);
            var match = voices.FirstOrDefault(v => v.Locale.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase) && v.Gender.Equals("Female", StringComparison.OrdinalIgnoreCase))
                        ?? voices.FirstOrDefault(v => v.Locale.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.ShortName;
        }
        catch { }

        return FallbackVoice(language) ?? settings.EnglishVoice;
    }

    private async Task<IReadOnlyList<EdgeVoice>> GetVoicesAsync(CancellationToken ct)
    {
        if (_voices is { Count: > 0 }) return _voices;
        try
        {
            if (File.Exists(_storage.VoiceCacheFile))
            {
                var cache = JsonSerializer.Deserialize<VoiceCache>(await File.ReadAllTextAsync(_storage.VoiceCacheFile, ct));
                if (cache is { Voices.Count: > 0 } && DateTime.UtcNow - cache.SavedUtc < TimeSpan.FromDays(7))
                    return _voices = cache.Voices;
            }
        }
        catch { }

        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt > 0) await RefreshClockSkewAsync(ct);
                var url = $"https://{BaseUrl}/voices/list?trustedclienttoken={TrustedClientToken}&Sec-MS-GEC={GenerateSecMsGec()}&Sec-MS-GEC-Version={SecMsGecVersion}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Cookie", $"muid={RandomHex(16)};");
                req.Headers.TryAddWithoutValidation("Sec-CH-UA", $"\" Not;A Brand\";v=\"99\", \"Microsoft Edge\";v=\"{ChromiumFullVersion.Split('.')[0]}\", \"Chromium\";v=\"{ChromiumFullVersion.Split('.')[0]}\"");
                req.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", "?0");
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                using var response = await _http.SendAsync(req, ct);
                if (response.StatusCode == HttpStatusCode.Forbidden) { last = new HttpRequestException("403"); continue; }
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var result = new List<EdgeVoice>();
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    var shortName = v.TryGetProperty("ShortName", out var sn) ? sn.GetString() : null;
                    var locale = v.TryGetProperty("Locale", out var lo) ? lo.GetString() : null;
                    var gender = v.TryGetProperty("Gender", out var ge) ? ge.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(shortName) && !string.IsNullOrWhiteSpace(locale)) result.Add(new EdgeVoice(shortName!, locale!, gender ?? string.Empty));
                }
                if (result.Count == 0) throw new InvalidOperationException("Список голосов пуст.");
                _voices = result;
                try { await File.WriteAllTextAsync(_storage.VoiceCacheFile, JsonSerializer.Serialize(new VoiceCache { SavedUtc = DateTime.UtcNow, Voices = result }), ct); } catch { }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("Не удалось получить список голосов.", last);
    }

    private async Task RefreshClockSkewAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("https://speech.platform.bing.com/", ct);
            if (response.Headers.Date is DateTimeOffset serverTime) _clockSkew = serverTime - DateTimeOffset.UtcNow;
        }
        catch { }
    }

    private static string? FallbackVoice(string lang) => lang switch
    {
        "de" => "de-DE-KatjaNeural", "fr" => "fr-FR-DeniseNeural", "es" => "es-ES-ElviraNeural",
        "it" => "it-IT-ElsaNeural", "pl" => "pl-PL-ZofiaNeural", "pt" => "pt-BR-FranciscaNeural",
        "tr" => "tr-TR-EmelNeural", "cs" => "cs-CZ-VlastaNeural", "ja" => "ja-JP-NanamiNeural",
        "ko" => "ko-KR-SunHiNeural", "zh" => "zh-CN-XiaoxiaoNeural", "ar" => "ar-EG-SalmaNeural",
        _ => null
    };

    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.Count > 0) ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, ms.ToArray());
    }

    private string GenerateSecMsGec()
    {
        const double winEpoch = 11644473600d;
        var now = DateTimeOffset.UtcNow + _clockSkew;
        var seconds = now.ToUnixTimeMilliseconds() / 1000d;
        var ticks = seconds + winEpoch;
        ticks -= ticks % 300d;
        ticks *= 10_000_000d;
        var raw = $"{ticks:F0}{TrustedClientToken}";
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(raw)));
    }

    private static string UserAgent()
    {
        var major = ChromiumFullVersion.Split('.')[0];
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0";
    }

    private string DateToString() => (DateTime.UtcNow + _clockSkew).ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", System.Globalization.CultureInfo.InvariantCulture);
    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
    private static string EscapeXml(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static string CleanText(string s)
    {
        var chars = s.Select(c => ((c <= 8) || (c >= 11 && c <= 12) || (c >= 14 && c <= 31)) ? ' ' : c).ToArray();
        return new string(chars).Trim();
    }

    private static IEnumerable<string> SplitForTts(string text, int maxChars)
    {
        var p = 0;
        while (p < text.Length)
        {
            var len = Math.Min(maxChars, text.Length - p);
            if (p + len < text.Length)
            {
                var cut = text.LastIndexOfAny(new[] { '\n', '.', '!', '?', ' ' }, p + len - 1, len);
                if (cut > p + maxChars / 2) len = cut - p + 1;
            }
            yield return text.Substring(p, len).Trim();
            p += len;
        }
    }
}
