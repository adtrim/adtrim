param([string]$PublishDirectory = (Join-Path $PSScriptRoot '..\src\AdTrim\bin\Release\net10.0-windows\win-x64\publish'))
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($relative in @('AdTrim.exe', 'binaries\mpv\win-x64\libmpv-2.dll', 'binaries\ffmpeg\win-x64\ffmpeg.exe', 'binaries\ffmpeg\win-x64\ffprobe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $relative) -PathType Leaf)) { throw "Missing package file: $relative" }
}
$versionSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\AdTrim\AppVersion.cs') -Raw
if ($versionSource -notmatch 'Numeric\s*=\s*"([0-9.]+)"') { throw 'Cannot read the application version.' }
$expectedVersion = $Matches[1] + '.0'
$publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'AdTrim.exe')).FileVersion
if ($publishedVersion -ne $expectedVersion) {
    throw "Published EXE is $publishedVersion, expected $expectedVersion. Run publish.cmd before packaging."
}
function Escape-Nsis([string]$value) { $value.Replace('$', '$$').Replace('"', '$\"') }
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('; Installation-owned files only. Directories are removed only when empty.')
Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($publish.Length).TrimStart('\')
    $lines.Add('Delete "$INSTDIR\' + (Escape-Nsis $relative) + '"')
}
Get-ChildItem -LiteralPath $publish -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
    $relative = $_.FullName.Substring($publish.Length).TrimStart('\')
    $lines.Add('RMDir "$INSTDIR\' + (Escape-Nsis $relative) + '"')
}
$output = Join-Path $PSScriptRoot '..\src\AdTrim\obj\uninstall-files.nsh'
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($output), $lines, [Text.UTF8Encoding]::new($false))
