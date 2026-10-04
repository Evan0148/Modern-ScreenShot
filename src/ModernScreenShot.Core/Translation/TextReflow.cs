using System.Text;

namespace ModernScreenShot.Core.Translation;

/// <summary>
/// Turns recognized screen text into paragraphs before it reaches the translation engine.
///
/// Screen text is hard-wrapped: a sentence the app renders across four lines arrives as four lines.
/// Those breaks are an artefact of layout, not of the text, and feeding them through makes the
/// engine translate line by line — the symptom is a paragraph coming back as a column of
/// disconnected fragments rather than a sentence. (The engine this app used before Argos/OPUS-MT
/// was even stricter about it: it split its input on <em>every</em> newline and translated each piece
/// separately, so the same reflow fixed the same class of bug there.)
///
/// So lines that continue the same sentence are stitched back together here, and a new paragraph
/// starts only where the text itself suggests one (a blank line, the end of a sentence, or a list
/// marker). Visual line breaks are deliberately not preserved: they carry no meaning for a
/// translator.
/// </summary>
public static class TextReflow
{
    /// <summary>Joins hard-wrapped lines into paragraphs. Safe on null/empty input.</summary>
    public static string ToParagraphs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraphs = new List<StringBuilder>();
        StringBuilder? current = null;
        string? previous = null;

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                current = null;   // a blank line always ends the paragraph
                previous = null;
                continue;
            }

            if (current is null || previous is null || StartsNewParagraph(previous, line))
            {
                current = new StringBuilder(line);
                paragraphs.Add(current);
            }
            else
            {
                AppendWrapped(current, line);
            }
            previous = line;
        }

        return string.Join("\n", paragraphs.Select(p => p.ToString()));
    }

    /// <summary>Joins a continuation line onto the paragraph being built.</summary>
    private static void AppendWrapped(StringBuilder paragraph, string line)
    {
        if (paragraph.Length == 0)
        {
            paragraph.Append(line);
            return;
        }

        char last = paragraph[^1];
        // A hyphen at a line break is a split word ("exam-" + "ple"), not punctuation.
        if (last == '-' && paragraph.Length >= 2 && char.IsLetter(paragraph[^2]))
        {
            paragraph.Length -= 1;
            paragraph.Append(line);
            return;
        }

        // CJK text carries no word spaces, so nothing is inserted at a CJK boundary.
        if (IsCjk(last) || IsCjk(line[0]))
        {
            paragraph.Append(line);
            return;
        }

        paragraph.Append(' ').Append(line);
    }

    private static bool StartsNewParagraph(string previous, string line) =>
        EndsSentence(previous) || StartsWithListMarker(line);

    /// <summary>True when the line ends where a sentence ends.</summary>
    private static bool EndsSentence(string line)
    {
        if (line.Length == 0) return false;
        // Trailing quotes/brackets sit after the full stop and must not hide it.
        int i = line.Length - 1;
        while (i >= 0 && IsTrailingCloser(line[i])) i--;
        if (i < 0) return false;
        char end = line[i];
        return end is '.' or '!' or '?' or ';' or '\u3002' or '\uFF01' or '\uFF1F' or '\uFF1B' or '\u2026';
    }

    private static bool IsTrailingCloser(char c) =>
        c is '"' or '\'' or ')' or ']' or '}'
          or '\u201D' or '\u2019' or '\uFF09' or '\u3011' or '\u300B' or '\u300D' or '\u300F';

    /// <summary>Bullets and "1." / "a)" style enumerations start their own paragraph.</summary>
    private static bool StartsWithListMarker(string line)
    {
        char first = line[0];
        if (first is '\u2022' or '\u00B7' or '\u25CF' or '\u25AA' or '\u2023' or '-' or '*' or '\u2013' or '\u2014')
            return true;

        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i > 0 && i < line.Length && (line[i] == '.' || line[i] == ')' || line[i] == '\u3001')) return true;

        // "a) some item" — a single letter followed by a bracket.
        return line.Length > 2 && char.IsLetter(first) && (line[1] == ')' || line[1] == '.') && line[2] == ' ';
    }

    private static bool IsCjk(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'
          or >= '\u3400' and <= '\u4DBF'
          or >= '\uF900' and <= '\uFAFF'
          or >= '\u3000' and <= '\u303F'
          or >= '\u3040' and <= '\u30FF'
          or >= '\uAC00' and <= '\uD7AF'
          or >= '\uFF00' and <= '\uFFEF';
}
