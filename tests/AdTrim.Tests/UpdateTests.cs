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
        Assert.Equal("https://github.com/adtrim/adtrim/releases/tag/v1.2.1", affected.ReleaseUri.AbsoluteUri);
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
        var first = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler));
        await first.CheckAsync();
        Assert.Null(first.Error);
        Assert.NotNull(first.Notice);
        await first.AcknowledgeAsync();
        handler.Feed = Bytes(Feed with { Summary = "Tampered" });
        var second = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler));
        await second.CheckAsync();
        Assert.NotNull(second.Error);
        Assert.Equal(first.Notice!.Version, second.Notice!.Version);
        Assert.Equal(first.Notice.Summary, second.Notice.Summary);
        Assert.Equal(first.Notice.AdvisoryIds, second.Notice.AdvisoryIds);
        Assert.False(second.AnnouncementDue);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task FailedOrRedirectedChecksNeverReportUpToDate(int status)
    {
        var handler = new ResponseHandler([], []) { Status = (HttpStatusCode)status };
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler));
        await service.CheckAsync();
        Assert.NotNull(service.Error);
        Assert.Null(service.Notice);
    }

    [Fact]
    public async Task OversizedResponseDoesNotProduceAnAnnouncement()
    {
        var handler = new ResponseHandler(new byte[UpdateFeedReader.MaximumBytes + 1], []);
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler));
        await service.CheckAsync();
        Assert.NotNull(service.Error);
        Assert.Null(service.Notice);
    }

    [Fact]
    public async Task ReusedRevisionWithDifferentSignedContentsKeepsPreviouslyVerifiedNotice()
    {
        var original = Bytes(Feed);
        var handler = new ResponseHandler(original, Sign(original));
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler));
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
        var service = new UpdateService(_directory, _key.ExportSubjectPublicKeyInfoPem(), new HttpClient(handler));
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
}
