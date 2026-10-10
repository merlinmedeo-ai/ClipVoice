using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ClipVoice.Services;

public static class LogService
{
    private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVoice", "logs");
    private static readonly object Sync = new();
    private static readonly object ErrorThrottleSync = new();
    private static readonly Dictionary<string, DateTime> RecentErrors = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<string?> CurrentOperation = new();
    private static DateOnly _lastLogCleanupDate;

    // Routine speech, clipboard and translation activity never writes a log file.
    public static bool DetailedLoggingEnabled => false;
    public static string? CurrentOperationId => CurrentOperation.Value;

    public static void Info([InterpolatedStringHandlerArgument()] ref QuietLogMessageHandler message) { }

    public static void InfoForOperation(string? operationId,
        [InterpolatedStringHandlerArgument(nameof(operationId))] ref QuietLogMessageHandler message) { }

    public static IDisposable BeginOperation(string action, string entryPoint)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = Guid.NewGuid().ToString("N")[..8];
        return new OperationScope(previous);
    }

    public static void Write(string title, Exception ex)
    {
        var signature = title + "|" + ExceptionSignature(ex);
        var now = DateTime.UtcNow;
        lock (ErrorThrottleSync)
        {
            if (RecentErrors.TryGetValue(signature, out var last) && now - last < TimeSpan.FromMinutes(30)) return;
            RecentErrors[signature] = now;
            if (RecentErrors.Count > 512)
            {
                foreach (var key in new List<string>(RecentErrors.Keys))
                    if (now - RecentErrors[key] > TimeSpan.FromMinutes(10)) RecentErrors.Remove(key);
                if (RecentErrors.Count > 512) RecentErrors.Clear();
                RecentErrors[signature] = now;
            }
        }

        // Exception messages and request URLs can contain copied text. Store only
        // the failure class and stack frames so diagnostic logs never retain text.
        var lines = new List<string> { $"{title}; exceptions={ExceptionSignature(ex)}" };
        try
        {
            var frames = new StackTrace(ex, true).GetFrames();
            if (frames is not null)
            {
                foreach (var frame in frames.Take(8))
                {
                    var method = frame.GetMethod();
                    if (method is null) continue;
                    var location = frame.GetFileLineNumber();
                    lines.Add($"  at {method.DeclaringType?.FullName}.{method.Name}" + (location > 0 ? $" (line {location})" : string.Empty));
                }
            }
        }
        catch { }

        Append("ERROR", string.Join(Environment.NewLine, lines));
    }

    private static string ExceptionSignature(Exception ex)
    {
        var types = new List<string>();
        Exception? current = ex;
        for (var i = 0; current is not null && i < 5; i++, current = current.InnerException)
        {
            var name = current.GetType().Name;
            if (current is HttpRequestException http && http.StatusCode is { } status)
                name += $"(HTTP {(int)status})";
            else if (current is TaskCanceledException)
                name += "(timeout)";
            types.Add(name);
        }
        return string.Join(" -> ", types);
    }

    private static void Append(string level, string message, string? operationId = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDir);
                var now = DateTime.Now;
                var path = Path.Combine(LogDir, "errors-" + now.ToString("yyyy-MM-dd") + ".log");
                if (_lastLogCleanupDate != DateOnly.FromDateTime(now))
                {
                    foreach (var old in Directory.GetFiles(LogDir, "errors-*.log"))
                    {
                        try
                        {
                            if (now - File.GetLastWriteTime(old) > TimeSpan.FromDays(14)) File.Delete(old);
                        }
                        catch { }
                    }
                    _lastLogCleanupDate = DateOnly.FromDateTime(now);
                }
                const long maxFileBytes = 2L * 1024 * 1024;
                const int retainedTailChars = 1024 * 1024;
                if (File.Exists(path) && new FileInfo(path).Length > maxFileBytes)
                {
                    var oldContent = File.ReadAllText(path);
                    File.WriteAllText(path, oldContent.Length <= retainedTailChars ? oldContent : oldContent[^retainedTailChars..]);
                }
                var operation = operationId is not null ? $" op={operationId}"
                    : CurrentOperation.Value is string id ? $" op={id}" : string.Empty;
                File.AppendAllText(path, $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {level}{operation} {message}\r\n");
            }
        }
        catch { }
    }

    public static string ReadRecentLogs(int maxChars = 350_000)
    {
        try
        {
            lock (Sync)
            {
                if (!Directory.Exists(LogDir)) return string.Empty;
                var files = Directory.GetFiles(LogDir, "errors-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(3)
                    .OrderBy(File.GetLastWriteTimeUtc)
                    .ToArray();
                var content = string.Join(Environment.NewLine, files.Select(File.ReadAllText));
                return content.Length <= maxChars ? content : content[^maxChars..];
            }
        }
        catch (Exception ex)
        {
            return $"Не удалось прочитать логи ClipVoice: {ex.GetType().Name}";
        }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public OperationScope(string? previous) { _previous = previous; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentOperation.Value = _previous;
        }
    }
}

[InterpolatedStringHandler]
public ref struct QuietLogMessageHandler
{
    public QuietLogMessageHandler(int literalLength, int formattedCount, out bool shouldAppend)
        => shouldAppend = false;

    public QuietLogMessageHandler(int literalLength, int formattedCount, string? operationId, out bool shouldAppend)
        => shouldAppend = false;

    public void AppendLiteral(string value) { }
    public void AppendFormatted<T>(T value) { }
    public void AppendFormatted<T>(T value, string? format) { }
    public void AppendFormatted<T>(T value, int alignment) { }
    public void AppendFormatted<T>(T value, int alignment, string? format) { }
}
