using System.Net;
using System.Text;
using System.Text.Json;

namespace ClipVoice.Services;

public sealed record TranslationResult(string Text, string DetectedLanguage, string Provider = "");

public sealed class TranslationService
{
    private readonly HttpClient _http;

    public TranslationService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(14) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/153 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://translate.google.com/");
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
        => (await TranslateDetailedAsync(text, targetLanguage, ct)).Text;

    public async Task<TranslationResult> TranslateDetailedAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TranslationResult(text, string.Empty);

        targetLanguage = TranslationDirectionService.NormalizeLanguage(targetLanguage);
        if (string.IsNullOrWhiteSpace(targetLanguage)) targetLanguage = "ru";

        // Keep requests reasonably small. This also makes the no-key fallback services
        // much less likely to reject a request because of URL length or free limits.
        var chunks = Split(text, 900).ToList();
        var output = new StringBuilder();
        var detected = string.Empty;
        var provider = string.Empty;

        foreach (var chunk in chunks)
        {
            var result = await TranslateChunkWithFallbackAsync(chunk, targetLanguage, ct);
            output.Append(result.Text);
            if (string.IsNullOrWhiteSpace(detected) && !string.IsNullOrWhiteSpace(result.DetectedLanguage))
                detected = result.DetectedLanguage;
            if (string.IsNullOrWhiteSpace(provider)) provider = result.Provider;
        }

        return new TranslationResult(output.ToString(), detected, provider);
    }

    public Task<string> DetectLanguageAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DetectLanguageLocally(text));
    }

    /// <summary>
    /// Fast, offline language guess for speech voice selection. Speech must not wait
    /// for one or more translation endpoints just to choose a voice.
    /// </summary>
    public string DetectLanguageLocally(string text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : DetectByScript(text.Length > 2000 ? text[..2000] : text);

    private async Task<TranslationResult> TranslateChunkWithFallbackAsync(string text, string targetLanguage, CancellationToken ct)
    {
        var errors = new List<Exception>();

        // 1. Chrome's consumer translate endpoint. It is a different host/path from
        // the old GTX endpoint and is used as a fallback by maintained translators.
        try
        {
            return await RetryAsync(
                () => TranslateGoogleClients5Async(text, targetLanguage, ct),
                "Google Chrome translate", errors, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 2. Classic Google consumer endpoint.
        try
        {
            return await RetryAsync(
                () => TranslateGoogleGtxAsync("https://translate.googleapis.com/translate_a/single", text, targetLanguage, ct),
                "Google GTX", errors, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 3. Same protocol via the translate.google.com host. Some networks treat
        // googleapis.com and translate.google.com differently.
        try
        {
            return await RetryAsync(
                () => TranslateGoogleGtxAsync("https://translate.google.com/translate_a/single", text, targetLanguage, ct),
                "Google Web", errors, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 4. Independent zero-config fallback. MyMemory accepts autodetect and does
        // not require the user to create an account or API key for basic use.
        try
        {
            return await RetryAsync(
                () => TranslateMyMemoryAsync(text, targetLanguage, ct),
                "MyMemory", errors, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        throw new InvalidOperationException(
            "Ни один онлайн-переводчик не ответил. Подробности записаны в журнал ClipVoice.",
            new AggregateException(errors));
    }

    private static async Task<TranslationResult> RetryAsync(
        Func<Task<TranslationResult>> action,
        string provider,
        List<Exception> errors,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await action();
                if (string.IsNullOrWhiteSpace(result.Text))
                    throw new InvalidOperationException(provider + " вернул пустой перевод.");
                return result with { Provider = provider };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == 0) await Task.Delay(250, ct);
            }
        }

        var wrapped = new InvalidOperationException(provider + ": " + ShortError(last), last);
        errors.Add(wrapped);
        throw wrapped;
    }

    private async Task<TranslationResult> TranslateGoogleGtxAsync(string endpoint, string text, string targetLanguage, CancellationToken ct)
    {
        var url = endpoint
            + "?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(targetLanguage)
            + "&dt=t&ie=UTF-8&oe=UTF-8&q=" + Uri.EscapeDataString(text);

        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException("неожиданный формат ответа");

        var translated = new StringBuilder();
        if (root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var part in root[0].EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0 && part[0].ValueKind == JsonValueKind.String)
                    translated.Append(part[0].GetString());
            }
        }

        if (translated.Length == 0) throw new InvalidOperationException("перевод не получен");
        var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString() ?? string.Empty
            : string.Empty;

        return new TranslationResult(translated.ToString(), detected);
    }

    private async Task<TranslationResult> TranslateGoogleClients5Async(string text, string targetLanguage, CancellationToken ct)
    {
        var url = "https://clients5.google.com/translate_a/t"
            + "?client=dict-chrome-ex&sl=auto&tl=" + Uri.EscapeDataString(targetLanguage)
            + "&q=" + Uri.EscapeDataString(text);

        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var translated = new StringBuilder();
        var detected = string.Empty;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("sentences", out var sentences) && sentences.ValueKind == JsonValueKind.Array)
            {
                foreach (var sentence in sentences.EnumerateArray())
                {
                    if (sentence.ValueKind == JsonValueKind.Object &&
                        sentence.TryGetProperty("trans", out var trans) &&
                        trans.ValueKind == JsonValueKind.String)
                        translated.Append(trans.GetString());
                }
            }

            if (root.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.String)
                detected = src.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(detected) &&
                root.TryGetProperty("ld_result", out var ld) && ld.ValueKind == JsonValueKind.Object &&
                ld.TryGetProperty("srclangs", out var langs) && langs.ValueKind == JsonValueKind.Array && langs.GetArrayLength() > 0 &&
                langs[0].ValueKind == JsonValueKind.String)
                detected = langs[0].GetString() ?? string.Empty;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            // Older Chrome response shape. Reuse the tolerant array parser.
            translated.Append(ExtractStringsFromChromeArray(root));
            detected = FindLikelyLanguageCode(root);
        }

        if (translated.Length == 0) throw new InvalidOperationException("перевод не получен");
        return new TranslationResult(translated.ToString(), detected);
    }

    private async Task<TranslationResult> TranslateMyMemoryAsync(string text, string targetLanguage, CancellationToken ct)
    {
        // The free endpoint is happier with short queries, so split only for this fallback.
        var output = new StringBuilder();
        var detected = string.Empty;

        foreach (var part in Split(text, 420))
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(part)
                + "&langpair=autodetect%7C" + Uri.EscapeDataString(targetLanguage);

            using var response = await _http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("responseStatus", out var status))
            {
                var statusCode = status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var n)
                    ? n
                    : int.TryParse(status.ToString(), out var p) ? p : 200;
                if (statusCode >= 400)
                {
                    var details = root.TryGetProperty("responseDetails", out var d) ? d.ToString() : "ошибка сервиса";
                    throw new InvalidOperationException($"HTTP {statusCode}: {details}");
                }
            }

            if (!root.TryGetProperty("responseData", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("translatedText", out var translatedText) || translatedText.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("неожиданный формат ответа");

            var translated = WebUtility.HtmlDecode(translatedText.GetString() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("перевод не получен");
            output.Append(translated);

            if (string.IsNullOrWhiteSpace(detected) && data.TryGetProperty("detectedLanguage", out var dl) && dl.ValueKind == JsonValueKind.String)
                detected = dl.GetString() ?? string.Empty;
        }

        return new TranslationResult(output.ToString(), detected);
    }

    private static string ExtractStringsFromChromeArray(JsonElement root)
    {
        // clients5 has had more than one response shape. Prefer known translation fields,
        // then fall back to the first plausible text-bearing nested array.
        try
        {
            if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in root[0].EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0 && part[0].ValueKind == JsonValueKind.String)
                        sb.Append(part[0].GetString());
                }
                if (sb.Length > 0) return sb.ToString();
            }
        }
        catch { }
        return string.Empty;
    }

    private static string FindLikelyLanguageCode(JsonElement root)
    {
        try
        {
            if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                return root[2].GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string ShortError(Exception? ex)
    {
        if (ex is null) return "неизвестная ошибка";
        if (ex is TaskCanceledException) return "тайм-аут";
        if (ex is HttpRequestException) return ex.Message;
        return ex.Message.Length > 140 ? ex.Message[..140] : ex.Message;
    }

    private static string DetectByScript(string text)
    {
        var lower = " " + text.ToLowerInvariant() + " ";
        if (lower.IndexOfAny(new[] { 'і', 'ї', 'є', 'ґ' }) >= 0) return "uk";
        if (text.Any(c => c >= '\u0400' && c <= '\u04FF')) return "ru";
        if (text.Any(c => c >= '\u3040' && c <= '\u30FF')) return "ja";
        if (text.Any(c => c >= '\uAC00' && c <= '\uD7AF')) return "ko";
        if (text.Any(c => c >= '\u4E00' && c <= '\u9FFF')) return "zh";
        if (text.Any(c => c >= '\u0600' && c <= '\u06FF')) return "ar";
        if (text.Any(c => c >= '\u0590' && c <= '\u05FF')) return "he";
        if (text.Any(c => c >= '\u0370' && c <= '\u03FF')) return "el";

        // Offline hints for common Latin-script languages. Online detection is still
        // preferred, but these keep Piper useful if the network is completely down.
        if (lower.IndexOfAny(new[] { 'ą', 'ć', 'ę', 'ł', 'ń', 'ś', 'ź', 'ż' }) >= 0) return "pl";
        if (lower.IndexOfAny(new[] { 'č', 'ď', 'ě', 'ň', 'ř', 'ť', 'ů' }) >= 0) return "cs";
        if (lower.IndexOfAny(new[] { 'ľ', 'ĺ', 'ŕ', 'ô' }) >= 0) return "sk";
        if (lower.Contains('ß') || lower.Contains(" der ") || lower.Contains(" die ") || lower.Contains(" das ") || lower.Contains(" und ") || lower.Contains(" nicht ")) return "de";
        if (lower.Contains('ñ') || lower.Contains('¿') || lower.Contains('¡') || lower.Contains(" que ") && (lower.Contains(" el ") || lower.Contains(" los ") || lower.Contains(" una "))) return "es";
        if (lower.Contains('ã') || lower.Contains('õ') || lower.Contains(" não ") || lower.Contains(" uma ") && lower.Contains(" com ")) return "pt";
        if (lower.Contains('ç') || lower.Contains('œ') || lower.Contains(" les ") || lower.Contains(" des ") || lower.Contains(" avec ") || lower.Contains(" pour ")) return "fr";
        if (lower.Contains(" gli ") || lower.Contains(" della ") || lower.Contains(" che ") && lower.Contains(" una ")) return "it";
        if (lower.Contains('æ') || lower.Contains('ø')) return lower.Contains(" og ") ? "no" : "da";
        if (lower.Contains('å') && (lower.Contains(" och ") || lower.Contains(" är "))) return "sv";
        if (lower.Contains(" het ") || lower.Contains(" een ") || lower.Contains(" van ")) return "nl";
        return "en";
    }

    private static IEnumerable<string> Split(string text, int max)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(max, text.Length - pos);
            if (pos + len < text.Length)
            {
                var cut = text.LastIndexOfAny(new[] { '\n', '.', '!', '?', ';', ',', ' ' }, pos + len - 1, len);
                if (cut > pos + max / 2) len = cut - pos + 1;
            }
            yield return text.Substring(pos, len);
            pos += len;
        }
    }
}
