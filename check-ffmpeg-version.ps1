# Build-time version gate for the bundled FFmpeg/ffprobe binaries.
#
# Existence isn't enough: publish.cmd copies whatever sits in the FFmpeg dir,
# so a stale or git-master build silently ships. That's how CVE-2026-8461
# (OOB write in the MagicYUV decoder, fixed in 8.1.2) reached a buildable
# state here. This gate refuses to publish unless both exes are an official
# release at or above the floor.
#
# $MinVersion is the single source of truth for the floor. Bump it when the
# floor moves; keep binaries/README.md in sync with it.
#
# $ReviewedDate / $MaxAgeDays are the staleness backstop. The floor is static,
# so it can't know about a CVE published after today. This forces a periodic
# re-check: once the pin hasn't been security-reviewed in $MaxAgeDays, the
# build fails until you re-check advisories and either update the binaries or
# bump $ReviewedDate. That review covers libmpv too (which has no -version CLI
# and so can't be gated directly). See binaries/README.md for the runbook.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$FfmpegDir,
    [string]$MinVersion = '8.1.2',
    [string]$ReviewedDate = '2026-06-24',
    [int]$MaxAgeDays = 90
)

$floor = [version]$MinVersion

foreach ($exe in @('ffmpeg.exe', 'ffprobe.exe')) {
    $path = Join-Path $FfmpegDir $exe
    if (-not (Test-Path $path)) {
        Write-Host "ERROR: $exe not found at '$path'."
        exit 1
    }

    try {
        $firstLine = (& $path -version | Select-Object -First 1)
    }
    catch {
        Write-Host "ERROR: failed to run '$path': $($_.Exception.Message)"
        exit 1
    }

    # Expected: "ffmpeg version <token> Copyright ..." where <token> is e.g.
    # "8.1.2-full_build-www.gyan.dev" (release) or "N-123034-g47e8..." (nightly).
    if ($firstLine -notmatch '^\S+\s+version\s+(\S+)') {
        Write-Host "ERROR: could not parse a version from '$exe': '$firstLine'"
        exit 1
    }
    $token = $Matches[1]

    # A release token starts with X.Y[.Z]. A nightly starts with 'N-' and has
    # no release version, so reject it outright (it may predate security fixes).
    if ($token -notmatch '^(\d+)\.(\d+)(?:\.(\d+))?') {
        Write-Host "ERROR: $exe is a non-release build ('$token'). Bundle an official release >= $MinVersion. See binaries/README.md."
        exit 1
    }

    $found = [version]::new([int]$Matches[1], [int]$Matches[2], $(if ($Matches[3]) { [int]$Matches[3] } else { 0 }))
    if ($found -lt $floor) {
        Write-Host "ERROR: $exe is $found, below the required $MinVersion (CVE-2026-8461). See binaries/README.md."
        exit 1
    }

    Write-Host "  $exe : $found (OK, >= $MinVersion)"
}

# Staleness backstop: the version floor can't know about a CVE published after
# it was set, so force a periodic re-review of the security advisories.
$reviewed = [datetime]::ParseExact($ReviewedDate, 'yyyy-MM-dd', $null)
$ageDays = [int]((Get-Date) - $reviewed).TotalDays
if ($ageDays -gt $MaxAgeDays) {
    Write-Host "ERROR: FFmpeg/libmpv pin last security-reviewed $ReviewedDate ($ageDays days ago, limit $MaxAgeDays)."
    Write-Host "Before releasing, re-check:"
    Write-Host "  - https://ffmpeg.org/security.html"
    Write-Host "  - the shinchiro libmpv build date (its embedded FFmpeg)"
    Write-Host "Then update the binaries if a fix exists, or bump ReviewedDate in check-ffmpeg-version.ps1 if current."
    exit 1
}

Write-Host "FFmpeg version gate passed (>= $MinVersion, reviewed $ReviewedDate, $ageDays days ago)."
exit 0
