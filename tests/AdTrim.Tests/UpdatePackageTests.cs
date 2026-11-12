using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class UpdatePackageTests : IDisposable
{
    private readonly RSA _key = RSA.Create(3072);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdTrim-package-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _installer = File.ReadAllBytes(typeof(UpdatePackageTests).Assembly.Location);
    private static string Version => System.Version.Parse(FileVersionInfo.GetVersionInfo(typeof(UpdatePackageTests).Assembly.Location).FileVersion!).ToString(3);
    private UpdatePackageManifest Manifest => new(1, Version, $"AdTrim-Setup-v{Version}.exe", _installer.Length, Convert.ToHexString(SHA256.HashData(_installer)));
    private static byte[] Bytes(UpdatePackageManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private byte[] Sign(byte[] bytes) => _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    private UpdatePackageManifest Verify(byte[] bytes, string? version = null, byte[]? signature = null)
        => UpdatePackageManifest.Verify(bytes, signature ?? Sign(bytes), _key.ExportSubjectPublicKeyInfoPem(), version ?? Version, DateTimeOffset.UtcNow);

    [Fact]
    public void InstallerIdentityIsBoundToTheSignedRelease()
    {
        var original = Bytes(Manifest);
        Assert.Equal(Manifest, Verify(original));
        Assert.Throws<CryptographicException>(() => Verify(Bytes(Manifest with { Sha256 = new string('a', 64) }), signature: Sign(original)));
        Assert.Throws<InvalidDataException>(() => Verify(original, "9.0.0"));
        foreach (var bad in new[] { Manifest with { InstallerName = "../../malware.exe" }, Manifest with { Schema = 2 },
            Manifest with { SizeBytes = 0 }, Manifest with { SizeBytes = UpdatePackageManifest.MaximumInstallerBytes + 1 },
            Manifest with { Sha256 = "missing" }, Manifest with { Version = "../1.0.0" } })
            Assert.ThrowsAny<Exception>(() => Verify(Bytes(bad)));
        foreach (var extra in new[] { ",\"url\":\"https://example.org/install.exe\"}", ",\"schema\":1}" })
            Assert.ThrowsAny<Exception>(() => Verify(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original)[..^1] + extra)));
    }

    [Fact]
    public async Task AVerifiedDownloadIsStagedWithItsSignedInformation()
    {
        using var handler = Handler();
        using var client = new HttpClient(handler);
        var reports = new List<UpdateDownloadProgress>();
        var service = new UpdatePackageService(_directory, _key.ExportSubjectPublicKeyInfoPem(), client);
        var update = await service.DownloadAsync(Version, "0.0.0", new InlineProgress(reports.Add), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, url => Assert.StartsWith($"https://github.com/adtrim/adtrim/releases/download/v{Version}/", url));
        Assert.Equal(_installer, await File.ReadAllBytesAsync(update.InstallerPath));
        Assert.Equal(Manifest, Verify(await File.ReadAllBytesAsync(Path.Combine(update.Directory, "update-package.json"))));
        // Each step is reported in order, so a stall or failure says where it happened.
        Assert.Equal(new[] { UpdateDownloadStep.GettingInformation, UpdateDownloadStep.VerifyingSignature, UpdateDownloadStep.Downloading,
            UpdateDownloadStep.CheckingDownload, UpdateDownloadStep.CheckingVersion, UpdateDownloadStep.Ready },
            reports.Select(r => r.Step).Distinct());
        var downloading = reports.Where(r => r.Step == UpdateDownloadStep.Downloading).Select(r => r.Fraction!.Value).ToList();
        Assert.Equal(1, downloading[^1]);
        Assert.All(downloading, value => Assert.InRange(value, 0, 1));
        Assert.All(reports.Where(r => r.Step != UpdateDownloadStep.Downloading), r => Assert.Null(r.Fraction));
        // Only a verified installer has its real name.
        Assert.Empty(Directory.GetFiles(update.Directory, "*" + UpdateStaging.PartialSuffix));
        UpdateStaging.RemoveDirectory(_directory, update.Directory);
    }

    [Fact]
    public async Task BadSignatureNeverDownloadsAnExecutable()
    {
        using var handler = Handler(signature: new byte[384]);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<CryptographicException>(() => Service(client).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(Directory.Exists(UpdateStaging.Root(_directory)));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("wrong-version")]
    [InlineData("cancelled")]
    public async Task FailedDownloadsCannotLeaveAnInstallerReadyToRun(string failure)
    {
        var payload = _installer.ToArray();
        if (failure == "corrupt") payload[^1] ^= 1;
        if (failure == "truncated") payload = payload[..^1];
        if (failure == "oversized") payload = payload.Concat(new byte[1]).ToArray();
        var manifest = failure == "wrong-version" ? Manifest with { Version = "9.0.0", InstallerName = "AdTrim-Setup-v9.0.0.exe" } : Manifest;
        using var handler = Handler(payload, manifest: manifest);
        using var client = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();
        if (failure == "cancelled") handler.BeforeInstaller = () => cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => Service(client).DownloadAsync(manifest.Version, "0.0.0", null, cancel.Token));
        Assert.Empty(Directory.GetDirectories(UpdateStaging.Root(_directory)));
    }

    [Fact]
    public async Task AReleaseWithoutPackageInformationOffersTheManualFallback()
    {
        using var handler = new PackageHandler(_ => new(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<UpdatePackageUnavailableException>(() => Service(client).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("http://github.com/installer.exe")]
    [InlineData("https://example.org/installer.exe")]
    [InlineData("https://github.com@evil.example/installer.exe")]
    public async Task RedirectsCannotMoveTheDownloadToUntrustedHosts(string target)
    {
        using var handler = new PackageHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(target); return response; });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RedirectLoopsAndOversizedMetadataAreBounded()
    {
        using var handler = new PackageHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://github.com/loop"); return r; });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(client).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
        Assert.Equal(6, handler.Requests.Count);
        using var big = new PackageHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[UpdateFeedReader.MaximumBytes + 1]) });
        using var bigClient = new HttpClient(big);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(bigClient).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
    }

    [Fact]
    public async Task SameOrOlderVersionsNeverContactTheNetwork()
    {
        using var handler = Handler();
        using var client = new HttpClient(handler);
        foreach (var installed in new[] { Version, "99.0.0" })
            await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).DownloadAsync(Version, installed, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void StartupCleanupRemovesEveryAbandonedDownloadButKeepsOwnedAndRecoverableFolders()
    {
        // Partial or unused downloads can be hundreds of megabytes; none may linger once nothing uses them.
        var partial = UpdateStaging.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(partial, "AdTrim-Setup-v9.9.9.exe" + UpdateStaging.PartialSuffix), "partial");
        var finished = UpdateStaging.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(finished, "AdTrim-Setup-v9.9.9.exe"), "unused");
        var resuming = UpdateStaging.CreateDirectory(_directory);
        var failedRestore = UpdateStaging.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(failedRestore, "backup"));
        var oldBackup = UpdateStaging.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(oldBackup, "backup"));
        Directory.SetCreationTimeUtc(oldBackup, DateTime.UtcNow.AddDays(-31));
        var other = Path.Combine(UpdateStaging.Root(_directory), "keep");
        Directory.CreateDirectory(other);
        var started = DateTime.UtcNow.AddSeconds(1);
        var thisSession = UpdateStaging.CreateDirectory(_directory);
        Directory.SetCreationTimeUtc(thisSession, started.AddSeconds(1));

        Assert.Equal(0, UpdateStaging.CleanUpAbandoned(_directory, started, keep: resuming));

        Assert.False(Directory.Exists(partial));
        Assert.False(Directory.Exists(finished));
        Assert.False(Directory.Exists(oldBackup));
        Assert.True(Directory.Exists(resuming));
        Assert.True(Directory.Exists(failedRestore));
        Assert.True(Directory.Exists(thisSession));
        Assert.True(Directory.Exists(other));
        Assert.Throws<IOException>(() => UpdateStaging.RemoveDirectory(_directory, _directory));
        Assert.Throws<IOException>(() => UpdateStaging.RemoveDirectory(_directory, Path.Combine(UpdateStaging.Root(_directory), "..", "outside")));
    }

    [Fact]
    public async Task ACleanupProblemDoesNotHideWhyADownloadFailed()
    {
        var corrupt = _installer.ToArray();
        corrupt[^1] ^= 1;
        using var handler = Handler(corrupt);
        using var client = new HttpClient(handler);
        FileStream? held = null;
        handler.BeforeInstaller = () =>
        {
            var job = Directory.GetDirectories(UpdateStaging.Root(_directory)).Single();
            held = new FileStream(Path.Combine(job, "update-package.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
            Assert.Contains("did not match", error.Message);
        }
        finally { held?.Dispose(); }
        Assert.Equal(0, UpdateStaging.CleanUpAbandoned(_directory, DateTime.UtcNow.AddMinutes(1)));
        Assert.Empty(Directory.GetDirectories(UpdateStaging.Root(_directory)));
    }

    private UpdatePackageService Service(HttpClient client) => new(_directory, _key.ExportSubjectPublicKeyInfoPem(), client);
    private PackageHandler Handler(byte[]? payload = null, byte[]? signature = null, UpdatePackageManifest? manifest = null)
    {
        var bytes = Bytes(manifest ?? Manifest);
        return new(uri => new(HttpStatusCode.OK) { Content = new ByteArrayContent(uri.AbsolutePath.EndsWith(".json") ? bytes
            : uri.AbsolutePath.EndsWith(".sig") ? signature ?? Sign(bytes) : payload ?? _installer) });
    }

    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    { public void Report(UpdateDownloadProgress value) => report(value); }
    private sealed class PackageHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Action? BeforeInstaller { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.EndsWith(".exe")) BeforeInstaller?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request.RequestUri));
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_directory))
        {
            foreach (var job in Directory.GetDirectories(UpdateStaging.Root(_directory)).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)))
                UpdateStaging.RemoveDirectory(_directory, job);
            Directory.Delete(_directory, true);
        }
    }

    // An update window that was ended mid-install leaves a backup and no completion record. The next
    // start must be able to tell that apart from every ordinary leftover.
    [Fact]
    public void AnInstallThatNeverFinishedIsFoundUntilItIsAcknowledgedOrSuperseded()
    {
        string Job(string version, bool backup, bool completed)
        {
            var folder = UpdateStaging.CreateDirectory(_directory);
            // The shape UpdateInstallation.WriteJob saves.
            File.WriteAllText(Path.Combine(folder, "job.json"),
                JsonSerializer.Serialize(new { Version = version, InstallDirectory = @"C:\Programs\AdTrim", ParentId = 1, ParentStartedTicks = 1L }));
            if (backup) Directory.CreateDirectory(Path.Combine(folder, "backup"));
            if (completed) File.WriteAllText(Path.Combine(folder, "completed.json"), "{}");
            return folder;
        }
        Job("9.0.0", backup: false, completed: false);
        Job("9.0.0", backup: true, completed: true);
        var unreadable = UpdateStaging.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(unreadable, "backup"));
        File.WriteAllText(Path.Combine(unreadable, "job.json"), "{");
        Assert.Null(UpdateStaging.FindInterruptedInstall(_directory, "2.0.0"));

        var interrupted = Job("9.0.0", backup: true, completed: false);
        Assert.Equal(new UpdateStaging.InterruptedInstall(interrupted, "9.0.0"), UpdateStaging.FindInterruptedInstall(_directory, "2.0.0"));
        // The app the update is restarting, and an installation already on that version, are not interrupted.
        Assert.Null(UpdateStaging.FindInterruptedInstall(_directory, "2.0.0", keep: interrupted));
        Assert.Null(UpdateStaging.FindInterruptedInstall(_directory, "9.0.0"));

        UpdateStaging.AcknowledgeInterruptedInstall(_directory, interrupted);
        Assert.Null(UpdateStaging.FindInterruptedInstall(_directory, "2.0.0"));
    }

    // A server that sends no length must still be cut off at the limit, not read to the end.
    [Fact]
    public async Task MetadataWithoutADeclaredLengthIsStillBounded()
    {
        using var handler = new PackageHandler(_ => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[UpdateFeedReader.MaximumBytes + 1]) });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(client).DownloadAsync(Version, "0.0.0", null, CancellationToken.None));
        Assert.False(Directory.Exists(UpdateStaging.Root(_directory)));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
