using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace ClipVoice.Models;

public sealed class TextTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New template";
    public string Content { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    public bool PasteOnHotkey { get; set; } = true;
    public bool IsSecret { get; set; }

    [JsonIgnore]
    public string PreviewContent => IsSecret ? "••••••••" : Content;

    [JsonIgnore]
    public string HotkeyBadge => string.IsNullOrWhiteSpace(Hotkey) ? "no hotkey" : Hotkey;

    public override string ToString() => Name;
}

public sealed class TemplateFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New folder";
    public ObservableCollection<TextTemplate> Templates { get; set; } = new();
    public override string ToString() => Name;
}
