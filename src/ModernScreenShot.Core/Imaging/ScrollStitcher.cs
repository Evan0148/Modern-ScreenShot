namespace ModernScreenShot.Core.Imaging;

/// <summary>
/// Incrementally stitches vertically scrolled frames of the same region into one tall image.
/// Static header/footer rows (toolbars, fixed footers) are detected per frame pair and kept only once.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _maxHeight;
    private readonly List<byte[]> _rows = [];
    private PixelBuffer? _prev;
    private RowSig[]? _prevSig;
    private PixelBuffer? _cached;
    private int _width;

    public ScrollStitcher(int maxHeight = 20000) => _maxHeight = Math.Max(100, maxHeight);

    public int Height => _rows.Count;
    public bool ReachedMax => _rows.Count >= _maxHeight;
    public int FrameCount { get; private set; }
    /// <summary>Number of frames appended without a detected overlap (possible seams).</summary>
    public int UnmatchedFrames { get; private set; }

    public PixelBuffer? Result
    {
        get
        {
            if (_rows.Count == 0) return null;
            if (_cached is not null && _cached.Height == _rows.Count) return _cached;
            var buf = new PixelBuffer(_width, Math.Min(_rows.Count, _maxHeight));
            for (int y = 0; y < buf.Height; y++) _rows[y].CopyTo(buf.Row(y));
            return _cached = buf;
        }
    }

    private readonly record struct RowSig(int Hash, bool Uniform);

    /// <summary>Adds a frame. Returns false if the frame is identical to the previous one (no scroll happened).</summary>
    public bool AddFrame(PixelBuffer frame)
    {
        if (_prev is null)
        {
            _width = frame.Width;
            for (int y = 0; y < frame.Height; y++) _rows.Add(frame.Row(y).ToArray());
            _prev = frame.Clone();
            _prevSig = Signatures(frame);
            FrameCount = 1;
            return true;
        }
        if (frame.Width != _width || frame.Height != _prev.Height)
            throw new ArgumentException("All frames must have the same size.", nameof(frame));
        if (ReachedMax) return false;

        var sig = Signatures(frame);
        var prevSig = _prevSig!;
        int h = frame.Height;

        int header = 0;
        while (header < h && RowsEqual(_prev, frame, header, header)) header++;
        if (header >= h) return false; // identical frame

        int footer = 0;
        while (footer < h - header && RowsEqual(_prev, frame, h - 1 - footer, h - 1 - footer)) footer++;

        int zoneStart = header, zoneEnd = h - footer, zoneH = zoneEnd - zoneStart;
        int dy = FindOffset(prevSig, sig, zoneStart, zoneEnd);

        // Drop the previous footer, append new content, re-append footer.
        if (footer > 0 && _rows.Count >= footer) _rows.RemoveRange(_rows.Count - footer, footer);
        if (dy > 0)
        {
            for (int y = zoneEnd - dy; y < zoneEnd; y++) _rows.Add(frame.Row(y).ToArray());
        }
        else
        {
            UnmatchedFrames++;
            for (int y = zoneStart; y < zoneEnd; y++) _rows.Add(frame.Row(y).ToArray());
        }
        for (int y = zoneEnd; y < h; y++) _rows.Add(frame.Row(y).ToArray());

        if (_rows.Count > _maxHeight) _rows.RemoveRange(_maxHeight, _rows.Count - _maxHeight);
        _prev = frame.Clone();
        _prevSig = sig;
        _cached = null;
        FrameCount++;
        return dy > 0 || zoneH > 0;
    }

    /// <summary>
    /// Finds dy so that new rows [zoneStart, zoneEnd - dy) equal previous rows [zoneStart + dy, zoneEnd).
    /// Returns 0 if nothing plausible matched.
    /// </summary>
    private static int FindOffset(RowSig[] prev, RowSig[] cur, int zoneStart, int zoneEnd)
    {
        int zoneH = zoneEnd - zoneStart;
        const int minOverlap = 8;
        int bestDy = 0;
        double bestScore = 0;
        for (int dy = 1; dy <= zoneH - minOverlap; dy++)
        {
            int overlap = zoneH - dy, matched = 0, informative = 0, mismatched = 0;
            for (int i = 0; i < overlap; i++)
            {
                var a = prev[zoneStart + dy + i];
                var b = cur[zoneStart + i];
                if (a.Uniform && b.Uniform && a.Hash == b.Hash) continue;
                informative++;
                if (a.Hash == b.Hash) matched++;
                else if (++mismatched > overlap / 20 + 2) break;
            }
            if (informative < minOverlap || mismatched > overlap / 20 + 2) continue;
            double score = (double)matched / informative;
            // Prefer higher score; for equal scores prefer more informative overlap.
            double weighted = score + informative * 1e-7;
            if (score >= 0.95 && weighted > bestScore)
            {
                bestScore = weighted;
                bestDy = dy;
            }
        }
        return bestDy;
    }

    private static bool RowsEqual(PixelBuffer a, PixelBuffer b, int ya, int yb) =>
        a.Row(ya).SequenceEqual(b.Row(yb));

    private static RowSig[] Signatures(PixelBuffer f)
    {
        var sigs = new RowSig[f.Height];
        int samples = Math.Min(f.Width, 512);
        Parallel.For(0, f.Height, y =>
        {
            var row = f.Row(y);
            var hc = new HashCode();
            int first = -1;
            bool uniform = true;
            for (int s = 0; s < samples; s++)
            {
                int x = (int)((long)s * (f.Width - 1) / Math.Max(1, samples - 1));
                int i = x * 4;
                int lum = (row[i] * 29 + row[i + 1] * 150 + row[i + 2] * 77) >> 10; // 0..63, tolerant to tiny noise
                hc.Add(lum);
                if (first < 0) first = lum;
                else if (lum != first) uniform = false;
            }
            sigs[y] = new RowSig(hc.ToHashCode(), uniform);
        });
        return sigs;
    }
}
