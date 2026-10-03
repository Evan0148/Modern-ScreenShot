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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip locked/ACL'd entry */ }
        }
        return [.. result.OrderByDescending(e => e.CreatedAt)];
    }

    /// <summary>Best-effort delete; returns false when the entry directory could not be removed.</summary>
    public bool Delete(HistoryEntry e)
    {
        if (!DeleteDirectory(e.Directory)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        // Fire Changed once at the end: HistoryWindow rebuilds its whole grid on every event, so
        // per-entry events turn "clear all" into an O(n²) refresh storm on large histories.
        bool any = false;
        foreach (var e in List()) any |= DeleteDirectory(e.Directory);
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool DeleteDirectory(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"History delete failed for '{directory}': {ex.Message}");
            return false;
        }
        return true;
    }

    public void Prune(int maxCount)
    {
        if (maxCount <= 0) return; // 0 = unlimited (deleting everything would be data loss)
        var all = List();
        bool any = false;
        foreach (var e in all.Skip(maxCount))
            any |= DeleteDirectory(e.Directory);
        // Same-thread sweep of crashed writes: dirs without meta.json can never be listed or
        // pruned again. Age gate keeps a just-created entry (files written after the directory)
        // out of harm's way.
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (File.Exists(Path.Combine(dir, "meta.json"))) continue;
            try
            {
                if (DateTime.UtcNow - System.IO.Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromMinutes(10))
                    any |= DeleteDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip */ }
        }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }
}
