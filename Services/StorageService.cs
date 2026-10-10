using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using ClipVoice.Models;

namespace ClipVoice.Services;

public sealed class StorageService
{
    public const long AudioCacheLimitBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan AudioCacheMaxAge = TimeSpan.FromDays(30);
    private readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVoice");
    private string StateFile => Path.Combine(_dir, "state.dat");
    private string StateBackup => Path.Combine(_dir, "state.bak");
    private string SettingsFile => Path.Combine(_dir, "settings.json");
    private string SettingsBackup => Path.Combine(_dir, "settings.bak.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DataDirectory => _dir;
    public string CacheDirectory => Path.Combine(_dir, "cache");
    public string VoiceCacheFile => Path.Combine(_dir, "voices.json");
    public string PiperDirectory => Path.Combine(_dir, "piper");
    public string PiperVoicesDirectory => Path.Combine(PiperDirectory, "voices");
    public string PiperCatalogFile => Path.Combine(PiperDirectory, "voices-v1.0.0.json");

    public StorageService()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(PiperDirectory);
        Directory.CreateDirectory(PiperVoicesDirectory);
    }

    public AppSettings LoadSettings()
    {
        foreach (var file in new[] { SettingsFile, SettingsBackup })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions);
                if (settings is not null) return settings;
            }
            catch { }
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicWrite(SettingsFile, SettingsBackup, Encoding.UTF8.GetBytes(json));
    }

    public PersistedState LoadState()
    {
        foreach (var file in new[] { StateFile, StateBackup })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var encrypted = File.ReadAllBytes(file);
                var json = Encoding.UTF8.GetString(DpapiService.Unprotect(encrypted));
                var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions);
                if (state is not null) return state;
            }
            catch { }
        }
        return DefaultState();
    }

    public void SaveState(IEnumerable<ClipboardItem> history, IEnumerable<TemplateFolder> folders)
    {
        var state = new PersistedState
        {
            History = history.ToList(),
            TemplateFolders = folders.Select(f => new TemplateFolderDto
            {
                Id = f.Id,
                Name = f.Name,
                Templates = f.Templates.ToList()
            }).ToList()
        };
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var encrypted = DpapiService.Protect(Encoding.UTF8.GetBytes(json));
        AtomicWrite(StateFile, StateBackup, encrypted);
    }

    public void PruneAudioCache(string? keepFile = null)
    {
        Directory.CreateDirectory(CacheDirectory);
        var keepPath = string.IsNullOrWhiteSpace(keepFile) ? null : Path.GetFullPath(keepFile);
        var now = DateTime.UtcNow;
        var files = Directory.EnumerateFiles(CacheDirectory)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) &&
                           (string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)))
            .Select(path =>
            {
                try { return new FileInfo(path); }
                catch { return null; }
            })
            .Where(file => file is { Exists: true })
            .Cast<FileInfo>()
            .ToList();

        foreach (var file in files)
        {
            if (keepPath is not null && string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (now - file.LastWriteTimeUtc <= AudioCacheMaxAge) continue;
            try { file.Delete(); } catch { }
        }

        long totalBytes = 0;
        var retained = files
            .Where(file => file.Exists && (keepPath is null || !string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var keepInfo = keepPath is null ? null : files.FirstOrDefault(f => string.Equals(f.FullName, keepPath, StringComparison.OrdinalIgnoreCase) && f.Exists);
        if (keepInfo is not null) totalBytes += keepInfo.Length;
        foreach (var file in retained)
        {
            if (file.Exists) totalBytes += file.Length;
        }

        foreach (var file in retained)
        {
            if (totalBytes <= AudioCacheLimitBytes) break;
            try
            {
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
            }
            catch { }
        }
    }

    public static ObservableCollection<TemplateFolder> ToFolders(PersistedState state)
    {
        var result = new ObservableCollection<TemplateFolder>();
        foreach (var f in state.TemplateFolders)
            result.Add(new TemplateFolder { Id = f.Id == Guid.Empty ? Guid.NewGuid() : f.Id, Name = f.Name, Templates = new ObservableCollection<TextTemplate>(f.Templates) });
        if (result.Count == 0)
            result.Add(new TemplateFolder { Name = "My templates" });
        return result;
    }

    private static void AtomicWrite(string target, string backup, byte[] data)
    {
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, data);
        if (File.Exists(target))
        {
            try
            {
                File.Replace(temp, target, backup, true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }
        if (File.Exists(target))
        {
            try { File.Copy(target, backup, true); } catch { }
            File.Delete(target);
        }
        File.Move(temp, target, true);
    }

    private static PersistedState DefaultState() => new()
    {
        TemplateFolders = new List<TemplateFolderDto>
        {
            new() { Id = Guid.NewGuid(), Name = "My templates", Templates = new List<TextTemplate>() }
        }
    };
}
