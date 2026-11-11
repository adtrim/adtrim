# Build-time gate for the bundled .NET runtime.
#
# The publish is self-contained, so the runtime named by RuntimeFrameworkVersion in the
# project ships inside AdTrim.exe and is not serviced by Windows Update. This forces a
# periodic check: once the pin has not been reviewed in $MaxAgeDays, publishing fails
# until someone compares it with the latest 10.0.x servicing release, updates the pin if
# needed, and sets $ReviewedDate.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$ReviewedDate = '2026-11-11',
    [int]$MaxAgeDays = 90
)

$source = Get-Content -LiteralPath $Project -Raw
if ($source -notmatch '<RuntimeFrameworkVersion>(\d+\.\d+\.\d+)</RuntimeFrameworkVersion>') {
    Write-Host "ERROR: $Project does not pin RuntimeFrameworkVersion."
    exit 1
}
$pinned = $Matches[1]
$reviewed = [datetime]::ParseExact($ReviewedDate, 'yyyy-MM-dd', $null)
$ageDays = [int]((Get-Date) - $reviewed).TotalDays
if ($ageDays -gt $MaxAgeDays) {
    Write-Host "ERROR: .NET runtime pin $pinned last reviewed $ReviewedDate ($ageDays days ago, limit $MaxAgeDays)."
    Write-Host "Compare it with the latest release at https://dotnet.microsoft.com/download/dotnet/10.0,"
    Write-Host "update RuntimeFrameworkVersion in the project if a newer servicing release exists,"
    Write-Host "then set ReviewedDate in tools/check-runtime-version.ps1."
    exit 1
}
Write-Host ".NET runtime pin $pinned (reviewed $ReviewedDate, $ageDays days ago)."
exit 0
