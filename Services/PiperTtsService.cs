using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipVoice.Services;

/// <summary>
/// Offline emergency TTS. The runtime and voices are downloaded once and then
/// work without Internet. Only one voice is loaded at a time in a short-lived
/// child process so ClipVoice does not keep neural models in RAM.
/// </summary>
public sealed class PiperTtsService : IDisposable
{
    private const string RuntimeUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const string VoiceRevision = "v1.0.0";
    private const string CatalogUrl = "https://huggingface.co/rhasspy/piper-voices/raw/v1.0.0/voices.json";
    private const string VoiceBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";
    private const int MemoryLimitMb = 1000;
    private const int AppWorkingSetCapMb = 256;
    private const int PiperWorkingSetCapMb = 768;
    private const int WorkingSetMinimumMb = 16;
    private const int EmergencyAbortThresholdMb = 1000;
    private readonly SemaphoreSlim _synthesisGate = new(1, 1);
    private const uint QuotaLimitsHardWsMinDisable = 0x00000002;
    private const uint QuotaLimitsHardWsMaxEnable = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, UIntPtr minimumWorkingSetSize, UIntPtr maximumWorkingSetSize, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessWorkingSetSizeEx(IntPtr hProcess, out UIntPtr minimumWorkingSetSize, out UIntPtr maximumWorkingSetSize, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    private readonly StorageService _storage;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(8) };
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private Process? _activeProcess;

    private sealed record VoiceProfile(string Key, string Family, string ModelPath, string ConfigPath, long SizeBytes);
    private readonly record struct WorkingSetSnapshot(UIntPtr Minimum, UIntPtr Maximum, uint Flags, bool Valid);

    public event Action<string>? ProgressChanged;
    public event Action<long?>? ProcessMemoryChanged;

    public PiperTtsService(StorageService storage)
    {
        _storage = storage;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipVoice/{AppInfo.Version} (Windows; local Piper fallback)");
    }

    public bool IsRuntimeReady => FindRuntimeExe() is not null;

    public bool IsCoreReady => IsRuntimeReady && new[] { "ru", "uk", "en" }.All(lang => FindInstalledVoice(lang) is not null);

    public IReadOnlyList<string> InstalledLanguages()
    {
        try
        {
            if (!Directory.Exists(_storage.PiperVoicesDirectory)) return Array.Empty<string>();
            return Directory.EnumerateFiles(_storage.PiperVoicesDirectory, "*.onnx", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path))
                .Select(name =>
                {
                    var underscore = name.IndexOf('_');
                    if (underscore > 0) return name[..underscore].ToLowerInvariant();
                    var dash = name.IndexOf('-');
                    return dash > 0 ? name[..dash].ToLowerInvariant() : string.Empty;
                })
                .Where(x => x.Length == 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task PrepareCoreAsync(CancellationToken ct = default)
    {
        await EnsureRuntimeAsync(ct);
        foreach (var lang in new[] { "ru", "uk", "en" })
        {
            ct.ThrowIfCancellationRequested();
            await EnsureVoiceAsync(lang, ct);
        }
        ProgressChanged?.Invoke("Offline fallback is ready: Russian, Ukrainian, and English work without an internet connection.");
    }

    public async Task PrepareLanguageAsync(string languageCode, CancellationToken ct = default)
    {
        var lang = NormalizeLanguage(languageCode);
        await EnsureRuntimeAsync(ct);
        await EnsureVoiceAsync(lang, ct);
        ProgressChanged?.Invoke($"The local {lang.ToUpperInvariant()} voice is ready for offline use.");
    }

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        // Use unique text so the probe really starts Piper instead of returning a cached WAV.
        var probeText = "Offline speech check " + DateTime.UtcNow.Ticks.ToString();
        var file = await SynthesizeAsync(probeText, "ru", speechRatePercent: 0, ct: ct);
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    public async Task<string> SynthesizeAsync(string text, string languageCode, int speechRatePercent = 0, CancellationToken ct = default)
    {
        var waitClock = Stopwatch.StartNew();
        await _synthesisGate.WaitAsync(ct);
        try
        {
            LogService.Info($"Piper synthesis slot acquired: wait_ms={waitClock.ElapsedMilliseconds}");
            return await SynthesizeCoreAsync(text, languageCode, speechRatePercent, ct);
        }
        finally { _synthesisGate.Release(); }
    }

    private async Task<string> SynthesizeCoreAsync(string text, string languageCode, int speechRatePercent, CancellationToken ct)
    {
        var totalClock = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("There is no text for local speech.");
        if (text.Length > 12_000) throw new InvalidOperationException("For local fallback speech, select a passage of up to 12,000 characters.");

        var lang = NormalizeLanguage(languageCode);
        var speed = Math.Clamp(1.0 + Math.Clamp(speechRatePercent, -50, 100) / 100.0, 0.5, 2.0);
        var lengthScale = 1.0 / speed;
        var prepClock = Stopwatch.StartNew();
        var runtime = await EnsureRuntimeAsync(ct);
        var runtimeMs = prepClock.ElapsedMilliseconds;
        var voice = await EnsureVoiceAsync(lang, ct);
        LogService.Info($"Piper runtime and voice ready: language={lang}; runtime_elapsed_ms={runtimeMs}; voice_prep_elapsed_ms={prepClock.ElapsedMilliseconds - runtimeMs}");

        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"piper|{VoiceRevision}|{voice.Key}|{lengthScale:0.00}|{text}")));
        var output = Path.Combine(_storage.CacheDirectory, cacheKey + ".wav");
        if (File.Exists(output) && new FileInfo(output).Length > 256)
        {
            LogService.Info($"Piper TTS cache hit: language={lang}; bytes={new FileInfo(output).Length}; prep_elapsed_ms={prepClock.ElapsedMilliseconds}; elapsed_ms={totalClock.ElapsedMilliseconds}");
            return output;
        }
        LogService.Info($"Piper TTS cache miss: language={lang}; chars={text.Length}; speed={speed:0.00}; length_scale={lengthScale:0.00}");

        var psi = new ProcessStartInfo
        {
            FileName = runtime,
            WorkingDirectory = Path.GetDirectoryName(runtime)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        // Keep the emergency local engine conservative on CPU and memory.
        // Piper/ONNX may otherwise create several worker threads on larger CPUs.
        psi.Environment["OMP_NUM_THREADS"] = "1";
        psi.Environment["OMP_WAIT_POLICY"] = "PASSIVE";
        psi.Environment["OPENBLAS_NUM_THREADS"] = "1";

        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(voice.ModelPath);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(voice.ConfigPath);
        psi.ArgumentList.Add("--output_file");
        var processOutput = output + "." + Guid.NewGuid().ToString("N") + ".tmp.wav";
        psi.ArgumentList.Add(processOutput);
        psi.ArgumentList.Add("--length_scale");
        psi.ArgumentList.Add(lengthScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        using var appProcess = Process.GetCurrentProcess();
        var originalAppWorkingSet = CaptureWorkingSet(appProcess);
        var appCapApplied = false;
        try
        {
            // Make room before loading the neural model. WPF/.NET can page inactive UI/code
            // back in later; the app and Piper share the configured 1 GB working-set budget.
            CompactAndTrim(appProcess);
            appCapApplied = TryApplyWorkingSetCap(appProcess, AppWorkingSetCapMb);
            if (appCapApplied) TryEmptyWorkingSet(appProcess);

            ProgressChanged?.Invoke($"Piper offline • {lang.ToUpperInvariant()} • generating audio…");
            if (!process.Start()) throw new InvalidOperationException("Could not start the local Piper process.");
            _activeProcess = process;
            LogService.Info($"Piper process started: language={lang}; startup_elapsed_ms={totalClock.ElapsedMilliseconds}");

            var piperCapApplied = TryApplyWorkingSetCap(process, PiperWorkingSetCapMb);
            if (piperCapApplied) TryEmptyWorkingSet(process);

            await process.StandardInput.WriteLineAsync(text.AsMemory(), ct);
            process.StandardInput.Close();
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var waitTask = process.WaitForExitAsync(timeout.Token);
            var overBudgetSamples = 0;
            while (!waitTask.IsCompleted)
            {
                await Task.WhenAny(waitTask, Task.Delay(140, timeout.Token));
                if (waitTask.IsCompleted) break;

                var piperMb = GetWorkingSetMb(process);
                ProcessMemoryChanged?.Invoke(piperMb);
                var appMb = GetWorkingSetMb(appProcess);
                var totalMb = appMb + piperMb;

                if (totalMb > MemoryLimitMb)
                {
                    // First try paging inactive pages instead of immediately killing speech.
                    TryEmptyWorkingSet(appProcess);
                    TryEmptyWorkingSet(process);
                    await Task.Delay(25, timeout.Token);
                    totalMb = GetWorkingSetMb(appProcess) + GetWorkingSetMb(process);
                }

                if (totalMb > EmergencyAbortThresholdMb)
                {
                    overBudgetSamples++;
                    if (overBudgetSamples >= 5)
                    {
                        try { process.Kill(true); } catch { }
                        throw new InvalidOperationException($"Piper could not stay within the {MemoryLimitMb} MB RAM limit, even after reducing its working set.");
                    }
                }
                else
                {
                    overBudgetSamples = 0;
                }
            }
            await waitTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Piper exited with an error" + (string.IsNullOrWhiteSpace(stderr) ? "." : ": " + LastLine(stderr)));
            if (!File.Exists(processOutput) || new FileInfo(processOutput).Length < 256)
                throw new InvalidOperationException("Piper did not create an audio file.");
            File.Move(processOutput, output, true);
            _storage.PruneAudioCache(output);
            LogService.Info($"Piper synthesis complete: language={lang}; bytes={new FileInfo(output).Length}; process_exit_code={process.ExitCode}; elapsed_ms={totalClock.ElapsedMilliseconds}");
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw new TimeoutException("Local Piper speech did not finish in time.");
        }
        finally
        {
            _activeProcess = null;
            ProcessMemoryChanged?.Invoke(null);
            try
            {
                if (appCapApplied) RestoreWorkingSet(appProcess, originalAppWorkingSet);
                TryEmptyWorkingSet(appProcess);
            }
            catch { }
            process.Dispose();
            try { if (File.Exists(processOutput)) File.Delete(processOutput); } catch { }
        }
    }


    private static WorkingSetSnapshot CaptureWorkingSet(Process process)
    {
        try
        {
            if (GetProcessWorkingSetSizeEx(process.Handle, out var min, out var max, out var flags))
                return new WorkingSetSnapshot(min, max, flags, true);
        }
        catch { }
        return new WorkingSetSnapshot(UIntPtr.Zero, UIntPtr.Zero, 0, false);
    }

    private static bool TryApplyWorkingSetCap(Process process, int maxMb)
    {
        try
        {
            var minBytes = new UIntPtr((ulong)WorkingSetMinimumMb * 1024UL * 1024UL);
            var maxBytes = new UIntPtr((ulong)maxMb * 1024UL * 1024UL);
            return SetProcessWorkingSetSizeEx(process.Handle, minBytes, maxBytes,
                QuotaLimitsHardWsMinDisable | QuotaLimitsHardWsMaxEnable);
        }
        catch { return false; }
    }

    private static void RestoreWorkingSet(Process process, WorkingSetSnapshot snapshot)
    {
        if (!snapshot.Valid) return;
        try { SetProcessWorkingSetSizeEx(process.Handle, snapshot.Minimum, snapshot.Maximum, snapshot.Flags); }
        catch { }
    }

    private static void CompactAndTrim(Process process)
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { }
        TryEmptyWorkingSet(process);
    }

    private static void TryEmptyWorkingSet(Process process)
    {
        try { K32EmptyWorkingSet(process.Handle); } catch { }
    }

    private static long GetWorkingSetMb(Process process)
    {
        try
        {
            process.Refresh();
            return Math.Max(0, process.WorkingSet64 / (1024 * 1024));
        }
        catch { return 0; }
    }

    private async Task<string> EnsureRuntimeAsync(CancellationToken ct)
    {
        var existing = FindRuntimeExe();
        if (existing is not null) return existing;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Local Piper in this build is configured for Windows x64. Online speech is still available.");

        await _installLock.WaitAsync(ct);
        try
        {
            existing = FindRuntimeExe();
            if (existing is not null) return existing;

            ProgressChanged?.Invoke("Offline fallback: downloading the Piper runtime…");
            var zip = Path.Combine(_storage.PiperDirectory, "piper-runtime.zip.tmp");
            var stage = Path.Combine(_storage.PiperDirectory, "runtime.tmp");
            var target = Path.Combine(_storage.PiperDirectory, "runtime");
            try
            {
                await DownloadFileAsync(RuntimeUrl, zip, ct);
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                ZipFile.ExtractToDirectory(zip, stage, true);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(stage, target);
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            }

            existing = FindRuntimeExe();
            if (existing is null) throw new InvalidOperationException("The Piper archive was downloaded, but piper.exe was not found inside it.");
            return existing;
        }
        finally { _installLock.Release(); }
    }

    private async Task<VoiceProfile> EnsureVoiceAsync(string languageCode, CancellationToken ct)
    {
        var lang = NormalizeLanguage(languageCode);
        var installed = FindInstalledVoice(lang);
        if (installed is not null) return installed;

        await _installLock.WaitAsync(ct);
        try
        {
            installed = FindInstalledVoice(lang);
            if (installed is not null) return installed;

            var profile = CoreProfile(lang) ?? await FindSmallestCatalogVoiceAsync(lang, ct)
                ?? throw new InvalidOperationException($"No local Piper voice was found for {lang.ToUpperInvariant()}.");

            var voiceDir = Path.Combine(_storage.PiperVoicesDirectory, profile.Family, SanitizeFileName(profile.Key));
            Directory.CreateDirectory(voiceDir);
            var model = Path.Combine(voiceDir, Path.GetFileName(profile.ModelPath));
            var config = Path.Combine(voiceDir, Path.GetFileName(profile.ConfigPath));

            if (!File.Exists(model) || new FileInfo(model).Length < Math.Max(1_000_000, profile.SizeBytes / 3))
            {
                var mb = Math.Max(1, profile.SizeBytes / (1024 * 1024));
                ProgressChanged?.Invoke($"Offline fallback: downloading the {lang.ToUpperInvariant()} voice (~{mb} MB)…");
                await DownloadFileAsync(VoiceBaseUrl + profile.ModelPath + "?download=true", model + ".tmp", ct);
                File.Move(model + ".tmp", model, true);
            }
            if (!File.Exists(config) || new FileInfo(config).Length < 100)
            {
                await DownloadFileAsync(VoiceBaseUrl + profile.ConfigPath + "?download=true", config + ".tmp", ct);
                File.Move(config + ".tmp", config, true);
            }

            return profile with { ModelPath = model, ConfigPath = config };
        }
        finally { _installLock.Release(); }
    }

    private VoiceProfile? FindInstalledVoice(string languageCode)
    {
        var lang = NormalizeLanguage(languageCode);
        try
        {
            if (!Directory.Exists(_storage.PiperVoicesDirectory)) return null;
            foreach (var model in Directory.EnumerateFiles(_storage.PiperVoicesDirectory, "*.onnx", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(model);
                if (!name.StartsWith(lang + "_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase)) continue;
                var config = model + ".json";
                if (!File.Exists(config)) continue;
                return new VoiceProfile(Path.GetFileNameWithoutExtension(model), lang, model, config, new FileInfo(model).Length);
            }
        }
        catch { }
        return null;
    }

    private static VoiceProfile? CoreProfile(string lang) => lang switch
    {
        "ru" => new VoiceProfile("ru_RU-ruslan-medium", "ru", "ru/ru_RU/ruslan/medium/ru_RU-ruslan-medium.onnx", "ru/ru_RU/ruslan/medium/ru_RU-ruslan-medium.onnx.json", 63_201_294),
        "uk" => new VoiceProfile("uk_UA-lada-x_low", "uk", "uk/uk_UA/lada/x_low/uk_UA-lada-x_low.onnx", "uk/uk_UA/lada/x_low/uk_UA-lada-x_low.onnx.json", 20_628_813),
        "en" => new VoiceProfile("en_US-lessac-low", "en", "en/en_US/lessac/low/en_US-lessac-low.onnx", "en/en_US/lessac/low/en_US-lessac-low.onnx.json", 63_201_294),
        _ => null
    };

    private async Task<VoiceProfile?> FindSmallestCatalogVoiceAsync(string lang, CancellationToken ct)
    {
        var json = await LoadCatalogAsync(ct);
        using var doc = JsonDocument.Parse(json);
        VoiceProfile? best = null;
        var bestScore = long.MaxValue;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var item = property.Value;
            if (!item.TryGetProperty("language", out var language) ||
                !language.TryGetProperty("family", out var familyProp) ||
                !string.Equals(familyProp.GetString(), lang, StringComparison.OrdinalIgnoreCase)) continue;

            var quality = item.TryGetProperty("quality", out var q) ? q.GetString() ?? "medium" : "medium";
            if (!item.TryGetProperty("files", out var files)) continue;
            string? modelPath = null, configPath = null;
            long size = long.MaxValue;
            foreach (var f in files.EnumerateObject())
            {
                if (f.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    modelPath = f.Name;
                    if (f.Value.TryGetProperty("size_bytes", out var sz) && sz.TryGetInt64(out var n)) size = n;
                }
                else if (f.Name.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase)) configPath = f.Name;
            }
            if (modelPath is null || configPath is null || size == long.MaxValue) continue;
            var qualityPenalty = quality switch { "x_low" => 0L, "low" => 5_000_000L, "medium" => 25_000_000L, _ => 80_000_000L };
            var score = size + qualityPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                best = new VoiceProfile(property.Name, lang, modelPath, configPath, size);
            }
        }
        return best;
    }

    private async Task<string> LoadCatalogAsync(CancellationToken ct)
    {
        if (File.Exists(_storage.PiperCatalogFile))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_storage.PiperCatalogFile);
                if (age < TimeSpan.FromDays(90)) return await File.ReadAllTextAsync(_storage.PiperCatalogFile, ct);
            }
            catch { }
        }

        try
        {
            ProgressChanged?.Invoke("Offline fallback: retrieving the Piper voice catalog…");
            var json = await _http.GetStringAsync(CatalogUrl, ct);
            try { await File.WriteAllTextAsync(_storage.PiperCatalogFile, json, new UTF8Encoding(false), ct); } catch { }
            return json;
        }
        catch when (File.Exists(_storage.PiperCatalogFile))
        {
            return await File.ReadAllTextAsync(_storage.PiperCatalogFile, ct);
        }
    }

    private async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await input.CopyToAsync(output, 64 * 1024, ct);
        await output.FlushAsync(ct);
    }

    private string? FindRuntimeExe()
    {
        var candidates = new[]
        {
            Path.Combine(_storage.PiperDirectory, "runtime", "piper", "piper.exe"),
            Path.Combine(_storage.PiperDirectory, "runtime", "piper.exe"),
            Path.Combine(_storage.PiperDirectory, "piper", "piper.exe"),
            Path.Combine(_storage.PiperDirectory, "piper.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string NormalizeLanguage(string? lang)
    {
        var value = (lang ?? "en").Trim().Replace('_', '-').ToLowerInvariant();
        return value.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "en";
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }

    private static string LastLine(string value)
        => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? value.Trim();

    public void Dispose()
    {
        try { if (_activeProcess is { HasExited: false }) _activeProcess.Kill(true); } catch { }
        _installLock.Dispose();
        _http.Dispose();
    }
}
