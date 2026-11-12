param([Parameter(Mandatory)][string]$KeyPath, [Parameter(Mandatory)][string]$BackupPath)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this tool with PowerShell 7.' }
if (Test-Path -LiteralPath $BackupPath) { throw 'Backup already exists. Refusing to overwrite it.' }
$password = Read-Host 'Enter a strong backup passphrase (store it in your password manager)' -AsSecureString
$again = Read-Host 'Enter the passphrase again' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
$pointerAgain = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($again)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    # A mistyped passphrase would make a backup nobody can open, and the check below could not tell.
    if ($plain -cne [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointerAgain)) { throw 'The two passphrases do not match. No backup was written.' }
    if ($plain.Length -lt 16) { throw 'Use a passphrase of at least 16 characters.' }
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { $read = 0; $rsa.ImportPkcs8PrivateKey($bytes, [ref]$read) }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    $parameters = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc, [Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
    $encrypted = $rsa.ExportEncryptedPkcs8PrivateKey($plain, $parameters)
    $check = [Security.Cryptography.RSA]::Create()
    try {
        $read = 0; $check.ImportEncryptedPkcs8PrivateKey($plain, $encrypted, [ref]$read)
        if ($check.ExportSubjectPublicKeyInfoPem() -cne $rsa.ExportSubjectPublicKeyInfoPem()) { throw 'Backup verification failed.' }
    } finally { $check.Dispose() }
    [IO.File]::WriteAllBytes($BackupPath, $encrypted)
    Write-Output 'Encrypted recovery backup created and verified. Keep it in separate backed-up storage.'
    Write-Output 'Check it now, and again from time to time, with tools/restore-update-key.ps1 (no -KeyPath).'
} finally {
    $plain = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointerAgain)
    $password.Dispose()
    $again.Dispose()
    $rsa.Dispose()
}
