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
        WriteAtomically(e.MetaPath, JsonSerializer.Serialize(e, JsonDefaults.Options));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveDocument(HistoryEntry e, AnnotationDocument doc) =>
        WriteAtomically(e.DocumentPath, UndoStack.Serialize(doc));

    /// <summary>tmp-file + rename so a crash mid-write can never leave a torn file behind.</summary>
    private static void WriteAtomically(string path, string content)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // History persistence is best-effort; a locked/full disk must not crash the capture flow.
            System.Diagnostics.Debug.WriteLine($"History write failed for '{path}': {ex.Message}");
        }
    }

    public AnnotationDocument? LoadDocument(HistoryEntry e)
    {
        if (!File.Exists(e.DocumentPath)) return null;
        try { return UndoStack.Deserialize(File.ReadAllText(e.DocumentPath)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
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

    /// <summary>Best-effort delete; returns false when the entry directory could not be removed.</summary>
    public bool Delete(HistoryEntry e)
    {
        try
        {
            if (System.IO.Directory.Exists(e.Directory)) System.IO.Directory.Delete(e.Directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"History delete failed for '{e.Directory}': {ex.Message}");
            return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        foreach (var e in List()) Delete(e);
    }

    public void Prune(int maxCount)
    {
        if (maxCount <= 0) return; // 0 = unlimited (deleting everything would be data loss)
        var all = List();
        foreach (var e in all.Skip(maxCount))
        {
            try { System.IO.Directory.Delete(e.Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use; retry next prune */ }
        }
        if (all.Count > maxCount) Changed?.Invoke(this, EventArgs.Empty);
    }
}
