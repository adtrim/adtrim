using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AdTrim.Services;

/// <summary>
/// Names a recording's cache folders. The frame cache this class once held is gone; the thumbnail
/// and audio-level caches still key their folders with its hash.
/// </summary>
public static class FrameCacheService
{
    /// <summary>
    /// Hash a source's identity into a stable directory key. SHA-256 over
    /// `{absolutePath}|{size}|{mtimeUnixMs}` - content-correlated enough that
    /// a re-encode invalidates the cache automatically.
    /// </summary>
    public static string ComputeSourceHash(string sourcePath)
    {
        var fi = new FileInfo(sourcePath);
        return ComputeSourceHash(sourcePath, new(fi.Length,
            new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(), 0));
    }

    internal static string ComputeSourceHash(string sourcePath, AdTrim.Models.SourceFingerprint fingerprint)
    {
        var key = $"{Path.GetFullPath(sourcePath).ToLowerInvariant()}|{fingerprint.SizeBytes}|{fingerprint.ModifiedUnixMs}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
