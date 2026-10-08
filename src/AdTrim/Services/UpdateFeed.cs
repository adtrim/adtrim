using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

public sealed record UpdateAdvisory(string Id, string AffectedFrom, string FixedIn, string Importance, string Summary);
public sealed record UpdateFeed(int Schema, long Revision, DateTimeOffset PublishedAt, DateTimeOffset? ExpiresAt,
    string LatestVersion, string Summary, UpdateAdvisory[] Advisories);
public sealed record UpdateNotice(string Version, string Importance, string Summary, string[] AdvisoryIds)
{
    public string Title => Importance switch
    {
        "urgent" => "Important Security Update",
        "security" => "Security Update",
        _ => $"AdTrim v{Version} is available"
    };
    public string Label => Importance == "regular" ? $"Update available: v{Version}" : $"{Title}: v{Version}";
    public Uri ReleaseUri => new($"https://github.com/adtrim/adtrim/releases/tag/v{UpdateFeedReader.ParseVersion(Version)}");
}

public static class UpdateFeedReader
{
    public const int MaximumBytes = 64 * 1024;
    public const int MaximumSignatureBytes = 8192;
    public sealed record SigningAuthorization(string PublicKey, DateTimeOffset ExpiresAt);
    public sealed record RotatedSignature(string Signature, string Authorization, string AuthorizationSignature);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static Version ParseVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z"))
            throw new InvalidDataException("Invalid update version.");
        return Version.Parse(value);
    }

    public static UpdateFeed Verify(byte[] bytes, byte[] signature, string publicKey, DateTimeOffset now, long minimumRevision)
    {
        if (bytes.Length > MaximumBytes || signature.Length > MaximumSignatureBytes)
            throw new InvalidDataException("Invalid update information size.");
        using var key = RSA.Create();
        key.ImportFromPem(publicKey);
        if (key.KeySize != 3072) throw new CryptographicException("Invalid trusted key.");
        // The embedded root authorizes a replacement signing key without trusting the host.
        if (signature.Length != 384)
        {
            using var signatureDocument = JsonDocument.Parse(signature, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateProperties(signatureDocument.RootElement);
            var envelope = JsonSerializer.Deserialize<RotatedSignature>(signature, Options)
                ?? throw new InvalidDataException("Invalid rotated signature.");
            var authorization = Convert.FromBase64String(envelope.Authorization);
            var authorizationSignature = Convert.FromBase64String(envelope.AuthorizationSignature);
            var signedAuthorization = System.Text.Encoding.UTF8.GetBytes("AdTrim update signing key v1\n").Concat(authorization).ToArray();
            if (!key.VerifyData(signedAuthorization, authorizationSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("Unauthorized update signing key.");
            using var authorizationDocument = JsonDocument.Parse(authorization, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateProperties(authorizationDocument.RootElement);
            var certificate = JsonSerializer.Deserialize<SigningAuthorization>(authorization, Options)
                ?? throw new InvalidDataException("Invalid signing authorization.");
            if (certificate.ExpiresAt <= now) throw new InvalidDataException("Signing authorization expired.");
            key.ImportFromPem(certificate.PublicKey);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        if (key.KeySize != 3072 || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Update information signature is invalid.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicateProperties(document.RootElement);
        var feed = JsonSerializer.Deserialize<UpdateFeed>(bytes, Options) ?? throw new InvalidDataException("Empty update information.");
        if (feed.Schema != 1 || feed.Revision < Math.Max(1, minimumRevision)
            || feed.PublishedAt == default || feed.PublishedAt > now.AddMinutes(5))
            throw new InvalidDataException("Update information is outdated or unsupported.");
        var latest = ParseVersion(feed.LatestVersion);
        ValidateText(feed.Summary, 1200);
        if (feed.Advisories is null || feed.Advisories.Length > 64) throw new InvalidDataException("Invalid advisories.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var advisory in feed.Advisories)
        {
            if (advisory is null || advisory.Id is null || !Regex.IsMatch(advisory.Id, @"\A[a-zA-Z0-9-]{1,64}\z")
                || !ids.Add(advisory.Id) || advisory.Importance is not ("security" or "urgent"))
                throw new InvalidDataException("Invalid security advisory.");
            var start = ParseVersion(advisory.AffectedFrom);
            var end = ParseVersion(advisory.FixedIn);
            if (start >= end || end > latest) throw new InvalidDataException("Invalid affected versions.");
            ValidateText(advisory.Summary, 600);
        }
        return feed;
    }

    public static UpdateNotice? Select(UpdateFeed feed, string installed)
    {
        var version = ParseVersion(installed);
        if (version >= ParseVersion(feed.LatestVersion)) return null;
        var affected = feed.Advisories.Where(a => version >= ParseVersion(a.AffectedFrom) && version < ParseVersion(a.FixedIn)).ToArray();
        var importance = affected.Any(a => a.Importance == "urgent") ? "urgent" : affected.Length > 0 ? "security" : "regular";
        return new(feed.LatestVersion, importance,
            affected.Length > 0 ? string.Join("\n\n", affected.Select(a => a.Summary)) : feed.Summary,
            affected.Select(a => a.Id).ToArray());
    }

    private static void ValidateText(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r'))
            || value.Any(c => c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
            throw new InvalidDataException("Invalid announcement text.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate update property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

public sealed record UpdateAcknowledgement(DateTimeOffset SeenAt, string Importance);
public sealed class UpdateHistory
{
    public Dictionary<string, UpdateAcknowledgement> Seen { get; set; } = new();

    private static IEnumerable<string> Keys(UpdateNotice notice) => notice.AdvisoryIds.Length == 0
        ? new[] { "release:" + notice.Version } : notice.AdvisoryIds.Select(id => "advisory:" + id);

    public bool IsDue(UpdateNotice notice, DateTimeOffset now) => Keys(notice).Any(key =>
        !Seen.TryGetValue(key, out var entry)
        || notice.Importance == "urgent" && entry.Importance != "urgent"
        || notice.Importance != "regular" && now - entry.SeenAt >= TimeSpan.FromDays(notice.Importance == "urgent" ? 1 : 7));

    public void Acknowledge(UpdateNotice notice, DateTimeOffset now)
    {
        foreach (var key in Keys(notice)) Seen[key] = new(now, notice.Importance);
        foreach (var key in Seen.OrderByDescending(p => p.Value.SeenAt).Skip(128).Select(p => p.Key).ToArray()) Seen.Remove(key);
    }
}
