using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

[SupportedOSPlatform("windows")]
public sealed class SourceCommentIntegrationTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMediaKeepsStreamsChaptersPayloadsAndMetadataWhileWindowsAndFfprobeReadOneComment(bool alternative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AdTrim.sln"))) root = root.Parent;
        Assert.NotNull(root);
        Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(root.FullName, "binaries/ffmpeg/win-x64"));
        var runner = AudioLevelServiceTests.Runner();
        string directory = Path.Combine(Path.GetTempPath(), "adtrim-comment-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.mp4"), stage = Path.Combine(directory, "staged.mp4");
            string chapters = Path.Combine(directory, "chapters.txt"), subtitles = Path.Combine(directory, "subtitles.srt");
            await File.WriteAllTextAsync(chapters, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Original chapter\n");
            await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:00,900\nOriginal subtitle\n");
            var args = new List<string> { "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1", "-i", subtitles, "-i", chapters,
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map_metadata", "4", "-map_chapters", "4",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "mov_text",
                "-metadata", "comment=Original user comment", "-metadata", "title=Original title",
                "-metadata", "custom_preserve=Original custom value", "-movflags", alternative ? "+faststart+use_metadata_tags" : "+faststart", source };
            var generation = await runner.RunFfmpegAsync(args);
            Assert.True(generation.Success, generation.Stderr);
            // Opaque metadata and an existing XMP packet must survive without interpretation.
            byte[] opaque = Encoding.ASCII.GetBytes("opaque-metadata-preserved");
            using (var file = File.Open(source, FileMode.Append))
            {
                byte[] header = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + 16 + opaque.Length));
                Encoding.ASCII.GetBytes("uuid").CopyTo(header, 4);
                file.Write(header); file.Write(new byte[16]); file.Write(opaque);
                byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><note>Original rights</note></x:xmpmeta>");
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(24 + xmp.Length));
                file.Write(header); file.Write(Convert.FromHexString("BE7ACFCB97A942E89C71999491E3AFAC")); file.Write(xmp);
            }
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            async Task<JsonObject> Probe(string path)
            {
                var r = await runner.RunFfprobeAsync(new[] { "-v", "error", "-show_format", "-show_streams", "-show_chapters",
                    "-show_packets", "-show_data_hash", "sha256", "-of", "json", path });
                Assert.True(r.Success, r.Stderr);
                var json = JsonNode.Parse(r.Stdout)!.AsObject();
                foreach (var p in json["packets"]!.AsArray()) p!.AsObject().Remove("pos");
                return json;
            }
            var before = await Probe(source);
            await Mp4Comments.StageAsync(source, stage, "2.0.1", new(2026, 10, 22), default);
            const string expected = "Original user comment; Exported with AdTrim 2.0.1 (2026-10-22)";
            var after = await Probe(stage);
            Assert.Equal(expected, after["format"]!["tags"]!["comment"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(before["streams"], after["streams"]));
            Assert.True(JsonNode.DeepEquals(before["chapters"], after["chapters"]));
            Assert.True(JsonNode.DeepEquals(before["packets"], after["packets"]));
            foreach (var tag in before["format"]!["tags"]!.AsObject().Where(p => p.Key != "comment"))
                Assert.True(JsonNode.DeepEquals(tag.Value, after["format"]!["tags"]![tag.Key]), tag.Key);
            Assert.Equal(expected, WindowsComment(stage));
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var decode = await runner.RunFfmpegAsync(new[] { "-v", "error", "-i", stage, "-map", "0:v", "-map", "0:a", "-f", "null", "-" });
            Assert.True(decode.Success, decode.Stderr);
            string repeated = Path.Combine(directory, "repeated.mp4");
            await Mp4Comments.StageAsync(stage, repeated, "2.0.1", new(2026, 10, 22), default);
            Assert.Equal(await File.ReadAllBytesAsync(stage), await File.ReadAllBytesAsync(repeated));
        }
        finally { Directory.Delete(directory, true); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Key { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Value { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface PropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out Key key);
        void GetValue(ref Key key, out Value value); void SetValue(ref Key key, ref Value value); void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bind, uint flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out PropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Value value);
    private static string? WindowsComment(string path)
    {
        var iid = typeof(PropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store);
        try
        {
            var key = new Key { Format = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), Id = 6 };
            store.GetValue(ref key, out var value);
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Text) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
