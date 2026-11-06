using AdTrim.Services;

if (args.Length != 2 && !(args.Length == 4 && args[0] == "--package"))
{
    Console.Error.WriteLine("Usage: AdTrim.UpdateFeed <updates.json> <public-key.pem>");
    Console.Error.WriteLine("   or: AdTrim.UpdateFeed --package <update-package.json> <public-key.pem> <version>");
    return 2;
}
try
{
    if (args.Length == 4)
    {
        var package = UpdatePackageManifest.Verify(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[1] + ".sig"),
            File.ReadAllText(args[2]), args[3], DateTimeOffset.UtcNow);
        await package.VerifyInstallerAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, package.InstallerName), CancellationToken.None);
        Console.WriteLine($"Verified installer v{package.Version}, {package.SizeBytes} bytes.");
        return 0;
    }
    var feed = UpdateFeedReader.Verify(File.ReadAllBytes(args[0]), File.ReadAllBytes(args[0] + ".sig"),
        File.ReadAllText(args[1]), DateTimeOffset.UtcNow, 0);
    Console.WriteLine($"Verified revision {feed.Revision}, release v{feed.LatestVersion}. Announcements do not expire.");
    return 0;
}
catch (Exception ex)
{
    // The message names the check that failed (signature, size, hash, version or format).
    Console.Error.WriteLine((args.Length == 4 ? "Package" : "Feed") + " verification failed: " + ex.Message);
    return 1;
}
