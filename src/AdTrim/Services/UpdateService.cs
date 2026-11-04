using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdTrim.Services;

public sealed class UpdateService
{
    private static readonly Uri FeedUri = new("https://adtrim.github.io/updates.json");
    private readonly string _directory;
    private readonly string _publicKey;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateState _state = new();
    private bool _loaded;
    public UpdateNotice? Notice { get; private set; }
    public bool IsChecking { get; private set; }
    public string? Error { get; private set; }
    public event EventHandler? Changed;

    private readonly string _installed;

    public UpdateService(string directory, string publicKey, HttpClient? http = null, string? installed = null)
    {
        _directory = directory;
        _installed = installed ?? AppVersion.Numeric;
        _publicKey = publicKey;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public bool AnnouncementDue => Notice is not null && _state.History.IsDue(Notice, DateTimeOffset.UtcNow);
    public bool IsReminder => Notice is not null && Notice.AdvisoryIds.Any(id => _state.History.Seen.ContainsKey("advisory:" + id));

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        IsChecking = true;
        Error = null;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (!_loaded)
            {
                await LoadAsync(ct);
                _loaded = true;
            }
            var bytes = await DownloadAsync(FeedUri, UpdateFeedReader.MaximumBytes, ct);
            var signature = await DownloadAsync(new Uri(FeedUri.AbsoluteUri + ".sig"), UpdateFeedReader.MaximumSignatureBytes, ct);
            var feed = UpdateFeedReader.Verify(bytes, signature, _publicKey, DateTimeOffset.UtcNow, _state.Revision);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (feed.Revision == _state.Revision && _state.Digest is not null && digest != _state.Digest)
                throw new InvalidDataException("Update revision was reused.");
            _state.Revision = feed.Revision;
            _state.Digest = digest;
            _state.Feed = bytes;
            _state.Signature = signature;
            Notice = UpdateFeedReader.Select(feed, _installed);
            await SaveAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException or FormatException or OperationCanceledException)
        {
            Error = "Unable to verify update information. Check your connection and try again.";
            Notice = CachedNotice();
        }
        finally
        {
            IsChecking = false;
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows the last verified announcement at once, before a network check finishes.
    /// Makes no requests; the saved feed is re-verified against the embedded key.</summary>
    public async Task LoadCachedAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (!_loaded)
            {
                await LoadAsync(ct);
                _loaded = true;
            }
            Notice ??= CachedNotice();
        }
        // Disk failures must not prevent a later network check.
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private UpdateNotice? CachedNotice()
    {
        if (_state.Feed is null || _state.Signature is null) return null;
        try
        {
            var cached = UpdateFeedReader.Verify(_state.Feed, _state.Signature, _publicKey, DateTimeOffset.UtcNow, _state.Revision);
            return UpdateFeedReader.Select(cached, _installed);
        }
        catch (Exception invalid) when (invalid is IOException or InvalidDataException or JsonException or CryptographicException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public async Task AcknowledgeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Notice is null) return;
            _state.History.Acknowledge(Notice, DateTimeOffset.UtcNow);
            await SaveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "The update reminder could not be saved."; }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maximum, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new HttpRequestException("Update server unavailable.");
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Update response too large.");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Update response too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var path = Path.Combine(_directory, "updates-state.json");
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 160_000) throw new InvalidDataException("Update state too large.");
                var state = JsonSerializer.Deserialize<UpdateState>(await File.ReadAllTextAsync(path, ct)) ?? new();
                if (state.History is null || state.History.Seen is null || state.History.Seen.Count > 128
                    || state.History.Seen.Values.Any(v => v is null)) state.History = new();
                _state = state;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // Keep the file until a verified announcement can replace it.
            _state = new();
        }
        // Only the separate record and verified announcement bytes establish the revision and digest.
        // The cache's copies of those fields can be damaged even when its JSON still parses.
        var floor = await ReadRevisionFloorAsync(ct);
        _state.Revision = floor?.Revision ?? 0;
        _state.Digest = floor?.Digest;
        try
        {
            if (_state.Feed is null || _state.Signature is null) return;
            var cached = UpdateFeedReader.Verify(_state.Feed, _state.Signature, _publicKey, DateTimeOffset.UtcNow, _state.Revision);
            var digest = Convert.ToHexString(SHA256.HashData(_state.Feed));
            if (cached.Revision == _state.Revision && _state.Digest is not null && digest != _state.Digest)
                throw new InvalidDataException("Update revision was reused.");
            _state.Revision = cached.Revision;
            _state.Digest = digest;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException or ArgumentException or FormatException)
        {
            _state.Feed = null;
            _state.Signature = null;
            return;
        }
        if (floor is null || floor.Revision < _state.Revision || floor.Digest is null)
        {
            try { await WriteRevisionFloorAsync(ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record RevisionFloor(long Revision, string? Digest);
    private string RevisionPath => Path.Combine(_directory, "updates-revision.json");

    private async Task<RevisionFloor?> ReadRevisionFloorAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(RevisionPath) || new FileInfo(RevisionPath).Length > 1024) return null;
            var floor = JsonSerializer.Deserialize<RevisionFloor>(await File.ReadAllTextAsync(RevisionPath, ct));
            return floor is { Revision: > 0 } ? floor : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task WriteRevisionFloorAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var temporary = RevisionPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new RevisionFloor(_state.Revision, _state.Digest)), ct);
        File.Move(temporary, RevisionPath, overwrite: true);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        // The revision is recorded before the cache, so an interrupted save cannot leave it behind.
        if (_state.Revision > 0) await WriteRevisionFloorAsync(ct);
        var path = Path.Combine(_directory, "updates-state.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_state), ct);
        File.Move(temporary, path, overwrite: true);
    }

    public sealed class UpdateState
    {
        public long Revision { get; set; }
        public string? Digest { get; set; }
        public byte[]? Feed { get; set; }
        public byte[]? Signature { get; set; }
        public UpdateHistory History { get; set; } = new();
    }
}
