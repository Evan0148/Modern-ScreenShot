using System.Text;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.Core.Ocr;

/// <summary>
/// Rebuilds readable lines from the boxes the detector reports.
///
/// PP-OCRv5 detects *boxes*, not lines, and it happily emits one box per word when the spacing is
/// wide — a poster, a headline, or any text with generous tracking comes back as "Jason" / "and" /
/// "Lucia" / … . Treating each box as a line made the OCR output unreadable and, downstream, made
/// translation translate each word on its own — which is how a paragraph turned into a column of
/// single words.
///
/// So the boxes are put back together here: fragments whose vertical extents overlap belong to the
/// same visual row, and a row is read left to right. Latin fragments are joined with a space; CJK
/// fragments are joined directly, because a space between two ideographs would be wrong.
/// </summary>
public static class OcrTextLayout
{
    /// <summary>How much two boxes must overlap vertically to count as the same visual row.</summary>
    private const double RowOverlapRatio = 0.5;

    /// <summary>
    /// Groups fragments into visual rows and joins each row into one line. Fragments that carry no
    /// geometry (or a single fragment) are returned untouched, so a detector that already reports
    /// whole lines is unaffected.
    /// </summary>
    public static IReadOnlyList<OcrLine> Assemble(IReadOnlyList<OcrLine> fragments)
    {
        if (fragments.Count <= 1) return fragments;
        foreach (var fragment in fragments)
        {
            if (fragment.Bounds.Width <= 0 || fragment.Bounds.Height <= 0) return fragments;
        }

        var rows = new List<List<OcrLine>>();
        foreach (var fragment in fragments)
        {
            List<OcrLine>? target = null;
            foreach (var row in rows)
            {
                if (SameRow(Union(row), fragment.Bounds)) { target = row; break; }
            }
            if (target is null) rows.Add([fragment]);
            else target.Add(fragment);
        }

        // Reading order: rows top to bottom, fragments within a row left to right.
        var ordered = rows
            .OrderBy(row => row.Min(f => f.Bounds.Y))
            .ThenBy(row => row.Min(f => f.Bounds.X))
            .Select(row => row.OrderBy(f => f.Bounds.X).ToList())
            .ToList();

        var result = new List<OcrLine>(ordered.Count);
        foreach (var row in ordered)
        {
            var text = new StringBuilder();
            foreach (var fragment in row)
            {
                if (text.Length > 0 && NeedsSpace(text[^1], fragment.Text)) text.Append(' ');
                text.Append(fragment.Text);
            }
            double score = row.Average(f => f.Score);
            result.Add(new OcrLine(text.ToString(), score, Union(row)));
        }
        return result;
    }

    private static bool SameRow(PixelRect row, PixelRect candidate)
    {
        int overlap = Math.Min(row.Bottom, candidate.Bottom) - Math.Max(row.Y, candidate.Y);
        int shorter = Math.Min(row.Height, candidate.Height);
        return shorter > 0 && overlap >= shorter * RowOverlapRatio;
    }

    private static PixelRect Union(List<OcrLine> row)
    {
        var bounds = row[0].Bounds;
        foreach (var fragment in row) bounds = bounds.Union(fragment.Bounds);
        return bounds;
    }

    /// <summary>True when a space belongs between two fragments that are being joined.</summary>
    private static bool NeedsSpace(char previous, string next)
    {
        if (next.Length == 0) return false;
        // CJK is written without word spaces; inserting one would corrupt the text.
        if (IsCjk(previous) || IsCjk(next[0])) return false;
        // Closing punctuation and trailing marks attach to what precedes them.
        if (IsAttaching(next[0])) return false;
        // A fragment that already ends in whitespace needs no extra separator.
        if (char.IsWhiteSpace(previous)) return false;
        return true;
    }

    private static bool IsAttaching(char c) =>
        c is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '%'
          or '\u3002' or '\uFF0C' or '\uFF01' or '\uFF1F' or '\uFF1B' or '\uFF1A'
          or '\uFF09' or '\u3011' or '\u300B' or '\u201D' or '\u2019';

    private static bool IsCjk(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'
          or >= '\u3400' and <= '\u4DBF'
          or >= '\uF900' and <= '\uFAFF'
          or >= '\u3040' and <= '\u30FF'   // Hiragana / Katakana
          or >= '\uAC00' and <= '\uD7AF';  // Hangul syllables
}
