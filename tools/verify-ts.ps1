$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root ('src\AdTrim\obj\ts-verification-' + [Guid]::NewGuid().ToString('N'))
foreach ($relative in @('binaries\ffmpeg\win-x64\ffmpeg.exe', 'binaries\ffmpeg\win-x64\ffprobe.exe', 'binaries\mpv\win-x64\libmpv-2.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "TS verification requires the bundled native tools: $relative"
    }
}
Push-Location $root
try {
    dotnet test tests/AdTrim.Tests/AdTrim.Tests.csproj --no-restore -c Release --filter 'FullyQualifiedName!~FixtureIntegrationTests' --logger 'trx;LogFileName=ts.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'TS workflow tests failed.' }
    [xml]$report = Get-Content -LiteralPath (Join-Path $results 'ts.trx') -Raw
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int]$counts.total -eq 0 -or [int]$counts.passed -ne [int]$counts.total) {
        throw 'Every TS verification test must run and pass. Skipped tests do not verify media support.'
    }
    dotnet run --project tools/AdTrim.Smoke -c Release --no-restore -- --ts
    if ($LASTEXITCODE -ne 0) { throw 'TS application smoke checks failed.' }
    Write-Host "TS verification passed. Test results: $results"
}
finally { Pop-Location }
