using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdTrim.Services;

/// <summary>
/// Creates, uses, backs up and restores the update signing key. The key file holds the PKCS#8
/// private key encrypted by Windows for the current account (DPAPI), so signing needs no
/// passphrase on the computer that owns it. The recovery backup is the same key as encrypted
/// PKCS#8 under a passphrase, for moving to another computer or account.
/// Private key bytes are never printed and are cleared after use.
/// </summary>
internal static class Signing
{
    private const int KeyBits = 3072;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Authorization(string AuthorizationBytes, string AuthorizationSignature);

    public static void CreateKey(string keyPath, string publicKeyPath)
    {
        if (File.Exists(keyPath)) throw new IOException("Key already exists. Refusing to replace it.");
        if (File.Exists(publicKeyPath)) throw new IOException("Public key already exists. Refusing to replace it.");
        using var rsa = RSA.Create(KeyBits);
        WriteProtected(keyPath, rsa);
        File.WriteAllText(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Writes <paramref name="path"/>.sig: 384 raw bytes, or the JSON envelope when an authorization is given.</summary>
    public static void Sign(string path, string keyPath, string? authorizationPath)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length > UpdateFeedReader.MaximumBytes) throw new InvalidDataException("The file to sign is empty or exceeds 64 KiB.");
        using var rsa = ReadProtected(keyPath);
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (authorizationPath is null) { File.WriteAllBytes(path + ".sig", signature); return; }
        using var certificate = JsonDocument.Parse(File.ReadAllBytes(authorizationPath));
        var envelope = new UpdateFeedReader.RotatedSignature(Convert.ToBase64String(signature),
            certificate.RootElement.GetProperty("authorization").GetString() ?? throw new InvalidDataException("Invalid authorization file."),
            certificate.RootElement.GetProperty("authorizationSignature").GetString() ?? throw new InvalidDataException("Invalid authorization file."));
        File.WriteAllBytes(path + ".sig", JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
    }

    /// <summary>Lets a replacement key sign for one year. Only the root key can do this.</summary>
    public static void Authorize(string rootKeyPath, string replacementPublicKeyPath, string authorizationPath)
    {
        if (File.Exists(authorizationPath)) throw new IOException("Authorization file already exists. Refusing to replace it.");
        using var rsa = ReadProtected(rootKeyPath);
        var authorization = JsonSerializer.SerializeToUtf8Bytes(new UpdateFeedReader.SigningAuthorization(
            File.ReadAllText(replacementPublicKeyPath), DateTimeOffset.UtcNow.AddYears(1)), Json);
        var signed = Encoding.UTF8.GetBytes("AdTrim update signing key v1\n").Concat(authorization).ToArray();
        var signature = rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        File.WriteAllBytes(authorizationPath, JsonSerializer.SerializeToUtf8Bytes(
            new { authorization = Convert.ToBase64String(authorization), authorizationSignature = Convert.ToBase64String(signature) }));
    }

    /// <summary>True when the key file opens for this Windows account and is the private half of the public key.</summary>
    public static void CheckKey(string keyPath, string publicKeyPath)
    {
        using var rsa = ReadProtected(keyPath);
        RequireSameKey(rsa, publicKeyPath, "The key file holds a different key than the public key given.");
    }

    public static void Backup(string keyPath, string backupPath, string passphrase)
    {
        if (File.Exists(backupPath)) throw new IOException("Backup already exists. Refusing to replace it.");
        if (passphrase.Length < 16) throw new InvalidDataException("Use a passphrase of at least 16 characters.");
        using var rsa = ReadProtected(keyPath);
        var encrypted = rsa.ExportEncryptedPkcs8PrivateKey(passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        using (var check = RSA.Create())
        {
            check.ImportEncryptedPkcs8PrivateKey(passphrase, encrypted, out _);
            if (check.ExportSubjectPublicKeyInfoPem() != rsa.ExportSubjectPublicKeyInfoPem()) throw new CryptographicException("Backup verification failed.");
        }
        WriteNew(backupPath, encrypted);
    }

    /// <summary>Opens the backup and compares it with the public key. Restores it when <paramref name="keyPath"/> is given.</summary>
    public static void CheckOrRestoreBackup(string backupPath, string publicKeyPath, string passphrase, string? keyPath)
    {
        if (keyPath is not null && File.Exists(keyPath)) throw new IOException("A key file already exists at that path. Refusing to replace it.");
        using var rsa = RSA.Create();
        try { rsa.ImportEncryptedPkcs8PrivateKey(passphrase, File.ReadAllBytes(backupPath), out _); }
        catch (CryptographicException) { throw new CryptographicException("The backup did not open. The passphrase is wrong or the file is damaged."); }
        RequireSameKey(rsa, publicKeyPath, "The backup opened, but it holds a different key than the public key given.");
        if (keyPath is null) return;
        WriteProtected(keyPath, rsa);
        // Read it back the way signing does.
        CheckKey(keyPath, publicKeyPath);
    }

    /// <summary>Typed input is not shown. Redirected input is read as one line, for scripted checks with throwaway keys.</summary>
    public static string ReadPassphrase(string prompt)
    {
        Console.Error.Write(prompt + ": ");
        // A pipe can begin with a byte order mark, which is not part of the passphrase.
        if (Console.IsInputRedirected) return (Console.In.ReadLine() ?? "").TrimStart('﻿');
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }

    private static void RequireSameKey(RSA rsa, string publicKeyPath, string message)
    {
        using var expected = RSA.Create();
        expected.ImportFromPem(File.ReadAllText(publicKeyPath));
        if (rsa.KeySize != KeyBits || !rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(expected.ExportSubjectPublicKeyInfo()))
            throw new CryptographicException(message);
    }

    private static RSA ReadProtected(string keyPath)
    {
        var bytes = Unprotect(File.ReadAllBytes(keyPath));
        var rsa = RSA.Create();
        try { rsa.ImportPkcs8PrivateKey(bytes, out _); return rsa; }
        catch { rsa.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void WriteProtected(string keyPath, RSA rsa)
    {
        var bytes = rsa.ExportPkcs8PrivateKey();
        try { WriteNew(keyPath, Protect(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }

    // Windows data protection for the current account, without entropy: the same bytes
    // System.Security.Cryptography.ProtectedData produces and reads, without the extra package.
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    private const int UiForbidden = 0x1;
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The signing key is protected by Windows.");
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(protect
                ? "Windows could not protect the key."
                : "The key file did not open. It belongs to another Windows account or computer, or is damaged.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                // The buffer holds private key bytes after an unprotect.
                Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
        finally { handle.Free(); }
    }
}
