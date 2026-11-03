using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClipVoice.Services;

internal sealed class GroqDictationService : IDisposable
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint mciSendString(string command, StringBuilder? result, int length, IntPtr callback);
    private readonly string _alias = "clipvoice_" + Guid.NewGuid().ToString("N");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(90) };
    public bool Recording { get; private set; }

    public static string EncodeKey(string key) => string.IsNullOrEmpty(key) ? "" : Convert.ToBase64String(DpapiService.Protect(Encoding.UTF8.GetBytes(key)));
    public static string DecodeKey(string value)
    {
        try { return string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(DpapiService.Unprotect(Convert.FromBase64String(value))); }
        catch { return ""; }
    }

    private void Command(string text)
    {
        var code = mciSendString(text, null, 0, IntPtr.Zero);
        if (code != 0) throw new InvalidOperationException($"Не удалось записать микрофон (код {code}). Проверьте микрофон по умолчанию и разрешения Windows для классических приложений.");
    }

    public void Start()
    {
        try
        {
            Command($"open new type waveaudio alias {_alias}");
            Command($"set {_alias} time format milliseconds");
            Command($"set {_alias} format tag pcm channels 1 samplespersec 16000 bitspersample 16 alignment 2 bytespersec 32000");
            Command($"record {_alias}");
            Recording = true;
        }
        catch { Dispose(); throw; }
    }

    public async Task<string> FinishAsync(string key, string language, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "ClipVoice-dictation-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Command($"stop {_alias}");
            Command($"save {_alias} \"{path}\"");
            Dispose();
            if (new FileInfo(path).Length < 3244) throw new InvalidOperationException("Запись слишком короткая. Произнесите фразу и повторите.");
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("whisper-large-v3"), "model");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0"), "temperature");
            if (language is "ru" or "uk" or "en") form.Add(new StringContent(language), "language");
            using var stream = File.OpenRead(path);
            var audio = new StreamContent(stream);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "dictation.wav");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = form;
            using var response = await Client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Groq отклонил ключ. Проверьте его в настройках.",
                    HttpStatusCode.Forbidden => "Нет доступа к модели. Проверьте разрешения проекта Groq.",
                    HttpStatusCode.TooManyRequests => "Достигнут лимит Groq. Подождите и повторите диктовку.",
                    _ => $"Ошибка Groq HTTP {(int)response.StatusCode}. Повторите позже."
                });
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
        }
        finally
        {
            Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    public void Dispose()
    {
        mciSendString($"close {_alias}", null, 0, IntPtr.Zero);
        Recording = false;
    }
}
