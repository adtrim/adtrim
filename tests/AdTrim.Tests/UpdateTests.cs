using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly RSA _key = RSA.Create(3072);
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "AdTrim-update-tests-" + Guid.NewGuid());
    // Service tests run as an older installed version, independent of the app's own version.
    private const string Installed = "1.1.0";
    private UpdateFeed Feed => new(1, 2, _now.AddMinutes(-1), _now.AddDays(30), "1.2.1", "Editing improvements.",
        [new("video-decoder", "1.0.0", "1.2.0", "security", "Fixes a video decoder vulnerability.")]);
    private static byte[] Bytes(UpdateFeed feed) => JsonSerializer.SerializeToUtf8Bytes(feed, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private byte[] Sign(byte[] bytes) => _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    private UpdateFeed Verify(byte[] bytes, byte[]? signature = null, long minimum = 0) => UpdateFeedReader.Verify(bytes, signature ?? Sign(bytes), _key.ExportSubjectPublicKeyInfoPem(), _now, minimum);

    [Fact]
    public void TamperedAnnouncementsCannotBorrowAValidSignature()
    {
        var original = Bytes(Feed);
        var changed = Bytes(Feed with { Summary = "Install this now" });
        Assert.Throws<CryptographicException>(() => Verify(changed, Sign(original)));
        using var other = RSA.Create(3072);
        Assert.Throws<CryptographicException>(() => UpdateFeedReader.Verify(original, Sign(original), other.ExportSubjectPublicKeyInfoPem(), _now, 0));
    }

    [Fact]
    public void ReplayAndFutureDatingAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed), minimum: 3));
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed with { PublishedAt = default })));
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed with { PublishedAt = _now.AddHours(1) })));
    }

    [Fact]
    public void SignedAnnouncementsRemainUsableAfterYearsWithoutMaintenance()
    {
        var bytes = Bytes(Feed);
        var verified = UpdateFeedReader.Verify(bytes, Sign(bytes), _key.ExportSubjectPublicKeyInfoPem(), _now.AddYears(20), 2);
        Assert.Equal("security", UpdateFeedReader.Select(verified, "1.1.0")!.Importance);
        Assert.Throws<InvalidDataException>(() => UpdateFeedReader.Verify(bytes, Sign(bytes),
            _key.ExportSubjectPublicKeyInfoPem(), _now.AddYears(20), 3));
    }

    [Fact]
    public void AnnouncementsCanOmitLegacyExpiration()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Bytes(Feed))!.AsObject();
        node.Remove("expiresAt");
        var verified = Verify(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.Null(verified.ExpiresAt);
        Assert.Equal(Feed.LatestVersion, verified.LatestVersion);
    }

    [Fact]
    public void ReplacementKeyMustBeAuthorizedByEmbeddedRootAndNotExpired()
    {
        using var replacement = RSA.Create(3072);
        var bytes = Bytes(Feed);
        byte[] Envelope(DateTimeOffset expiry, bool trusted)
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var authorization = JsonSerializer.SerializeToUtf8Bytes(new UpdateFeedReader.SigningAuthorization(replacement.ExportSubjectPublicKeyInfoPem(), expiry), options);
            var authorizedBytes = System.Text.Encoding.UTF8.GetBytes("AdTrim update signing key v1\n").Concat(authorization).ToArray();
            var rootSignature = (trusted ? _key : replacement).SignData(authorizedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return JsonSerializer.SerializeToUtf8Bytes(new UpdateFeedReader.RotatedSignature(
                Convert.ToBase64String(replacement.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
                Convert.ToBase64String(authorization), Convert.ToBase64String(rootSignature)), options);
        }
        Assert.Equal(Feed.LatestVersion, Verify(bytes, Envelope(_now.AddDays(30), true)).LatestVersion);
        Assert.Throws<CryptographicException>(() => Verify(bytes, Envelope(_now.AddDays(30), false)));
        Assert.Throws<InvalidDataException>(() => Verify(bytes, Envelope(_now, true)));
    }

    [Theory]
    [InlineData("1.2.3/../../malware")]
    [InlineData("https://evil.example")]
    [InlineData("1.2.3-beta")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3\n")]
    public void ReleaseDestinationCannotBeSuppliedByFeed(string version)
    {
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed with { LatestVersion = version })));
    }

    [Fact]
    public void AmbiguousOrUnboundedMetadataIsRejected()
    {
        var text = System.Text.Encoding.UTF8.GetString(Bytes(Feed));
        var duplicate = System.Text.Encoding.UTF8.GetBytes(text.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"));
        Assert.Throws<InvalidDataException>(() => Verify(duplicate));
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed with { Summary = new string('a', 1201) })));
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed with { Summary = "Unsafe\u202etext" })));
        Assert.Throws<InvalidDataException>(() => Verify(Bytes(Feed with { Advisories = [Feed.Advisories[0] with { FixedIn = "2.0.0" }] })));
    }

    [Fact]
    public void SecurityAdviceSurvivesLaterRoutineReleasesButDoesNotWarnFixedVersions()
    {
        var feed = Verify(Bytes(Feed));
        var affected = UpdateFeedReader.Select(feed, "1.1.0")!;
        Assert.Equal("security", affected.Importance);
        // The What's new page lists every release between the installed version and the new one.
        Assert.Equal("https://adtrim.github.io/whats-new/#from=1.1.0&to=1.2.1", affected.ReleaseUri.AbsoluteUri);
        Assert.Equal("regular", UpdateFeedReader.Select(feed, "1.2.0")!.Importance);
        Assert.Null(UpdateFeedReader.Select(feed, "1.2.1"));
        Assert.Null(UpdateFeedReader.Select(feed, "2.0.0"));
    }

    [Fact]
    public void ReminderHistoryFollowsAdvisoryAndEscalationRatherThanEveryRelease()
    {
        var notice = UpdateFeedReader.Select(Feed, "1.1.0")!;
        var history = new UpdateHistory();
        Assert.True(history.IsDue(notice, _now));
        history.Acknowledge(notice, _now);
        Assert.False(history.IsDue(notice with { Version = "1.2.2" }, _now.AddDays(6)));
        Assert.True(history.IsDue(notice, _now.AddDays(7)));
        var urgent = notice with { Importance = "urgent" };
        Assert.True(history.IsDue(urgent, _now));
        history.Acknowledge(urgent, _now);
        Assert.False(history.IsDue(urgent, _now.AddHours(23)));
        Assert.True(history.IsDue(urgent, _now.AddDays(1)));
        var regular = new UpdateNotice("1.2.1", "regular", "New release", []);
        history.Acknowledge(regular, _now);
        Assert.False(history.IsDue(regular, _now.AddYears(1)));
        Assert.True(history.IsDue(regular with { Version = "1.2.2" }, _now));
    }

    [Fact]
    public async Task VerifiedCacheAndReminderSurviveRestartWithoutTrustingTamperedNetworkContent()
    {
        var bytes = Bytes(Feed with { PublishedAt = _now.AddYears(-10), ExpiresAt = _now.AddYears(-10).AddDays(30) });
        var handler = new ResponseHandler(bytes, Sign(bytes));
        var first = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await first.CheckAsync();
        Assert.Null(first.Error);
        Assert.NotNull(first.Notice);
        await first.AcknowledgeAsync();
        handler.Feed = Bytes(Feed with { Summary = "Tampered" });
        var second = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await second.CheckAsync();
        Assert.NotNull(second.Error);
        Assert.Equal(first.Notice!.Version, second.Notice!.Version);
        Assert.Equal(first.Notice.Summary, second.Notice.Summary);
        Assert.Equal(first.Notice.AdvisoryIds, second.Notice.AdvisoryIds);
        Assert.False(second.AnnouncementDue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedCacheCannotDisableSignedNetworkChecks(bool loadAtStartup)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "updates-state.json");
        foreach (var contents in new[] { "{", new string('x', 160_001), "{\"History\":null}" })
        {
            await File.WriteAllTextAsync(path, contents);
            var bytes = Bytes(Feed);
            var handler = new ResponseHandler(bytes, Sign(bytes));
            var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
            if (loadAtStartup) await service.LoadCachedAsync();
            Assert.Null(service.Notice);
            Assert.Equal(0, handler.Requests);
            await service.CheckAsync();
            Assert.Equal(2, handler.Requests);
            Assert.Null(service.Error);
            Assert.Equal(Feed.LatestVersion, service.Notice!.Version);
            var restarted = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
            await restarted.LoadCachedAsync();
            Assert.Equal(Feed.LatestVersion, restarted.Notice!.Version);
        }
    }

    [Fact]
    public async Task DamagedCacheDoesNotMakeUnsignedNetworkDataTrustworthy()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "updates-state.json");
        await File.WriteAllTextAsync(path, "{");
        var handler = new ResponseHandler(Bytes(Feed with { Summary = "Changed without signing" }), Sign(Bytes(Feed)));
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await service.CheckAsync();
        Assert.Equal(2, handler.Requests);
        Assert.NotNull(service.Error);
        Assert.Null(service.Notice);
        Assert.Equal("{", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DamagedReminderHistoryDoesNotDiscardTheKnownRevision()
    {
        var (accepted, _) = ServiceFor(Feed with { Revision = 3 });
        await accepted.CheckAsync();
        await RewriteCacheAsync(state => state.History = null!);
        var bytes = Bytes(Feed);
        var handler = new ResponseHandler(bytes, Sign(bytes));
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await service.CheckAsync();
        Assert.Equal(2, handler.Requests);
        Assert.NotNull(service.Error);
        Assert.Equal(Feed.LatestVersion, service.Notice!.Version);
    }

    private (UpdateService Service, ResponseHandler Handler) ServiceFor(UpdateFeed feed)
    {
        var bytes = Bytes(feed);
        var handler = new ResponseHandler(bytes, Sign(bytes));
        return (new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed), handler);
    }

    private async Task RewriteCacheAsync(Action<UpdateService.UpdateState> change)
    {
        var path = Path.Combine(_directory, "updates-state.json");
        var state = JsonSerializer.Deserialize<UpdateService.UpdateState>(await File.ReadAllTextAsync(path))!;
        change(state);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConflictingCacheDigestCannotOverrideTheIndependentRevisionRecord(bool reuseRevision)
    {
        var original = Feed with { Revision = 5 };
        var (accepted, _) = ServiceFor(original);
        await accepted.CheckAsync();
        string revisionPath = Path.Combine(_directory, "updates-revision.json");
        string revision = await File.ReadAllTextAsync(revisionPath);
        var requested = reuseRevision ? original with { LatestVersion = "1.3.0" } : original;
        await RewriteCacheAsync(state => state.Digest = reuseRevision
            ? Convert.ToHexString(SHA256.HashData(Bytes(requested))) : new string('0', 64));

        var (restarted, handler) = ServiceFor(requested);
        await restarted.LoadCachedAsync();
        Assert.Equal(0, handler.Requests);
        Assert.Equal(original.LatestVersion, restarted.Notice!.Version);
        await restarted.CheckAsync();

        Assert.Equal(reuseRevision, restarted.Error is not null);
        Assert.Equal(original.LatestVersion, restarted.Notice!.Version);
        Assert.Equal(revision, await File.ReadAllTextAsync(revisionPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisionMigrationUsesTheSignedFeedInsteadOfCorruptedCacheFields(bool removeRevisionRecord)
    {
        var original = Feed with { Revision = 5 };
        var (accepted, _) = ServiceFor(original);
        await accepted.CheckAsync();
        string revisionPath = Path.Combine(_directory, "updates-revision.json");
        if (removeRevisionRecord) File.Delete(revisionPath);
        await RewriteCacheAsync(state => { state.Revision = 999; state.Digest = new string('0', 64); });

        var (restarted, _) = ServiceFor(original with { Revision = 6 });
        await restarted.LoadCachedAsync();
        using (var floor = JsonDocument.Parse(await File.ReadAllTextAsync(revisionPath)))
        {
            Assert.Equal(5, floor.RootElement.GetProperty("Revision").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Bytes(original))), floor.RootElement.GetProperty("Digest").GetString());
        }
        await restarted.CheckAsync();
        Assert.Null(restarted.Error);
        Assert.Equal(original.LatestVersion, restarted.Notice!.Version);
    }

    [Fact]
    public async Task AnInvalidCachedSignatureCannotEstablishARevisionRecord()
    {
        var original = Feed with { Revision = 5 };
        var (accepted, _) = ServiceFor(original);
        await accepted.CheckAsync();
        string revisionPath = Path.Combine(_directory, "updates-revision.json");
        File.Delete(revisionPath);
        await RewriteCacheAsync(state => { state.Revision = 999; state.Signature![0] ^= 0xFF; });

        var (restarted, _) = ServiceFor(original with { Revision = 6 });
        await restarted.LoadCachedAsync();
        Assert.Null(restarted.Notice);
        Assert.False(File.Exists(revisionPath));
        await restarted.CheckAsync();
        Assert.Null(restarted.Error);
        Assert.NotNull(restarted.Notice);
    }

    [Fact]
    public async Task AConflictingSignedCacheCannotReplaceAnAcceptedAnnouncement()
    {
        var original = Feed with { Revision = 5 };
        var (accepted, _) = ServiceFor(original);
        await accepted.CheckAsync();
        var reused = Bytes(original with { LatestVersion = "1.3.0" });
        await RewriteCacheAsync(state =>
        {
            state.Feed = reused;
            state.Signature = Sign(reused);
            state.Digest = Convert.ToHexString(SHA256.HashData(reused));
        });

        var (restarted, _) = ServiceFor(original);
        await restarted.LoadCachedAsync();
        Assert.Null(restarted.Notice);
        await restarted.CheckAsync();
        Assert.Null(restarted.Error);
        Assert.Equal(original.LatestVersion, restarted.Notice!.Version);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ACorruptOrMissingCacheCannotReadmitAnOlderSignedAnnouncement(string? cache)
    {
        // Rollback protection must not depend on the disposable cache surviving.
        var (accepted, _) = ServiceFor(Feed with { Revision = 5 });
        await accepted.CheckAsync();
        Assert.Null(accepted.Error);
        var state = Path.Combine(_directory, "updates-state.json");
        if (cache is null) File.Delete(state); else await File.WriteAllTextAsync(state, cache);

        var (older, _) = ServiceFor(Feed with { Revision = 4, LatestVersion = "1.1.5" });
        await older.CheckAsync();
        Assert.NotNull(older.Error);
        Assert.Null(older.Notice);

        var (reused, _) = ServiceFor(Feed with { Revision = 5, LatestVersion = "1.3.0" });
        await reused.CheckAsync();
        Assert.NotNull(reused.Error);

        var (newer, _) = ServiceFor(Feed with { Revision = 6, LatestVersion = "1.3.0" });
        await newer.CheckAsync();
        Assert.Null(newer.Error);
        Assert.Equal("1.3.0", newer.Notice!.Version);
    }

    [Fact]
    public async Task AnExistingInstallationKeepsItsCachedRevisionWhenTheRevisionFileIsAdded()
    {
        var (accepted, _) = ServiceFor(Feed with { Revision = 5 });
        await accepted.CheckAsync();
        // An installation from before the revision file existed has only the cache.
        File.Delete(Path.Combine(_directory, "updates-revision.json"));
        var (restarted, _) = ServiceFor(Feed with { Revision = 5 });
        await restarted.LoadCachedAsync();
        Assert.True(File.Exists(Path.Combine(_directory, "updates-revision.json")));
        await File.WriteAllTextAsync(Path.Combine(_directory, "updates-state.json"), "{");

        var (older, _) = ServiceFor(Feed with { Revision = 4 });
        await older.CheckAsync();
        Assert.NotNull(older.Error);
    }

    [Fact]
    public async Task ACorruptRevisionFileFallsBackToTheCachedRevisionAndIsRewritten()
    {
        var (accepted, _) = ServiceFor(Feed with { Revision = 5 });
        await accepted.CheckAsync();
        var revision = Path.Combine(_directory, "updates-revision.json");
        await File.WriteAllTextAsync(revision, "{");

        var (older, _) = ServiceFor(Feed with { Revision = 4 });
        await older.CheckAsync();
        Assert.NotNull(older.Error);
        Assert.Contains("\"Revision\":5", await File.ReadAllTextAsync(revision));
    }

    [Fact]
    public async Task LastVerifiedAnnouncementShowsAtStartupWithoutWaitingForTheNetwork()
    {
        // Startup shows the saved result at once; the fresh check follows in the background.
        var bytes = Bytes(Feed);
        var handler = new ResponseHandler(bytes, Sign(bytes));
        await new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed).CheckAsync();
        int requests = handler.Requests;

        var restarted = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await restarted.LoadCachedAsync();

        Assert.Equal(requests, handler.Requests);
        Assert.Equal("1.2.1", restarted.Notice!.Version);
        Assert.Null(restarted.Error);
        Assert.False(restarted.IsChecking);
    }

    [Fact]
    public async Task SavedAnnouncementIsReverifiedBeforeItIsShown()
    {
        var bytes = Bytes(Feed);
        await new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(new ResponseHandler(bytes, Sign(bytes))), Installed).CheckAsync();
        var path = Path.Combine(_directory, "updates-state.json");
        var state = JsonSerializer.Deserialize<UpdateService.UpdateState>(await File.ReadAllTextAsync(path))!;
        state.Feed = Bytes(Feed with { Summary = "Tampered on disk" });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));

        var restarted = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(new ResponseHandler([], [])), Installed);
        await restarted.LoadCachedAsync();

        Assert.Null(restarted.Notice);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task FailedOrRedirectedChecksNeverReportUpToDate(int status)
    {
        var handler = new ResponseHandler([], []) { Status = (HttpStatusCode)status };
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await service.CheckAsync();
        Assert.NotNull(service.Error);
        Assert.Null(service.Notice);
    }

    [Fact]
    public async Task OversizedResponseDoesNotProduceAnAnnouncement()
    {
        var handler = new ResponseHandler(new byte[UpdateFeedReader.MaximumBytes + 1], []);
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await service.CheckAsync();
        Assert.NotNull(service.Error);
        Assert.Null(service.Notice);
    }

    [Fact]
    public async Task ReusedRevisionWithDifferentSignedContentsKeepsPreviouslyVerifiedNotice()
    {
        var original = Bytes(Feed);
        var handler = new ResponseHandler(original, Sign(original));
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        await service.CheckAsync();
        handler.Feed = Bytes(Feed with { LatestVersion = "1.3.0" });
        handler.Signature = Sign(handler.Feed);
        await service.CheckAsync();
        Assert.NotNull(service.Error);
        Assert.Equal("1.2.1", service.Notice!.Version);
    }

    [Fact]
    public async Task ConcurrentWindowChecksShareOneNetworkOperation()
    {
        var bytes = Bytes(Feed);
        var handler = new ResponseHandler(bytes, Sign(bytes)) { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler), Installed);
        var first = service.CheckAsync();
        await service.CheckAsync();
        Assert.True(service.IsChecking);
        Assert.Equal(1, handler.Requests);
        handler.Pause.SetResult();
        await first;
        Assert.Null(service.Error);
        Assert.Equal(2, handler.Requests);
    }

    private sealed class ResponseHandler(byte[] feed, byte[] signature) : HttpMessageHandler
    {
        public byte[] Feed { get; set; } = feed;
        public byte[] Signature { get; set; } = signature;
        public TaskCompletionSource? Pause { get; init; }
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (Pause is not null) await Pause.Task.WaitAsync(ct);
            return new HttpResponseMessage(Status)
            { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sig") ? Signature : Feed) };
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    // A server that sends no length must still be cut off at the limit.
    [Fact]
    public async Task AnOversizedResponseWithoutADeclaredLengthDoesNotProduceAnAnnouncement()
    {
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(new UnknownLengthHandler()), Installed);
        await service.CheckAsync();
        Assert.NotNull(service.Error);
        Assert.Null(service.Notice);
    }

    private sealed class UnknownLengthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent() });
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(new byte[UpdateFeedReader.MaximumBytes + 1]).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
