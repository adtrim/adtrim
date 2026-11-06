using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

public sealed record UpdatePackageManifest(int Schema, string Version, string InstallerName, long SizeBytes, string Sha256)
{
    public const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    [JsonIgnore] public Uri InstallerUri => ReleaseAsset(Version, InstallerName);
    public static Uri ReleaseAsset(string version, string name)
    {
        UpdateFeedReader.ParseVersion(version);
        return new Uri($"https://github.com/adtrim/adtrim/releases/download/v{version}/{name}");
    }

    public static UpdatePackageManifest Verify(byte[] bytes, byte[] signature, string publicKey, string version, DateTimeOffset now)
    {
        UpdateFeedReader.VerifySignature(bytes, signature, publicKey, now);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        UpdateFeedReader.RejectDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(bytes, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 8,
        }) ?? throw new InvalidDataException("The update package information is empty.");
        UpdateFeedReader.ParseVersion(manifest.Version);
        if (manifest.Schema != 1 || manifest.Version != version
            || manifest.InstallerName != $"AdTrim-Setup-v{version}.exe"
            || manifest.SizeBytes <= 0 || manifest.SizeBytes > MaximumInstallerBytes
            || manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "\\A[0-9a-fA-F]{64}\\z"))
            throw new InvalidDataException("The update package information is invalid.");
        return manifest;
    }

    public async Task VerifyInstallerAsync(string path, CancellationToken ct, IProgress<UpdateDownloadProgress>? progress = null)
    {
        progress?.Report(new(UpdateDownloadStep.CheckingDownload));
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
        {
            if (input.Length != SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer did not match its signed update information.");
        }
        progress?.Report(new(UpdateDownloadStep.CheckingVersion));
        var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (!System.Version.TryParse(fileVersion, out var actual) || actual != new System.Version(Version + ".0"))
            throw new InvalidDataException("The downloaded installer has the wrong version.");
    }
}

public sealed record DownloadedUpdate(string Directory, UpdatePackageManifest Manifest)
{
    public string InstallerPath => Path.Combine(Directory, Manifest.InstallerName);
}

/// <summary>The download step under way. <paramref name="Fraction"/> is set only while bytes are transferred.</summary>
public sealed record UpdateDownloadProgress(UpdateDownloadStep Step, double? Fraction = null);

public enum UpdateDownloadStep { GettingInformation, VerifyingSignature, Downloading, CheckingDownload, CheckingVersion, Ready }

public sealed class UpdatePackageUnavailableException() : IOException("This release requires a manual installation. Open What's new to download it.");

public sealed class UpdatePackageService : IDisposable
{
    private readonly string _directory;
    private readonly string _publicKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    public UpdatePackageService(string directory, string publicKey, HttpClient? http = null)
    {
        _directory = directory;
        _publicKey = publicKey;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    public async Task<DownloadedUpdate> DownloadAsync(string version, string installed, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        if (UpdateFeedReader.ParseVersion(version) <= UpdateFeedReader.ParseVersion(installed))
            throw new InvalidDataException("The update must be newer than this copy of AdTrim.");
        progress?.Report(new(UpdateDownloadStep.GettingInformation));
        var bytes = await MetadataAsync(UpdatePackageManifest.ReleaseAsset(version, "update-package.json"), UpdateFeedReader.MaximumBytes, ct);
        var signature = await MetadataAsync(UpdatePackageManifest.ReleaseAsset(version, "update-package.json.sig"), UpdateFeedReader.MaximumSignatureBytes, ct);
        progress?.Report(new(UpdateDownloadStep.VerifyingSignature));
        var manifest = UpdatePackageManifest.Verify(bytes, signature, _publicKey, version, DateTimeOffset.UtcNow);
        var directory = UpdateStaging.CreateDirectory(_directory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "update-package.json"), bytes, ct);
            await File.WriteAllBytesAsync(Path.Combine(directory, "update-package.json.sig"), signature, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            progress?.Report(new(UpdateDownloadStep.Downloading, 0));
            using var response = await GetAsync(manifest.InstallerUri, timeout.Token);
            if (response.Content.Headers.ContentLength is { } length && length != manifest.SizeBytes)
                throw new InvalidDataException("The update download has an unexpected size.");
            // Bytes arrive under a temporary name; only a verified installer gets its real name.
            string partial = Path.Combine(directory, manifest.InstallerName + UpdateStaging.PartialSuffix);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                var buffer = new byte[1024 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    received += count;
                    if (received > manifest.SizeBytes) throw new InvalidDataException("The update download is larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report(new(UpdateDownloadStep.Downloading, (double)received / manifest.SizeBytes));
                }
                await output.FlushAsync(timeout.Token);
                output.Flush(true);
            }
            await manifest.VerifyInstallerAsync(partial, ct, progress);
            File.Move(partial, Path.Combine(directory, manifest.InstallerName));
            progress?.Report(new(UpdateDownloadStep.Ready));
            return new(directory, manifest);
        }
        catch (Exception original)
        {
            // A cleanup problem must not hide why the download failed; startup cleanup retries it.
            try { UpdateStaging.RemoveDirectory(_directory, directory); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"[update] cleanup after '{original.Message}' failed: {cleanup.Message}"); }
            throw;
        }
    }

    private async Task<byte[]> MetadataAsync(Uri uri, int maximum, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetAsync(uri, timeout.Token);
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The update information is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("The update information is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !(uri.Host == "github.com"
                || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"))
                throw new InvalidDataException("The update download left GitHub's supported HTTPS hosts.");
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); throw new UpdatePackageUnavailableException(); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            response.Dispose();
            throw new HttpRequestException("The update could not be downloaded from GitHub.");
        }
        throw new HttpRequestException("The update download redirected too many times.");
    }
}
