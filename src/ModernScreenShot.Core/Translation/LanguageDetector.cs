namespace ModernScreenShot.Core.Translation;

/// <summary>
/// Picks the source language of a recognized string from the writing system it uses.
///
/// This is deliberately a script heuristic, not a statistical language identifier: the app only
/// needs to tell "the text on screen is Chinese" from "the text on screen is Latin-script", which
/// decides the direction of the translation. It costs nothing, runs offline, and cannot mis-identify
/// a language the engine has no package for.
/// </summary>
public static class LanguageDetector
{
    /// <summary>"zh" when the text is meaningfully CJK, "en" otherwise.</summary>
    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "en";

        int cjk = 0, latin = 0;
        foreach (char c in text)
        {
            if (IsCjk(c)) cjk++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
        }

        if (cjk == 0) return "en";
        if (latin == 0) return "zh";
        // CJK carries far more meaning per character, so a modest share of CJK characters already
        // means the text is primarily Chinese even when Latin words (product names, code) appear.
        return cjk * 2 >= latin ? "zh" : "en";
    }

    /// <summary>CJK unified ideographs, extensions, and the CJK punctuation/symbol blocks.</summary>
    private static bool IsCjk(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'   // CJK Unified Ideographs
          or >= '\u3400' and <= '\u4DBF'   // Extension A
          or >= '\uF900' and <= '\uFAFF'   // Compatibility Ideographs
          or >= '\u3000' and <= '\u303F'   // CJK Symbols and Punctuation
          or >= '\uFF00' and <= '\uFFEF';  // Halfwidth and Fullwidth Forms
}
