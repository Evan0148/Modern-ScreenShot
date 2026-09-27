using System.Text.Json;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.Core.History;

public sealed class HistoryEntry
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? WindowTitle { get; set; }
    public string? Mode { get; set; }
    /// <summary>Last exported file path, if any.</summary>
    public string? SavedPath { get; set; }

    public string Directory { get; set; } = "";
    public string OriginalPath => Path.Combine(Directory, "original.png");
    public string DocumentPath => Path.Combine(Directory, "doc.json");
    public string ThumbnailPath => Path.Combine(Directory, "thumb.png");
    public string MetaPath => Path.Combine(Directory, "meta.json");
}

/// <summary>
/// File layout: {root}/{id}/original.png, doc.json, thumb.png, meta.json.
/// Image encoding is done by the caller (App layer) through the provided delegates so Core stays UI-free.
/// </summary>
public sealed class HistoryStore
{
    public string Root { get; }
    public event EventHandler? Changed;

    public HistoryStore(string? root = null) => Root = root ?? AppPaths.HistoryDir;

    public HistoryEntry Create(int width, int height, string? windowTitle, string? mode)
    {
        var now = DateTime.Now;
        var id = $"{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
        var entry = new HistoryEntry
        {
            Id = id, CreatedAt = now, Width = width, Height = height, WindowTitle = windowTitle, Mode = mode,
            Directory = Path.Combine(Root, id),
        };
        System.IO.Directory.CreateDirectory(entry.Directory);
        return entry;
    }

    public void SaveMeta(HistoryEntry e)
    {
        File.WriteAllText(e.MetaPath, JsonSerializer.Serialize(e, JsonDefaults.Options));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveDocument(HistoryEntry e, AnnotationDocument doc) =>
        File.WriteAllText(e.DocumentPath, UndoStack.Serialize(doc));

    public AnnotationDocument? LoadDocument(HistoryEntry e)
    {
        if (!File.Exists(e.DocumentPath)) return null;
        try { return UndoStack.Deserialize(File.ReadAllText(e.DocumentPath)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Newest first.</summary>
    public List<HistoryEntry> List()
    {
        var result = new List<HistoryEntry>();
        if (!System.IO.Directory.Exists(Root)) return result;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta) || !File.Exists(Path.Combine(dir, "original.png"))) continue;
            try
            {
                var e = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(meta), JsonDefaults.Options);
                if (e is null) continue;
                e.Directory = dir;
                result.Add(e);
            }
            catch (JsonException) { /* skip corrupt entry */ }
            catch (IOException) { /* skip locked entry */ }
        }
        return [.. result.OrderByDescending(e => e.CreatedAt)];
    }

    public void Delete(HistoryEntry e)
    {
        if (System.IO.Directory.Exists(e.Directory)) System.IO.Directory.Delete(e.Directory, true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        foreach (var e in List()) Delete(e);
    }

    public void Prune(int maxCount)
    {
        var all = List();
        foreach (var e in all.Skip(Math.Max(0, maxCount)))
        {
            try { System.IO.Directory.Delete(e.Directory, true); }
            catch (IOException) { /* in use; retry next prune */ }
        }
        if (all.Count > maxCount) Changed?.Invoke(this, EventArgs.Empty);
    }
}
