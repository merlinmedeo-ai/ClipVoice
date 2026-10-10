namespace ClipVoice.Services;

public static class TranslationDirectionService
{
    public static string ResolveTargetLanguage(string detectedLanguage, string preferredCyrillicLanguage)
    {
        var source = NormalizeLanguage(detectedLanguage);
        var preferred = NormalizePreferred(preferredCyrillicLanguage);

        // Russian and Ukrainian always go to English.
        // Every other detected language goes to the user's chosen Cyrillic language.
        return source is "ru" or "uk" ? "en" : preferred;
    }

    public static string NormalizePreferred(string language)
        => string.Equals(language, "uk", StringComparison.OrdinalIgnoreCase) ? "uk" : "ru";

    public static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return string.Empty;
        var value = language.Trim().ToLowerInvariant();
        var separator = value.IndexOfAny(new[] { '-', '_' });
        return separator > 0 ? value[..separator] : value;
    }

    public static string DisplayName(string language)
    {
        var code = NormalizeLanguage(language);
        return code switch
        {
            "ru" => "Russian",
            "uk" => "Ukrainian",
            "en" => "English",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            "it" => "Italian",
            "pt" => "Portuguese",
            "pl" => "Polish",
            "cs" => "Czech",
            "sk" => "Slovak",
            "ro" => "Romanian",
            "nl" => "Dutch",
            "sv" => "Swedish",
            "no" => "Norwegian",
            "da" => "Danish",
            "fi" => "Finnish",
            "tr" => "Turkish",
            "el" => "Greek",
            "bg" => "Bulgarian",
            "be" => "Belarusian",
            "sr" => "Serbian",
            "hr" => "Croatian",
            "hu" => "Hungarian",
            "lt" => "Lithuanian",
            "lv" => "Latvian",
            "et" => "Estonian",
            "ar" => "Arabic",
            "he" => "Hebrew",
            "zh" => "Chinese",
            "ja" => "Japanese",
            "ko" => "Korean",
            _ => string.IsNullOrWhiteSpace(code) ? "Auto" : code.ToUpperInvariant()
        };
    }
}
