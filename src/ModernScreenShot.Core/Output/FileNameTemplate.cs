using System.Text;
using System.Text.RegularExpressions;

namespace ModernScreenShot.Core.Output;

public static partial class FileNameTemplate
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"\{(?<name>[a-zA-Z]+)(?::(?<fmt>[^}]*))?\}")]
    private static partial Regex TokenRegex();

    public static string Format(string template, DateTime time, int counter, string? windowTitle, string? mode)
    {
        if (string.IsNullOrWhiteSpace(template)) template = "Screenshot_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";
        var result = TokenRegex().Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return name switch
            {
                "yyyy" => time.ToString("yyyy"),
                "yy" => time.ToString("yy"),
                "MM" => time.ToString("MM"),
                "dd" => time.ToString("dd"),
                "HH" => time.ToString("HH"),
                "mm" => time.ToString("mm"),
                "ss" => time.ToString("ss"),
                "fff" => time.ToString("fff"),
                "counter" => fmt is null ? counter.ToString() : counter.ToString(fmt.All(ch => ch == '0') ? fmt : "0"),
                "window" => string.IsNullOrWhiteSpace(windowTitle) ? "Window" : windowTitle!,
                "mode" => mode ?? "",
                _ => m.Value,
            };
        });
        return Sanitize(result);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 120) s = s[..120].TrimEnd('.', ' ');
        if (s.Length == 0) return "Screenshot";
        var stem = s.Split('.')[0];
        if (Reserved.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>extension with or without leading dot.</summary>
    public static string GetUniquePath(string directory, string baseName, string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var path = Path.Combine(directory, baseName + ext);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(directory, $"{baseName} ({n}){ext}");
        return path;
    }
}
