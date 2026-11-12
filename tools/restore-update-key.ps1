# Checks an encrypted recovery backup of the update signing key, or restores the key from it.
#
# Check only (writes nothing):
#   restore-update-key.ps1 -BackupPath <backup> -PublicKeyPath src/AdTrim/Services/update-public-key.pem
# Restore for the current Windows account (never overwrites an existing key file):
#   restore-update-key.ps1 -BackupPath <backup> -PublicKeyPath <public.pem> -KeyPath <new-private-path>
#
# Either way the backup must open with the passphrase and hold the private half of the given
# public key. Run the check after every backup and from time to time afterwards.
param(
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [string]$KeyPath
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this tool with PowerShell 7.' }
if ($KeyPath -and (Test-Path -LiteralPath $KeyPath)) { throw 'A key file already exists at that path. Refusing to overwrite it.' }
$password = Read-Host 'Enter the backup passphrase' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
$rsa = [Security.Cryptography.RSA]::Create()
$expected = [Security.Cryptography.RSA]::Create()
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $expected.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
    try { $read = 0; $rsa.ImportEncryptedPkcs8PrivateKey($plain, [IO.File]::ReadAllBytes($BackupPath), [ref]$read) }
    catch [Security.Cryptography.CryptographicException] { throw 'The backup did not open. The passphrase is wrong or the file is damaged.' }
    if ($rsa.ExportSubjectPublicKeyInfoPem() -cne $expected.ExportSubjectPublicKeyInfoPem()) {
        throw 'The backup opened, but it holds a different key than the public key given.'
    }
    if (-not $KeyPath) {
        Write-Output 'The backup opens with this passphrase and matches the public key. Nothing was written.'
        return
    }
    $bytes = $rsa.ExportPkcs8PrivateKey()
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $stream = [IO.FileStream]::new($KeyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($protected, 0, $protected.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    # Read it back the way the signing tool does.
    $check = [Security.Cryptography.RSA]::Create()
    try {
        $restored = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { $read = 0; $check.ImportPkcs8PrivateKey($restored, [ref]$read) }
        finally { [Array]::Clear($restored, 0, $restored.Length) }
        if ($check.ExportSubjectPublicKeyInfoPem() -cne $expected.ExportSubjectPublicKeyInfoPem()) { throw 'The restored key file failed verification.' }
    } finally { $check.Dispose() }
    Write-Output 'The signing key was restored for this Windows account and verified.'
} finally {
    $plain = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $password.Dispose()
    $rsa.Dispose()
    $expected.Dispose()
}
