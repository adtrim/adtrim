namespace AdTrim.Services;

/// <summary>
/// Learns what this recording's program looks and sounds like, without a logo or
/// any outside definitions, and scores each second for how unlike the program it is.
/// Program is assumed to be the long, self-similar majority; advertisements are short,
/// fast-cut and unlike the rest of the recording. Confident seconds seed a small model
/// (appearance axes, cut rate, typicality, center-channel occupancy) that is refit for a
/// few rounds. Returns null when the recording gives too little to learn from.
/// </summary>
public static class ShowProfile
{
    public const int Width = 80;
    private const int Neighbors = 10, Exclude = 60, MaxReferences = 4_000, Rounds = 3, Axes = 12, Window = 31;

    /// <summary>Appends one compact descriptor for a 320x180 YUV 4:4:4 frame: Y, Cb and Cr
    /// 4x4 block means, then Y (16), Cb (8) and Cr (8) histograms scaled to 0-255.</summary>
    public static void Describe(ReadOnlySpan<byte> frame, List<byte> output)
    {
        const int w = RecurringGraphic.Width, h = RecurringGraphic.Height, plane = w * h;
        if (frame.Length != 3 * plane) throw new ArgumentException("Unexpected analysis frame size.");
        for (int p = 0; p < 3; p++)
        {
            var pixels = frame.Slice(p * plane, plane);
            for (int by = 0; by < 4; by++)
                for (int bx = 0; bx < 4; bx++)
                {
                    long sum = 0;
                    for (int y = by * 44; y < by * 44 + 44; y++)
                        for (int x = bx * 80; x < bx * 80 + 80; x++) sum += pixels[y * w + x];
                    output.Add((byte)Math.Round(sum / (44.0 * 80)));
                }
        }
        foreach (var (p, bins) in new[] { (0, 16), (1, 8), (2, 8) })
        {
            var counts = new int[bins];
            foreach (byte value in frame.Slice(p * plane, plane)) counts[value * bins / 256]++;
            foreach (int count in counts) output.Add((byte)Math.Round(255.0 * count / plane));
        }
    }

    /// <summary>Per-second probability that each second is not program, or null.</summary>
    public static double[]? Score(SuggestionEvidence evidence, long durationUs)
    {
        int n = Math.Min(evidence.Appearance.Length / Width, (int)(durationUs / 1_000_000) + 1);
        if (n < 600) return null;
        var z = Standardize(n, evidence.Appearance);
        var typicality = Smooth(Typicality(z), Window);
        var cuts = new double[n];
        foreach (var (time, score) in evidence.Scenes)
            if (score > .4 && time >= 0 && time / 1_000_000 < n) cuts[time / 1_000_000]++;
        // Strong cuts per 31 s, then averaged over 31 s so a single busy shot does not dominate.
        var rate = Smooth(cuts, Window);
        for (int i = 0; i < n; i++) rate[i] *= Window;
        rate = Smooth(rate, Window);
        var columns = new List<double[]>(Project(z, Axes)) { rate, typicality };
        if (evidence.CentreDialogue.Count > 0)
            columns.Add(Smooth(Enumerable.Range(0, n).Select(i => i < evidence.CentreDialogue.Count && evidence.CentreDialogue[i] ? 1.0 : 0).ToArray(), Window));
        var x = Columns(columns, n);

        var seed = Add(Rank(rate), Rank(typicality));
        double median = Quantile(seed, .5), high = Quantile(seed, .85);
        var program = Runs(seed.Select(v => v <= median).ToArray(), 60);
        var ad = Runs(seed.Select(v => v >= high).ToArray(), 20);
        double[] raw = [];
        for (int round = 0; round < Rounds; round++)
        {
            if (program.Count(v => v) < 120 || ad.Count(v => v) < 40) return null;
            var (weights, bias) = FitLogistic(x, program, ad);
            raw = x.Select(row => Sigmoid(Dot(row, weights) + bias)).ToArray();
            var p = Smooth(raw, 15);
            program = Runs(p.Select(v => v < .2).ToArray(), 60);
            ad = Runs(p.Select(v => v > .8).ToArray(), 20);
        }
        return raw;
    }

    private static double[][] Standardize(int n, byte[] appearance)
    {
        var z = new double[n][];
        var mean = new double[Width]; var sd = new double[Width];
        for (int i = 0; i < n; i++) for (int j = 0; j < Width; j++) mean[j] += appearance[i * Width + j];
        for (int j = 0; j < Width; j++) mean[j] /= n;
        for (int i = 0; i < n; i++) for (int j = 0; j < Width; j++) sd[j] += Math.Pow(appearance[i * Width + j] - mean[j], 2);
        for (int j = 0; j < Width; j++) sd[j] = Math.Sqrt(sd[j] / n);
        for (int i = 0; i < n; i++)
        {
            z[i] = new double[Width];
            for (int j = 0; j < Width; j++) z[i][j] = sd[j] > 1e-6 ? (appearance[i * Width + j] - mean[j]) / sd[j] : 0;
        }
        return z;
    }

    /// <summary>Mean distance to the nearest seconds elsewhere in the recording (outside +/-1 min).
    /// Long recordings compare against evenly spaced reference seconds to stay bounded.</summary>
    private static double[] Typicality(double[][] z)
    {
        int n = z.Length, step = Math.Max(1, (n + MaxReferences - 1) / MaxReferences);
        var result = new double[n];
        var nearest = new double[Neighbors];
        for (int i = 0; i < n; i++)
        {
            Array.Fill(nearest, double.PositiveInfinity);
            for (int r = 0; r < n; r += step)
            {
                if (Math.Abs(r - i) <= Exclude) continue;
                double d = 0;
                for (int j = 0; j < Width; j++) { double v = z[i][j] - z[r][j]; d += v * v; }
                if (d >= nearest[Neighbors - 1]) continue;
                int k = Neighbors - 1;
                while (k > 0 && nearest[k - 1] > d) { nearest[k] = nearest[k - 1]; k--; }
                nearest[k] = d;
            }
            double total = 0; int count = 0;
            foreach (double d in nearest) if (double.IsFinite(d)) { total += Math.Sqrt(d); count++; }
            result[i] = count > 0 ? total / count : 0;
        }
        return result;
    }

    /// <summary>Scores on the recording's own main appearance axes (principal components).</summary>
    private static IEnumerable<double[]> Project(double[][] z, int axes)
    {
        int n = z.Length;
        var cov = new double[Width, Width];
        foreach (var row in z)
            for (int a = 0; a < Width; a++)
                for (int b = a; b < Width; b++) cov[a, b] += row[a] * row[b];
        for (int a = 0; a < Width; a++) for (int b = a; b < Width; b++) cov[b, a] = cov[a, b] /= n;
        var (values, vectors) = Jacobi(cov);
        foreach (int axis in Enumerable.Range(0, Width).OrderByDescending(i => values[i]).Take(axes))
            yield return z.Select(row => { double s = 0; for (int j = 0; j < Width; j++) s += row[j] * vectors[j, axis]; return s; }).ToArray();
    }

    private static (double[] Values, double[,] Vectors) Jacobi(double[,] input)
    {
        int m = input.GetLength(0);
        var a = (double[,])input.Clone();
        var v = new double[m, m];
        for (int i = 0; i < m; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = 0;
            for (int p = 0; p < m; p++) for (int q = p + 1; q < m; q++) off += a[p, q] * a[p, q];
            if (off < 1e-12) break;
            for (int p = 0; p < m; p++)
                for (int q = p + 1; q < m; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-15) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < m; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < m; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < m; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        return (Enumerable.Range(0, m).Select(i => a[i, i]).ToArray(), v);
    }

    private static double[][] Columns(List<double[]> columns, int n)
    {
        foreach (var column in columns)
        {
            double mean = column.Average(), sd = Math.Sqrt(column.Select(v => (v - mean) * (v - mean)).Average());
            for (int i = 0; i < n; i++) column[i] = sd > 1e-6 ? (column[i] - mean) / sd : 0;
        }
        return Enumerable.Range(0, n).Select(i => columns.Select(c => c[i]).ToArray()).ToArray();
    }

    /// <summary>Class-balanced, lightly regularized logistic regression on the seed seconds.</summary>
    private static (double[] Weights, double Bias) FitLogistic(double[][] x, bool[] program, bool[] ad)
    {
        var rows = Enumerable.Range(0, x.Length).Where(i => program[i] || ad[i]).ToArray();
        int positives = rows.Count(i => ad[i]);
        double pos = .5 / Math.Max(positives / (double)rows.Length, 1e-3), neg = .5 / Math.Max(1 - positives / (double)rows.Length, 1e-3);
        int width = x[0].Length;
        var w = new double[width]; double bias = 0;
        var gradient = new double[width];
        for (int step = 0; step < 400; step++)
        {
            Array.Clear(gradient); double gb = 0;
            foreach (int i in rows)
            {
                double y = ad[i] ? 1 : 0;
                double g = (Sigmoid(Dot(x[i], w) + bias) - y) * (ad[i] ? pos : neg);
                for (int j = 0; j < width; j++) gradient[j] += g * x[i][j];
                gb += g;
            }
            for (int j = 0; j < width; j++) w[j] -= .5 * (gradient[j] / rows.Length + .01 * w[j]);
            bias -= .5 * gb / rows.Length;
        }
        return (w, bias);
    }

    internal static double[] Smooth(double[] values, int width)
    {
        var result = new double[values.Length];
        int half = width / 2;
        double sum = 0;
        for (int i = 0; i < Math.Min(half, values.Length); i++) sum += values[i];
        for (int i = 0; i < values.Length; i++)
        {
            if (i + half < values.Length) sum += values[i + half];
            if (i - half - 1 >= 0) sum -= values[i - half - 1];
            result[i] = sum / width;
        }
        return result;
    }

    internal static bool[] Runs(bool[] mask, int minimum)
    {
        var result = new bool[mask.Length];
        for (int i = 0; i < mask.Length;)
        {
            if (!mask[i]) { i++; continue; }
            int j = i;
            while (j < mask.Length && mask[j]) j++;
            if (j - i >= minimum) Array.Fill(result, true, i, j - i);
            i = j;
        }
        return result;
    }

    private static double[] Rank(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var result = new double[values.Length];
        for (int r = 0; r < order.Length; r++) result[order[r]] = r / (double)Math.Max(1, values.Length - 1);
        return result;
    }

    private static double Quantile(double[] values, double q)
    {
        var sorted = values.Order().ToArray();
        double position = q * (sorted.Length - 1);
        int low = (int)Math.Floor(position);
        return low + 1 < sorted.Length ? sorted[low] + (position - low) * (sorted[low + 1] - sorted[low]) : sorted[low];
    }

    private static double[] Add(double[] a, double[] b) => a.Zip(b, (x, y) => x + y).ToArray();
    private static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }
    private static double Sigmoid(double v) => 1 / (1 + Math.Exp(-v));
}
