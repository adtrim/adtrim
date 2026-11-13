# Checks the update signing commands of tools/AdTrim.UpdateFeed end to end, with throwaway
# keys in a temporary folder. It never reads or writes a real key. Run from the repository root:
#   powershell -File tools/test-update-signing.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
dotnet build "$repo\tools\AdTrim.UpdateFeed\AdTrim.UpdateFeed.csproj" -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The update feed tool did not build.' }
$tool = "$repo\tools\AdTrim.UpdateFeed\bin\Release\net10.0\AdTrim.UpdateFeed.exe"
$appKey = "$repo\src\AdTrim\Services\update-public-key.pem"
$t = Join-Path ([IO.Path]::GetTempPath()) ('adtrim-signtest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $t | Out-Null
$fails = 0
$r = $null
function Check($name, $ok) { if ($ok) { "PASS $name" } else { "FAIL $name :: $($script:r.Out)"; $script:fails++ } }
function Run($stdin, [string[]]$a) {
    $psi = [Diagnostics.ProcessStartInfo]::new($tool)
    $psi.Arguments = ($a | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    if ($stdin) { $p.StandardInput.Write($stdin) }
    $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    if (-not $p.WaitForExit(60000)) { $p.Kill(); return @{ Code = -99; Out = 'TIMEOUT' } }
    return @{ Code = $p.ExitCode; Out = $out.Trim() }
}
function NewFeed($path) {
    [IO.File]::WriteAllText($path, '{"schema":1,"revision":1,"publishedAt":"2026-01-01T00:00:00Z","latestVersion":"1.0.0","summary":"Signing test announcement.","advisories":[]}' + "`n")
}
try {
    NewFeed "$t\updates.json"
    $pass = "throwaway-passphrase-0001`n"
    $r = Run $null @('--create-key', "$t\k.dpapi", "$t\k.pem"); Check 'create key' ($r.Code -eq 0 -and (Test-Path "$t\k.dpapi") -and (Test-Path "$t\k.pem"))
    $r = Run $null @('--create-key', "$t\k.dpapi", "$t\k2.pem"); Check 'create refuses to replace a key' ($r.Code -eq 1)
    $r = Run $null @('--check-key', "$t\k.dpapi", "$t\k.pem"); Check 'check-key matches' ($r.Code -eq 0)
    $r = Run $null @('--check-key', "$t\k.dpapi", $appKey); Check 'check-key rejects another public key' ($r.Code -eq 1)
    $r = Run $null @('--sign', "$t\updates.json", "$t\k.dpapi"); Check 'sign' ($r.Code -eq 0 -and (Get-Item "$t\updates.json.sig").Length -eq 384)
    $r = Run $null @("$t\updates.json", "$t\k.pem"); Check 'the signed announcement verifies' ($r.Code -eq 0)
    $r = Run $null @("$t\updates.json", $appKey); Check 'the app''s public key rejects a throwaway signature' ($r.Code -eq 1)
    [IO.File]::AppendAllText("$t\updates.json", ' '); $r = Run $null @("$t\updates.json", "$t\k.pem"); Check 'a changed announcement no longer verifies' ($r.Code -eq 1)
    NewFeed "$t\updates.json"

    $r = Run ($pass + "different-passphrase-0002`n") @('--backup-key', "$t\k.dpapi", "$t\bad.p8"); Check 'backup refuses mismatched passphrases' ($r.Code -eq 1 -and -not (Test-Path "$t\bad.p8"))
    $r = Run ("short`nshort`n") @('--backup-key', "$t\k.dpapi", "$t\bad.p8"); Check 'backup refuses a short passphrase' ($r.Code -eq 1 -and -not (Test-Path "$t\bad.p8"))
    $r = Run ($pass + $pass) @('--backup-key', "$t\k.dpapi", "$t\b.p8"); Check 'backup' ($r.Code -eq 0 -and (Test-Path "$t\b.p8"))
    $r = Run $pass @('--check-backup', "$t\b.p8", "$t\k.pem"); Check 'check-backup with the right passphrase' ($r.Code -eq 0)
    $r = Run "wrong-passphrase-00000`n" @('--check-backup', "$t\b.p8", "$t\k.pem"); Check 'check-backup rejects a wrong passphrase' ($r.Code -eq 1)
    $r = Run $pass @('--check-backup', "$t\b.p8", $appKey); Check 'check-backup rejects another public key' ($r.Code -eq 1)
    $r = Run $pass @('--restore-key', "$t\b.p8", "$t\k.pem", "$t\restored.dpapi"); Check 'restore' ($r.Code -eq 0 -and (Test-Path "$t\restored.dpapi"))
    $r = Run $pass @('--restore-key', "$t\b.p8", "$t\k.pem", "$t\restored.dpapi"); Check 'restore refuses to replace a key' ($r.Code -eq 1 -and $r.Out -like '*already exists*')
    Remove-Item "$t\updates.json.sig"
    $r = Run $null @('--sign', "$t\updates.json", "$t\restored.dpapi"); $signed = $r.Code
    $r = Run $null @("$t\updates.json", "$t\k.pem"); Check 'the restored key signs what the original public key verifies' ($signed -eq 0 -and $r.Code -eq 0)

    $r = Run $null @('--create-key', "$t\ops.dpapi", "$t\ops.pem")
    $r = Run $null @('--authorize', "$t\k.dpapi", "$t\ops.pem", "$t\auth.json"); Check 'authorize a replacement key' ($r.Code -eq 0)
    Remove-Item "$t\updates.json.sig"
    $r = Run $null @('--sign', "$t\updates.json", "$t\ops.dpapi", "$t\auth.json"); $signed = $r.Code
    $r = Run $null @("$t\updates.json", "$t\k.pem"); Check 'an announcement signed by the authorized key verifies against the root' ($signed -eq 0 -and $r.Code -eq 0)
    $r = Run $null @("$t\updates.json", "$t\ops.pem"); Check 'the replacement key is not a root' ($r.Code -eq 1)
}
finally {
    Remove-Item -Recurse -Force $t -ErrorAction SilentlyContinue
    "temporary folder removed: $(-not (Test-Path $t)); failures: $fails"
}
exit $fails
