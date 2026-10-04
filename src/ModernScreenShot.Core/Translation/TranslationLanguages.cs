namespace ModernScreenShot.Core.Translation;

/// <summary>
/// One language the translation model understands.
///
/// Hy-MT2 is a single multilingual model rather than a package per language pair (that was the Argos
/// design), so a direction is just two of these — nothing has to be downloaded per pair. Names carry
/// both scripts because the app's UI switches between Chinese and English at runtime; the model
/// itself is prompted in English, which is what Tencent documents and what was measured.
/// </summary>
public sealed record TranslationLanguage(string Code, string EnglishName, string ChineseName)
{
    /// <summary>Name for the current UI language; falls back to English.</summary>
    public string DisplayName(bool chinese) => chinese ? ChineseName : EnglishName;

    public override string ToString() => EnglishName;
}

/// <summary>The 36 languages Hy-MT2 translates between.</summary>
public static class TranslationLanguages
{
    public static readonly IReadOnlyList<TranslationLanguage> All =
    [
        new("zh", "Chinese", "中文"),
        new("en", "English", "英语"),
        new("ja", "Japanese", "日语"),
        new("ko", "Korean", "韩语"),
        new("fr", "French", "法语"),
        new("de", "German", "德语"),
        new("es", "Spanish", "西班牙语"),
        new("pt", "Portuguese", "葡萄牙语"),
        new("it", "Italian", "意大利语"),
        new("ru", "Russian", "俄语"),
        new("uk", "Ukrainian", "乌克兰语"),
        new("pl", "Polish", "波兰语"),
        new("cs", "Czech", "捷克语"),
        new("nl", "Dutch", "荷兰语"),
        new("tr", "Turkish", "土耳其语"),
        new("ar", "Arabic", "阿拉伯语"),
        new("he", "Hebrew", "希伯来语"),
        new("fa", "Persian", "波斯语"),
        new("ur", "Urdu", "乌尔都语"),
        new("hi", "Hindi", "印地语"),
        new("bn", "Bengali", "孟加拉语"),
        new("gu", "Gujarati", "古吉拉特语"),
        new("mr", "Marathi", "马拉地语"),
        new("ta", "Tamil", "泰米尔语"),
        new("te", "Telugu", "泰卢固语"),
        new("th", "Thai", "泰语"),
        new("vi", "Vietnamese", "越南语"),
        new("id", "Indonesian", "印尼语"),
        new("ms", "Malay", "马来语"),
        new("tl", "Filipino", "菲律宾语"),
        new("km", "Khmer", "高棉语"),
        new("my", "Burmese", "缅甸语"),
        new("bo", "Tibetan", "藏语"),
        new("kk", "Kazakh", "哈萨克语"),
        new("mn", "Mongolian", "蒙古语"),
        new("ug", "Uyghur", "维吾尔语"),
    ];

    public static TranslationLanguage? ByCode(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>English name for the prompt; the code itself when it is not a known language.</summary>
    public static string EnglishNameOf(string code) => ByCode(code)?.EnglishName ?? code;

    /// <summary>Localized label for a code, used by the result window and the settings list.</summary>
    public static string DisplayNameOf(string code, bool chinese) => ByCode(code)?.DisplayName(chinese) ?? code.ToUpperInvariant();
}
