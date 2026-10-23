using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

/// <summary>Reads comments and stages metadata-only updates for self-contained MP4 files.</summary>
public static class Mp4Comments
{
    private const int MaxIndexBytes = 64 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Regex ExportLabel = new(@"^Exported with AdTrim \d+\.\d+(?:\.\d+)? \(\d{4}-\d{2}-\d{2}\)$", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyOutput = new(@"^Edited by AdTrim v?\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant);
    private sealed record Box(string Type, long Offset, long Size, int Header);
    private sealed record Index(List<Box> Boxes, Box Movie, byte[] Bytes);

    public static bool HasExportLabel(IEnumerable<string> comments) => comments
        .SelectMany(c => c.Split(';')).Any(c => ExportLabel.IsMatch(c.Trim()) || LegacyOutput.IsMatch(c.Trim()));

    public static string Merge(IEnumerable<string> comments, string version, DateOnly date)
    {
        var existing = comments.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal)
            .SelectMany(c => c.Split(';')).Where(c => !ExportLabel.IsMatch(c.Trim())).ToList();
        var text = string.Join(";", existing).TrimEnd();
        return (text.Length == 0 ? "" : text + (text.EndsWith(';') ? " " : "; "))
            + $"Exported with AdTrim {version} ({date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)})";
    }

    public static IReadOnlyList<string> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Comments(Load(stream).Bytes);
    }

    private static Index Load(FileStream stream)
    {
        var boxes = new List<Box>();
        long offset = 0;
        Span<byte> header = stackalloc byte[16];
        while (offset < stream.Length)
        {
            if (boxes.Count >= 100_000 || stream.Length - offset < 8) throw Invalid();
            stream.Position = offset;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header.Slice(4, 4));
            int length = 8;
            if (size == 1)
            {
                stream.ReadExactly(header[8..]);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]));
                length = 16;
            }
            if (size == 0) size = stream.Length - offset;
            if (size < length || size > stream.Length - offset) throw Invalid();
            boxes.Add(new(type, offset, size, length));
            offset += size;
        }
        if (!boxes.Any(b => b.Type == "ftyp") || boxes.Count(b => b.Type == "moov") != 1) throw Invalid();
        var movie = boxes.Single(b => b.Type == "moov");
        if (movie.Size > MaxIndexBytes) throw new NotSupportedException("The MP4 metadata index is too large to update safely.");
        var bytes = new byte[(int)movie.Size];
        stream.Position = movie.Offset;
        stream.ReadExactly(bytes);
        return new(boxes, movie, bytes);
    }

    private static List<byte[]> Children(byte[] bytes, int start)
    {
        var result = new List<byte[]>();
        for (int at = start; at < bytes.Length;)
        {
            if (bytes.Length - at < 8 || result.Count >= 100_000) throw Invalid();
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            int header = 8;
            if (size == 1)
            {
                if (bytes.Length - at < 16) throw Invalid();
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8)));
                header = 16;
            }
            if (size == 0) size = bytes.Length - at;
            if (size < header || size > bytes.Length - at) throw Invalid();
            result.Add(bytes.AsSpan(at, (int)size).ToArray());
            at += (int)size;
        }
        return result;
    }
    private static string Type(byte[] b) => Encoding.Latin1.GetString(b, 4, 4);
    private static int Header(byte[] b) => BinaryPrimitives.ReadUInt32BigEndian(b) == 1 ? 16 : 8;
    private static byte[] Make(string type, IEnumerable<byte[]> contents)
    {
        var parts = contents.ToArray();
        int length = checked(8 + parts.Sum(p => p.Length));
        if (length > MaxIndexBytes) throw new NotSupportedException("Updated MP4 metadata is too large.");
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)length);
        Encoding.Latin1.GetBytes(type).CopyTo(bytes, 4);
        int at = 8;
        foreach (var part in parts) { part.CopyTo(bytes, at); at += part.Length; }
        return bytes;
    }

    private static HashSet<string> CommentKeys(byte[] meta)
    {
        var keys = Children(meta, Header(meta) + 4).FirstOrDefault(b => Type(b) == "keys");
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (keys is null) return result;
        int h = Header(keys);
        if (keys.Length < h + 8) throw Invalid();
        var entries = Children(keys, h + 8);
        if (BinaryPrimitives.ReadUInt32BigEndian(keys.AsSpan(h + 4)) != entries.Count) throw Invalid();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (Type(entry) != "mdta") continue;
            var name = Utf8.GetString(entry, Header(entry), entry.Length - Header(entry));
            if (name is not ("comment" or "com.apple.quicktime.comment")) continue;
            var id = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(id, (uint)(i + 1));
            result.Add(Encoding.Latin1.GetString(id));
        }
        return result;
    }

    private static List<string> Comments(byte[] movie)
    {
        var comments = new List<string>();
        foreach (var parent in Children(movie, Header(movie)))
        {
            var metas = Type(parent) == "udta" ? Children(parent, Header(parent)).Where(b => Type(b) == "meta")
                : Type(parent) == "meta" ? new[] { parent } : Enumerable.Empty<byte[]>();
            foreach (var meta in metas)
            {
                int h = Header(meta);
                if (meta.Length < h + 4 || meta[h] != 0) throw Invalid();
                var keys = CommentKeys(meta);
                foreach (var list in Children(meta, h + 4).Where(b => Type(b) == "ilst"))
                    foreach (var entry in Children(list, Header(list)).Where(b => Type(b) == "©cmt" || keys.Contains(Type(b))))
                        foreach (var value in Children(entry, Header(entry)))
                        {
                            int v = Header(value);
                            if (Type(value) != "data" || value.Length < v + 8) throw Invalid();
                            uint encoding = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(v));
                            uint locale = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(v + 4));
                            if (locale != 0 || encoding is not (1 or 2))
                                throw new NotSupportedException("Localized or unfamiliar MP4 comments cannot be updated safely.");
                            comments.Add(encoding == 1 ? Utf8.GetString(value, v + 8, value.Length - v - 8)
                                : new UnicodeEncoding(true, false, true).GetString(value, v + 8, value.Length - v - 8));
                        }
            }
        }
        return comments;
    }

    private static byte[]? StripComments(byte[] meta)
    {
        var keys = CommentKeys(meta);
        var parts = Children(meta, Header(meta) + 4).Select(b => Type(b) == "ilst"
            ? Make("ilst", Children(b, Header(b)).Where(e => Type(e) != "©cmt" && !keys.Contains(Type(e)))) : b).ToList();
        // An empty standard metadata block carries no remaining user fields.
        if (parts.All(b => Type(b) == "hdlr" || Type(b) == "ilst" && b.Length == Header(b))) return null;
        return Make("meta", new[] { meta.AsSpan(Header(meta), 4).ToArray() }.Concat(parts));
    }

    private static byte[] UpdatedMovie(Index index, string comment)
    {
        if (index.Boxes.Any(b => b.Type is not ("ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "uuid")))
            throw new NotSupportedException("This MP4 layout cannot be updated safely.");
        var children = Children(index.Bytes, Header(index.Bytes));
        if (children.Any(b => Type(b) is "mvex" or "cmov" || Type(b) == "meta"))
            throw new NotSupportedException("Fragmented or movie-level metadata requires a different MP4 writer.");
        var data = Make("data", new[] { new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, Utf8.GetBytes(comment) });
        var handler = Convert.FromHexString("0000002168646C7200000000000000006D6469726170706C000000000000000000");
        var standard = Make("meta", new[] { new byte[4], handler, Make("ilst", new[] { Make("©cmt", new[] { data }) }) });
        var userData = children.Where(b => Type(b) == "udta").ToArray();
        if (userData.Length > 1) throw Invalid();
        var kept = new List<byte[]> { standard };
        if (userData.Length == 1)
            foreach (var child in Children(userData[0], Header(userData[0])))
            {
                if (Type(child) == "©cmt") throw new NotSupportedException("Legacy user-data comments cannot be migrated safely.");
                var updated = Type(child) == "meta" ? StripComments(child) : child;
                if (updated is not null) kept.Add(updated);
            }
        var udta = Make("udta", kept);
        var movie = Make("moov", children.Where(b => Type(b) != "udta").Append(udta));
        long delta = movie.Length - index.Movie.Size;
        return FixOffsets(movie, delta, index, 0);
    }

    private static byte[] FixOffsets(byte[] b, long delta, Index index, int depth)
    {
        if (depth > 12) throw Invalid();
        string type = Type(b);
        int h = Header(b);
        if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "dinf")
        {
            if (h != 8) throw new NotSupportedException("Extended MP4 track containers cannot be updated safely.");
            return Make(type, Children(b, h).Select(c => FixOffsets(c, delta, index, depth + 1)));
        }
        if (type is "saio" or "senc" or "iloc" or "mvex" or "cmov")
            throw new NotSupportedException("Encrypted or offset-bearing MP4 extensions cannot be updated safely.");
        if (type == "stsd")
        {
            if (b.Length < h + 8) throw Invalid();
            if (Children(b, h + 8).Any(e => Type(e) is "encv" or "enca"))
                throw new NotSupportedException("Encrypted MP4 tracks cannot be updated safely.");
        }
        if (type == "dref")
        {
            if (b.Length < h + 8) throw Invalid();
            foreach (var entry in Children(b, h + 8))
                if (Type(entry) != "url " || entry.Length != Header(entry) + 4
                    || BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(Header(entry))) != 1)
                    throw new NotSupportedException("External MP4 media references cannot be updated safely.");
        }
        if (type is not ("stco" or "co64")) return b;
        if (b.Length < h + 8) throw Invalid();
        uint count = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(h + 4));
        int width = type == "stco" ? 4 : 8;
        if ((long)count * width != b.Length - h - 8) throw Invalid();
        var output = b.ToArray();
        for (int at = h + 8; at < b.Length; at += width)
        {
            long offset = width == 4 ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at))
                : checked((long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at)));
            if (!index.Boxes.Any(box => box.Type == "mdat" && offset >= box.Offset + box.Header && offset < box.Offset + box.Size))
                throw new NotSupportedException("MP4 media offsets could not be verified.");
            long updated = offset >= index.Movie.Offset + index.Movie.Size ? checked(offset + delta) : offset;
            if (width == 4) BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(at), checked((uint)updated));
            else BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(at), checked((ulong)updated));
        }
        return output;
    }

    public static async Task StageAsync(string source, string staged, string version, DateOnly date, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var index = Load(input);
        var comment = Merge(Comments(index.Bytes), version, date);
        var movie = UpdatedMovie(index, comment);
        await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            foreach (var box in index.Boxes)
            {
                ct.ThrowIfCancellationRequested();
                if (box == index.Movie) { await output.WriteAsync(movie, ct); continue; }
                input.Position = box.Offset;
                await CopyExactlyAsync(input, output, box.Size, ct);
            }
            output.Flush(true);
        }
        // Validate the staged index and compare every untouched byte against the locked source.
        await using var check = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var stagedIndex = Load(check);
        if (!stagedIndex.Bytes.SequenceEqual(movie) || Comments(stagedIndex.Bytes) is not { Count: 1 } comments || comments[0] != comment)
            throw new IOException("The staged source comment could not be verified.");
        var before = index.Boxes.Where(b => b != index.Movie).ToArray();
        var after = stagedIndex.Boxes.Where(b => b != stagedIndex.Movie).ToArray();
        if (before.Length != after.Length) throw Invalid();
        byte[] a = new byte[1024 * 1024], bbuf = new byte[a.Length];
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i].Type != after[i].Type || before[i].Size != after[i].Size) throw Invalid();
            input.Position = before[i].Offset;
            check.Position = after[i].Offset;
            for (long left = before[i].Size; left > 0;)
            {
                int n = (int)Math.Min(left, a.Length);
                await input.ReadExactlyAsync(a.AsMemory(0, n), ct);
                await check.ReadExactlyAsync(bbuf.AsMemory(0, n), ct);
                if (!a.AsSpan(0, n).SequenceEqual(bbuf.AsSpan(0, n))) throw new IOException("Source media preservation check failed.");
                left -= n;
            }
        }
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long count, CancellationToken ct)
    {
        byte[] buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            int n = (int)Math.Min(count, buffer.Length);
            await input.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            count -= n;
        }
    }
    private static InvalidDataException Invalid() => new("The MP4 metadata is incomplete or unsupported; the source was not changed.");
}
