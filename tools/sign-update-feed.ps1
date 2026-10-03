param(
    [Parameter(Mandatory)][string]$KeyPath,
    [string]$FeedPath,
    [string]$PublicKeyPath,
    [switch]$CreateKey,
    [string]$AuthorizePublicKeyPath,
    [string]$AuthorizationPath
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this tool with PowerShell 7.' }
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    if ($CreateKey) {
        if (Test-Path -LiteralPath $KeyPath) { throw 'Key already exists. Refusing to replace it.' }
        if (-not $PublicKeyPath) { throw 'PublicKeyPath is required when creating a key.' }
        if (Test-Path -LiteralPath $PublicKeyPath) { throw 'Public key already exists. Refusing to replace it.' }
        $privateBytes = $rsa.ExportPkcs8PrivateKey()
        try {
            $protected = [System.Security.Cryptography.ProtectedData]::Protect($privateBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
            [System.IO.File]::WriteAllBytes($KeyPath, $protected)
        } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
        [System.IO.File]::WriteAllText($PublicKeyPath, $rsa.ExportSubjectPublicKeyInfoPem())
        Write-Output 'Created Windows-account-protected key and public verification key.'
    } else {
        if (-not $FeedPath -and -not $AuthorizePublicKeyPath) { throw 'FeedPath or AuthorizePublicKeyPath is required.' }
        $privateBytes = [System.Security.Cryptography.ProtectedData]::Unprotect([System.IO.File]::ReadAllBytes($KeyPath), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { $read = 0; $rsa.ImportPkcs8PrivateKey($privateBytes, [ref]$read) }
        finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
        if ($AuthorizePublicKeyPath) {
            if (-not $AuthorizationPath) { throw 'AuthorizationPath is required.' }
            $authorization = @{ publicKey = [System.IO.File]::ReadAllText($AuthorizePublicKeyPath); expiresAt = [DateTimeOffset]::UtcNow.AddYears(1).ToString('O') } | ConvertTo-Json -Compress
            $authorizationBytes = [System.Text.Encoding]::UTF8.GetBytes($authorization)
            $signedBytes = [System.Text.Encoding]::UTF8.GetBytes("AdTrim update signing key v1`n" + $authorization)
            $authorizationSignature = $rsa.SignData($signedBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
            @{ authorization = [Convert]::ToBase64String($authorizationBytes); authorizationSignature = [Convert]::ToBase64String($authorizationSignature) } | ConvertTo-Json | Set-Content -LiteralPath $AuthorizationPath -Encoding utf8NoBOM
            Write-Output 'Authorized replacement signing key for one year.'
            return
        }
        $bytes = [System.IO.File]::ReadAllBytes($FeedPath)
        if ($bytes.Length -gt 65536) { throw 'Feed exceeds 64 KiB.' }
        $signature = $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
        if ($AuthorizationPath) {
            $certificate = Get-Content -LiteralPath $AuthorizationPath -Raw | ConvertFrom-Json
            @{ signature = [Convert]::ToBase64String($signature); authorization = $certificate.authorization; authorizationSignature = $certificate.authorizationSignature } | ConvertTo-Json | Set-Content -LiteralPath ($FeedPath + '.sig') -Encoding utf8NoBOM
        } else { [System.IO.File]::WriteAllBytes($FeedPath + '.sig', $signature) }
        Write-Output 'Signed update feed.'
    }
} finally { $rsa.Dispose() }
