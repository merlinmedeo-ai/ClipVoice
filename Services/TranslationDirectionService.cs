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
            "ru" => "русский",
            "uk" => "украинский",
            "en" => "английский",
            "fr" => "французский",
            "de" => "немецкий",
            "es" => "испанский",
            "it" => "итальянский",
            "pt" => "португальский",
            "pl" => "польский",
            "cs" => "чешский",
            "sk" => "словацкий",
            "ro" => "румынский",
            "nl" => "нидерландский",
            "sv" => "шведский",
            "no" => "норвежский",
            "da" => "датский",
            "fi" => "финский",
            "tr" => "турецкий",
            "el" => "греческий",
            "bg" => "болгарский",
            "be" => "белорусский",
            "sr" => "сербский",
            "hr" => "хорватский",
            "hu" => "венгерский",
            "lt" => "литовский",
            "lv" => "латышский",
            "et" => "эстонский",
            "ar" => "арабский",
            "he" => "иврит",
            "zh" => "китайский",
            "ja" => "японский",
            "ko" => "корейский",
            _ => string.IsNullOrWhiteSpace(code) ? "авто" : code.ToUpperInvariant()
        };
    }
}
