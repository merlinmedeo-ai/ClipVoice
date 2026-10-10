namespace ClipVoice.Models;

public sealed class AppSettings
{
    public string HotkeyPanel { get; set; } = "Ctrl+Alt+Space";
    public string HotkeySpeak { get; set; } = "Ctrl+Alt+S";
    public string HotkeyTranslate { get; set; } = "Ctrl+Alt+T";
    public string HotkeyTranslateSpeak { get; set; } = "Ctrl+Alt+Y";
    public bool AutoStart { get; set; }
    public bool ShowEdgeHandle { get; set; } = false;
    public bool AutoCaptureSelection { get; set; } = true;
    public bool HidePanelAfterPaste { get; set; } = true;
    public bool HidePanelWhenFocusLost { get; set; } = true;
    public bool EnableAltRightClickTemplates { get; set; } = true;
    public string TemplateMouseModifier { get; set; } = "Alt";
    public int HistoryLimit { get; set; } = 25;
    public int SpeechRatePercent { get; set; } = 0;
    public bool AutoPrepareLocalTts { get; set; } = false;
    public string PreferredCyrillicTranslationLanguage { get; set; } = "ru";
    public string RussianVoice { get; set; } = "ru-RU-SvetlanaNeural";
    public string EnglishVoice { get; set; } = "en-US-EmmaMultilingualNeural";
    public string UkrainianVoice { get; set; } = "uk-UA-PolinaNeural";
}

public sealed class PersistedState
{
    public List<ClipboardItem> History { get; set; } = new();
    public List<TemplateFolderDto> TemplateFolders { get; set; } = new();
}

public sealed class TemplateFolderDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<TextTemplate> Templates { get; set; } = new();
}
