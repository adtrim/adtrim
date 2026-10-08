using AdTrim.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: AdTrim.UpdateFeed <updates.json> <public-key.pem>");
    return 2;
}
try
{
    var feed = UpdateFeedReader.Verify(File.ReadAllBytes(args[0]), File.ReadAllBytes(args[0] + ".sig"),
        File.ReadAllText(args[1]), DateTimeOffset.UtcNow, 0);
    Console.WriteLine($"Verified revision {feed.Revision}, release v{feed.LatestVersion}. Announcements do not expire.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Feed verification failed: " + ex.Message);
    return 1;
}
