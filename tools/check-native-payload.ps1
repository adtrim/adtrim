[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BinariesDir,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
if (-not $ManifestPath) { $ManifestPath = Join-Path $BinariesDir 'native-build.json' }
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw 'Native build manifest is missing. See binaries/README.md.'
}
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$required = @('ffmpeg/win-x64/ffmpeg.exe', 'ffmpeg/win-x64/ffprobe.exe', 'mpv/win-x64/libmpv-2.dll')
foreach ($relative in $required) {
    $expected = $manifest.files.PSObject.Properties[$relative].Value
    if ($expected -notmatch '^[0-9a-fA-F]{64}$') { throw "Missing native hash: $relative" }
    $path = Join-Path $BinariesDir $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing native binary: $relative" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) {
        throw "Native binary does not match its recorded source build: $relative. See binaries/README.md."
    }
}
Write-Host 'Native payload matches the recorded source build.'
