using AdTrim.Services;

const string Usage = """
    Verify:
      AdTrim.UpdateFeed <updates.json> <public-key.pem>
      AdTrim.UpdateFeed --package <update-package.json> <public-key.pem> <version>
    Sign (writes <file>.sig; no passphrase, Windows unlocks the key):
      AdTrim.UpdateFeed --sign <file> <key-file> [authorization.json]
    Key management:
      AdTrim.UpdateFeed --create-key <key-file> <public-key.pem>
      AdTrim.UpdateFeed --check-key <key-file> <public-key.pem>
      AdTrim.UpdateFeed --backup-key <key-file> <backup-file>
      AdTrim.UpdateFeed --check-backup <backup-file> <public-key.pem>
      AdTrim.UpdateFeed --restore-key <backup-file> <public-key.pem> <key-file>
      AdTrim.UpdateFeed --authorize <root-key-file> <replacement-public-key.pem> <authorization.json>
    """;

string command = args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "";
bool valid = command switch
{
    "" => args.Length == 2,
    "--package" => args.Length == 4,
    "--sign" => args.Length is 3 or 4,
    "--create-key" or "--check-key" or "--backup-key" or "--check-backup" => args.Length == 3,
    "--restore-key" or "--authorize" => args.Length == 4,
    _ => false,
};
if (!valid)
{
    Console.Error.WriteLine(Usage);
    return 2;
}
try
{
    switch (command)
    {
        case "--package":
            var package = UpdatePackageManifest.Verify(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[1] + ".sig"),
                File.ReadAllText(args[2]), args[3], DateTimeOffset.UtcNow);
            await package.VerifyInstallerAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, package.InstallerName), CancellationToken.None);
            Console.WriteLine($"Verified installer v{package.Version}, {package.SizeBytes} bytes.");
            return 0;
        case "--sign":
            Signing.Sign(args[1], args[2], args.Length == 4 ? args[3] : null);
            Console.WriteLine("Signed " + Path.GetFileName(args[1]) + ". Verify it before uploading.");
            return 0;
        case "--create-key":
            Signing.CreateKey(args[1], args[2]);
            Console.WriteLine("Created Windows-account-protected key and public verification key. Back it up with --backup-key.");
            return 0;
        case "--check-key":
            Signing.CheckKey(args[1], args[2]);
            Console.WriteLine("The key file opens for this Windows account and matches the public key.");
            return 0;
        case "--backup-key":
            var passphrase = Signing.ReadPassphrase("Enter a strong backup passphrase (store it in your password manager)");
            // A mistyped passphrase would make a backup nobody can open, and the built-in check could not tell.
            if (passphrase != Signing.ReadPassphrase("Enter the passphrase again"))
                throw new InvalidDataException("The two passphrases do not match. No backup was written.");
            Signing.Backup(args[1], args[2], passphrase);
            Console.WriteLine("Encrypted recovery backup created and verified. Keep it in separate backed-up storage, and check it with --check-backup.");
            return 0;
        case "--check-backup":
            Signing.CheckOrRestoreBackup(args[1], args[2], Signing.ReadPassphrase("Enter the backup passphrase"), null);
            Console.WriteLine("The backup opens with this passphrase and matches the public key. Nothing was written.");
            return 0;
        case "--restore-key":
            Signing.CheckOrRestoreBackup(args[1], args[2], Signing.ReadPassphrase("Enter the backup passphrase"), args[3]);
            Console.WriteLine("The signing key was restored for this Windows account and verified.");
            return 0;
        case "--authorize":
            Signing.Authorize(args[1], args[2], args[3]);
            Console.WriteLine("Authorized the replacement signing key for one year.");
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
    string what = command switch { "" => "Feed verification", "--package" => "Package verification", _ => command[2..] };
    Console.Error.WriteLine(what + " failed: " + ex.Message);
    return 1;
}
