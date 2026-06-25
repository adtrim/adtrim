# Downloads the bundled runtime dependencies (ffmpeg + libmpv) into the
# project's binaries/ tree - the single source publish.cmd and dev builds read.
# Double-click fetch-binaries.cmd to run it.
#
# ffmpeg: BtbN's release-branch "gpl" build (GitHub CI, fast, includes libx264),
#   verified against BtbN's published checksums.sha256. Falls back to gyan.dev's
#   release-full build (verified against gyan's SHA-256) if BtbN is unreachable.
# libmpv: latest shinchiro build from SourceForge, verified against the RSS MD5.
#
# BtbN ships .zip (extracted with the built-in Expand-Archive); gyan and libmpv
# ship .7z, so 7-Zip is used for those (the standalone 7zr.exe is fetched if
# 7-Zip isn't installed).
#
# After installing it runs the build gate (check-ffmpeg-version.ps1) so you know
# whether the ffmpeg it pulled clears the version floor.

[CmdletBinding()]
param([string]$Root)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # large downloads are far faster without the progress bar on PS 5.1
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# Resolve the project root (this script's folder). $PSScriptRoot can be empty
# depending on how the script is launched, so fall back to the invocation path.
if (-not $Root) {
    if ($PSScriptRoot) { $Root = $PSScriptRoot }
    elseif ($MyInvocation.MyCommand.Path) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
    else { $Root = (Get-Location).Path }
}

$ffmpegDir = Join-Path $Root 'binaries\ffmpeg\win-x64'
$mpvDir    = Join-Path $Root 'binaries\mpv\win-x64'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('adtrim-deps-' + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Get-Text($url) {
    # Some hosts serve text as octet-stream, so .Content can come back a byte[].
    $c = (Invoke-WebRequest $url -UseBasicParsing -UserAgent 'AdTrim-fetch').Content
    if ($c -is [byte[]]) { [Text.Encoding]::UTF8.GetString($c) } else { [string]$c }
}

function Save-Url($url, $outFile) {
    Write-Host "  GET $url"
    Invoke-WebRequest $url -OutFile $outFile -UseBasicParsing -UserAgent 'Mozilla/5.0'
}

function Get-SevenZip {
    foreach ($p in @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")) {
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    Write-Host "  7-Zip not installed - fetching standalone 7zr.exe"
    $seven = Join-Path $tmp '7zr.exe'
    Save-Url 'https://www.7-zip.org/a/7zr.exe' $seven
    return $seven
}

function Expand-SevenZip($sevenZip, $archive, $dest) {
    & $sevenZip x $archive "-o$dest" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed for $archive" }
}

function Copy-FfmpegFrom($extractRoot, $label) {
    $ff = Get-ChildItem $extractRoot -Recurse -Filter 'ffmpeg.exe'  | Select-Object -First 1
    $fp = Get-ChildItem $extractRoot -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1
    if (-not $ff -or -not $fp) { throw "ffmpeg.exe / ffprobe.exe not found in the $label archive" }
    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    Copy-Item $ff.FullName (Join-Path $ffmpegDir 'ffmpeg.exe')  -Force
    Copy-Item $fp.FullName (Join-Path $ffmpegDir 'ffprobe.exe') -Force
    Write-Host "  installed from $label -> $ffmpegDir"
}

function Install-FfmpegFromBtbN {
    Write-Host "  trying BtbN (GitHub CI build)..."
    $rel = Invoke-RestMethod 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest' -UseBasicParsing -UserAgent 'AdTrim-fetch'
    # Newest release-branch, static, gpl, win64 build. Excludes master ('N-...'
    # nightlies, which the gate rejects) and the -shared variants.
    $candidates = foreach ($a in $rel.assets) {
        if ($a.name -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-\d+\.\d+\.zip$') {
            [pscustomobject]@{ Name = $a.name; Url = $a.browser_download_url; Ver = [version]"$($Matches[1]).$($Matches[2])" }
        }
    }
    $asset = $candidates | Sort-Object Ver -Descending | Select-Object -First 1
    if (-not $asset) { throw 'no release-branch gpl win64 build in the BtbN release' }
    Write-Host "  build: $($asset.Name)"
    $sumsAsset = $rel.assets | Where-Object { $_.name -eq 'checksums.sha256' } | Select-Object -First 1
    if (-not $sumsAsset) { throw 'BtbN checksums.sha256 not found' }
    $sumsText = Get-Text $sumsAsset.browser_download_url
    $line = ($sumsText -split "`n") | Where-Object { $_ -match [regex]::Escape($asset.Name) } | Select-Object -First 1
    $expected = ([regex]::Match([string]$line, '[0-9a-fA-F]{64}')).Value.ToLower()
    if (-not $expected) { throw "no checksum for $($asset.Name)" }
    $archive = Join-Path $tmp 'ffmpeg.zip'
    Save-Url $asset.Url $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLower() -ne $expected) {
        throw 'BtbN SHA-256 mismatch - download corrupt or tampered'
    }
    Write-Host "  SHA-256 verified"
    $out = Join-Path $tmp 'ffmpeg-btbn'
    Expand-Archive -Path $archive -DestinationPath $out -Force
    Copy-FfmpegFrom $out 'BtbN'
}

function Install-FfmpegFromGyan($sevenZip) {
    Write-Host "  trying gyan.dev (release-full)..."
    $base = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z'
    $ver = try { (Get-Text "$base.ver").Trim() } catch { '(unknown)' }
    Write-Host "  latest gyan release: $ver"
    $archive = Join-Path $tmp 'ffmpeg.7z'
    Save-Url $base $archive
    $expected = ([regex]::Match((Get-Text "$base.sha256"), '[0-9a-fA-F]{64}')).Value.ToLower()
    if (-not $expected) { throw 'could not read gyan SHA-256' }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLower() -ne $expected) {
        throw 'gyan SHA-256 mismatch - download corrupt or tampered'
    }
    Write-Host "  SHA-256 verified"
    $out = Join-Path $tmp 'ffmpeg-gyan'
    Expand-SevenZip $sevenZip $archive $out
    Copy-FfmpegFrom $out 'gyan'
}

function Update-Ffmpeg($sevenZip) {
    Write-Host ""
    Write-Host "== FFmpeg =="
    try {
        Install-FfmpegFromBtbN
    }
    catch {
        Write-Host "  BtbN unavailable: $($_.Exception.Message)"
        Write-Host "  falling back to gyan.dev"
        Install-FfmpegFromGyan $sevenZip
    }
}

function Update-Libmpv($sevenZip) {
    Write-Host ""
    Write-Host "== libmpv (shinchiro / SourceForge) =="
    $feed = [xml](Get-Text 'https://sourceforge.net/projects/mpv-player-windows/rss?path=/libmpv')
    $ns = New-Object System.Xml.XmlNamespaceManager($feed.NameTable)
    $ns.AddNamespace('media', 'http://video.search.yahoo.com/mrss/')
    $item = $feed.SelectNodes('//item') |
        Where-Object { $_.SelectSingleNode('title').InnerText -match 'mpv-dev-x86_64-\d+-git-[0-9a-f]+\.7z' } |
        Select-Object -First 1
    if (-not $item) { throw 'no mpv-dev-x86_64 build found in the SourceForge feed' }
    $name = $item.SelectSingleNode('title').InnerText -replace '.*/', ''
    Write-Host "  latest build: $name"
    # SourceForge's /download link returns a mirror-selection interstitial; the
    # master mirror serves the raw file directly.
    $url = "https://master.dl.sourceforge.net/project/mpv-player-windows/libmpv/$($name)?viasf=1"
    $archive = Join-Path $tmp 'libmpv.7z'
    Save-Url $url $archive
    $hashNode = $item.SelectSingleNode(".//media:hash[@algo='md5']", $ns)
    if ($hashNode) {
        $expected = $hashNode.InnerText.Trim().ToLower()
        if ((Get-FileHash $archive -Algorithm MD5).Hash.ToLower() -ne $expected) {
            throw 'libmpv MD5 mismatch - download corrupt or tampered'
        }
        Write-Host "  MD5 verified"
    } else {
        Write-Host "  (no MD5 in feed; skipping hash check)"
    }
    $out = Join-Path $tmp 'mpv'
    Expand-SevenZip $sevenZip $archive $out
    $dll = Get-ChildItem $out -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
    if (-not $dll) { throw 'libmpv-2.dll not found in the archive' }
    New-Item -ItemType Directory -Force -Path $mpvDir | Out-Null
    Copy-Item $dll.FullName (Join-Path $mpvDir 'libmpv-2.dll') -Force
    Write-Host "  installed -> $mpvDir"
}

try {
    $sevenZip = Get-SevenZip
    Update-Ffmpeg $sevenZip
    Update-Libmpv $sevenZip

    Write-Host ""
    Write-Host "== Build gate check =="
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'check-ffmpeg-version.ps1') -FfmpegDir $ffmpegDir
    $gate = $LASTEXITCODE

    Write-Host ""
    if ($gate -eq 0) {
        Write-Host "All set. binaries/ is ready - run publish.cmd && installer.cmd to release."
    } else {
        Write-Host "NOTE: the ffmpeg just installed does NOT clear the build gate (see above)."
        Write-Host "publish.cmd will refuse to build until a passing release is available."
    }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
