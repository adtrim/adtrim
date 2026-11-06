param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$installer = Get-Item -LiteralPath $InstallerPath
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer.FullName)
$version = [Version]$versionInfo.FileVersion
if ($version.Revision -ne 0 -or $version.Build -lt 0) { throw 'Installer must have a three-part AdTrim version with .0 appended.' }
$numeric = $version.ToString(3)
if ($installer.Name -cne "AdTrim-Setup-v$numeric.exe") { throw 'Installer filename does not match its embedded version.' }
if ($installer.Length -le 0 -or $installer.Length -gt 1GB) { throw 'Installer size is invalid.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$manifest = [ordered]@{
    schema = 1
    version = $numeric
    installerName = $installer.Name
    sizeBytes = $installer.Length
    sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$path = Join-Path $output 'update-package.json'
if (Test-Path -LiteralPath $path) { throw 'Update package information already exists. Refusing to replace it.' }
$json = ($manifest | ConvertTo-Json).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
Write-Output "Prepared installer information for v$numeric. Sign and verify it before uploading."
