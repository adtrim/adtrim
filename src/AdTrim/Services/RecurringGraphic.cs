namespace AdTrim.Services;

/// <summary>Signed edges recurring across unrelated pictures, learned from the recording.</summary>
public sealed class RecurringGraphic
{
    public const int Width = 320;
    public const int Height = 180;
    public const int FrameBytes = Width * Height;
    private readonly int[] _counts = new int[FrameBytes * 4];
    private int _samples;
    private (int Pixel, int Direction)[] _pattern = [];
    public int PatternPixels => _pattern.Length;

    public void Observe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes) throw new ArgumentException("Unexpected analysis frame size.");
        _samples++;
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
            {
                if (!InCorner(x, y)) continue;
                int pixel = y * Width + x;
                int gx = frame[pixel + 1] - frame[pixel - 1];
                int gy = frame[pixel + Width] - frame[pixel - Width];
                if (gx > 12) _counts[pixel * 4]++;
                if (gx < -12) _counts[pixel * 4 + 1]++;
                if (gy > 12) _counts[pixel * 4 + 2]++;
                if (gy < -12) _counts[pixel * 4 + 3]++;
            }
    }

    private static bool InCorner(int x, int y)
        => (x < 80 || x >= 240) && (y < 54 || y >= 126);

    public bool Complete()
    {
        if (_samples < 20) return false;
        var persistent = new bool[FrameBytes];
        var direction = new int[FrameBytes];
        for (int pixel = 0; pixel < FrameBytes; pixel++)
        {
            int best = 0;
            for (int d = 1; d < 4; d++)
                if (_counts[pixel * 4 + d] > _counts[pixel * 4 + best]) best = d;
            direction[pixel] = best;
            persistent[pixel] = _counts[pixel * 4 + best] > .45 * _samples;
        }
        double bestWeight = 0;
        for (int seed = 0; seed < FrameBytes; seed++)
        {
            if (!persistent[seed]) continue;
            var todo = new Stack<int>();
            var pixels = new List<int>();
            todo.Push(seed); persistent[seed] = false;
            int minX = Width, minY = Height, maxX = 0, maxY = 0;
            double weight = 0;
            while (todo.TryPop(out int pixel))
            {
                pixels.Add(pixel);
                int x = pixel % Width, y = pixel / Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                weight += Math.Pow(_counts[pixel * 4 + direction[pixel]] / (double)_samples, 2);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx <= 0 || xx >= Width - 1 || yy <= 0 || yy >= Height - 1) continue;
                        int next = yy * Width + xx;
                        if (!persistent[next]) continue;
                        persistent[next] = false; todo.Push(next);
                    }
            }
            if (pixels.Count < 5 || maxX - minX < 3 || maxY - minY < 3
                || maxX - minX > 64 || maxY - minY > 64 || weight <= bestWeight) continue;
            bestWeight = weight;
            _pattern = pixels.Select(p => (p, direction[p])).ToArray();
        }
        return PatternPixels > 0;
    }

    public double Match(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes) throw new ArgumentException("Unexpected analysis frame size.");
        if (PatternPixels == 0) return 0;
        int matched = 0;
        foreach (var (pixel, direction) in _pattern)
        {
            int response = direction switch
            {
                0 => frame[pixel + 1] - frame[pixel - 1],
                1 => frame[pixel - 1] - frame[pixel + 1],
                2 => frame[pixel + Width] - frame[pixel - Width],
                _ => frame[pixel - Width] - frame[pixel + Width],
            };
            if (response > 12) matched++;
        }
        return matched / (double)PatternPixels;
    }
}
