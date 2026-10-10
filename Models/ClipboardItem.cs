using System.Text.Json.Serialization;

namespace ClipVoice.Models;

public enum ClipboardItemKind { Text, Files }

public sealed class ClipboardItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipboardItemKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string Preview => Kind == ClipboardItemKind.Text
        ? (Text.Length > 180 ? Text[..180] + "…" : Text)
        : $"📁 {Files.Count} файл(ов): {string.Join(", ", Files.Select(Path.GetFileName).Take(3))}";

    [JsonIgnore]
    public string TimeText => CreatedAt.ToString("HH:mm");

    public string Fingerprint() => Kind == ClipboardItemKind.Text
        ? "T:" + Text
        : "F:" + string.Join("|", Files);
}
