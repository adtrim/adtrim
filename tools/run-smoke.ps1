<#
.SYNOPSIS
Runs every AdTrim UI smoke check, each in its own process, and removes the synthetic media afterward.

.DESCRIPTION
Checks: the full application run (editing, saves, collapsed playback, export, source comments and
progress), open recovery, window lifecycle, window state across monitors, TS workflow, long paths,
and signed update download/install/restart (requires NSIS).
Each check gets its own short folder below -Root. Folders are removed after each check unless it
failed and -KeepFailedFixtures was given. Unit tests, the playback probe and verify-ts.ps1 are separate.

.PARAMETER Root
A new or empty folder for this run, at most 80 characters. Defaults to a new folder in the temp directory.

.PARAMETER KeepFailedFixtures
Keep the synthetic media of failed checks for inspection. Passing checks are always cleaned up.

.PARAMETER Hardware
Also run the hardware export checks inside the full run (ADTRIM_TEST_HARDWARE=1).
#>
param(
    [string]$Root,
    [switch]$KeepFailedFixtures,
    [switch]$Hardware
)
$ErrorActionPreference = 'Stop'
$program = Split-Path $PSScriptRoot -Parent
foreach ($relative in @('binaries\ffmpeg\win-x64\ffmpeg.exe', 'binaries\ffmpeg\win-x64\ffprobe.exe', 'binaries\mpv\win-x64\libmpv-2.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $program $relative) -PathType Leaf)) {
        throw "Smoke checks require the bundled native tools: $relative"
    }
}
if (-not $Root) { $Root = Join-Path ([IO.Path]::GetTempPath()) ('ats-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
# Each check adds its own name below the root; the smoke tool refuses roots longer than 100 characters.
if ($Root.Length -gt 80) { throw "Use a smoke root of at most 80 characters; '$Root' has $($Root.Length)." }
$createdRoot = -not (Test-Path -LiteralPath $Root)
New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Remove-OwnedFolder([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove '$full' outside '$Root'." }
    if (-not (Test-Path -LiteralPath $full)) { return $true }
    if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Write-Warning "Not removing link '$full'."
        return $false
    }
    # The extended-length form removes the long-path check's folders on Windows PowerShell too.
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try { [IO.Directory]::Delete('\\?\' + $full, $true); return $true }
        catch { Start-Sleep -Milliseconds 300 }
    }
    Write-Warning "Could not remove '$full'."
    return $false
}

$checks = [ordered]@{
    'full' = @()
    'open-recovery' = @('--open-recovery')
    'lifecycle' = @('--lifecycle')
    'window-state' = @('--window-state', '{dir}\window')
    'ts' = @('--ts')
    'long-path' = @('--long-path')
    'updates' = @('--updates')
}
$saved = @{}
foreach ($name in 'ADTRIM_SMOKE_ROOT', 'ADTRIM_SMOKE_KEEP_FAILED', 'ADTRIM_TEST_HARDWARE') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$results = @()
$retained = @()
$cleanupFailures = @()
Push-Location $program
try {
    dotnet build tools/AdTrim.Smoke/AdTrim.Smoke.csproj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The smoke tool did not build.' }
    foreach ($name in $checks.Keys) {
        $dir = Join-Path $Root $name
        if (Test-Path -LiteralPath $dir) { throw "'$dir' already exists; choose a new -Root." }
        New-Item -ItemType Directory -Path $dir | Out-Null
        $env:ADTRIM_SMOKE_ROOT = $dir
        $env:ADTRIM_SMOKE_KEEP_FAILED = $(if ($KeepFailedFixtures) { '1' } else { $null })
        $env:ADTRIM_TEST_HARDWARE = $(if ($Hardware -and $name -eq 'full') { '1' } else { $null })
        $arguments = @($checks[$name] | ForEach-Object { $_.Replace('{dir}', $dir) })
        Write-Host "== $name"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        # Native stderr must not stop the script; the exit code decides the result.
        $ErrorActionPreference = 'Continue'
        $output = @(& dotnet run --no-build --project tools/AdTrim.Smoke/AdTrim.Smoke.csproj -c Release -- @arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        $notes = @()
        foreach ($line in $output) {
            if ($line -match '^HARDWARE: (.+?);.*available=False') { $notes += "unavailable: $($Matches[1])" }
            if ($line -match '^CLEANUP') { $notes += $line }
        }
        if ($name -eq 'window-state') {
            $monitors = $output | Where-Object { $_ -match '^Verified monitors: (\d+)' } | ForEach-Object { [int]$Matches[1] }
            if ($code -eq 0 -and $monitors -lt 2) { $notes += 'multi-monitor maximize not checked: one monitor' }
        }
        if ($name -eq 'full' -and -not $Hardware) { $notes += 'hardware export checks skipped (use -Hardware)' }
        if ($code -ne 0) { $output | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" } }
        if ($code -ne 0 -and $KeepFailedFixtures) { $retained += $dir }
        elseif (-not (Remove-OwnedFolder $dir)) { $cleanupFailures += $dir }
        $results += [pscustomobject]@{
            Check = $name
            Result = $(if ($code -eq 0) { 'passed' } else { "FAILED ($code)" })
            Passes = @($output | Where-Object { $_ -match '^PASS' }).Count
            Seconds = [int]$watch.Elapsed.TotalSeconds
            Notes = $notes -join '; '
        }
    }
}
finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
foreach ($dir in $retained) { Write-Host "Retained failed fixture: $dir" }
foreach ($dir in $cleanupFailures) { Write-Host "CLEANUP FAILED: $dir" }
if ($createdRoot -and $retained.Count -eq 0 -and $cleanupFailures.Count -eq 0) { Remove-Item -LiteralPath $Root -Force }
$failed = @($results | Where-Object { $_.Result -ne 'passed' }).Count
if ($failed -gt 0 -or $cleanupFailures.Count -gt 0) {
    Write-Host "$failed of $($results.Count) smoke checks failed."
    exit 1
}
Write-Host "All $($results.Count) smoke checks passed."
