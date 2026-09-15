#Requires -Version 7.0
[CmdletBinding()]
param([string]$Executable = 'dist/CAD2NC/CAD2NC.exe', [switch]$VerifyOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$exe = [IO.Path]::GetFullPath($Executable, $root)
$qa = Join-Path $root ('build/example-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$nc = Join-Path $qa 'output.nc'
$summary = Join-Path $qa 'summary.json'
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.WorkingDirectory = Split-Path -Parent $exe
foreach ($argument in @('--convert', (Join-Path $root 'tests/fixtures/lnc_sheet_mm.dxf'),
    '--output', $nc, '--summary', $summary, '--accept-import-diagnostics')) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Example conversion timed out.' }
if ($process.ExitCode -ne 0) { throw "Example conversion failed (exit $($process.ExitCode))." }
$payload = Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json -AsHashtable
if ($payload.application_version -ne $version -or $payload.automatic_safety_layering -ne $false -or
    @($payload.depth_passes).Count -ne 1 -or $payload.depth_passes[0] -ne -0.1 -or
    $payload.toolpaths -ne 2 -or $payload.origin[0] -ne 1000 -or $payload.origin[1] -ne 2000) {
    throw 'Example version, geometry, origin or default depth changed; review before updating the example.'
}
$bytes = [IO.File]::ReadAllBytes($nc)
if (@($bytes | Where-Object { $_ -gt 127 }).Count -ne 0) { throw 'Example NC must be ASCII.' }
$code = [Text.Encoding]::ASCII.GetString($bytes)
if ($code -notmatch "`r`n" -or $code -match '(?<!\r)\n|\r(?!\n)') { throw 'Example NC must use CRLF.' }
$stem = "lnc_sheet_mm-$version"
$expectedNc = Join-Path $root "examples/$stem.nc"
$expectedJson = Join-Path $root "examples/$stem.json"
$payload.source = 'tests/fixtures/lnc_sheet_mm.dxf'
$payload.output = "examples/$stem.nc"
# Immutable dictionaries in the application have process-dependent enumeration order.
# Canonicalize public JSON object keys without changing array order or CLI output.
function ConvertTo-SortedValue($Value) {
    if ($Value -is [System.Collections.IDictionary]) {
        $sorted = [ordered]@{}
        foreach ($key in ($Value.Keys | Sort-Object -CaseSensitive)) {
            $sorted[$key] = ConvertTo-SortedValue $Value[$key]
        }
        return $sorted
    }
    if ($Value -is [System.Collections.IList]) {
        $items = @($Value | ForEach-Object { ConvertTo-SortedValue $_ })
        return ,$items
    }
    return $Value
}
$json = (ConvertTo-SortedValue $payload | ConvertTo-Json -Depth 100).Replace("`r`n", "`n").TrimEnd() + "`n"
if ($VerifyOnly) {
    if ((Get-FileHash -LiteralPath $nc).Hash -ne (Get-FileHash -LiteralPath $expectedNc).Hash) { throw 'Committed example NC differs from the current executable.' }
    if ([IO.File]::ReadAllText($expectedJson).Replace("`r`n", "`n") -cne $json) { throw 'Committed example summary differs from the current executable.' }
    Write-Host "Verified reproducible $version example (ASCII/CRLF, default final-depth pass)."
}
else {
    Copy-Item -LiteralPath $nc -Destination $expectedNc -Force
    [IO.File]::WriteAllText($expectedJson, $json, [Text.UTF8Encoding]::new($false))
    Write-Host "Generated public example: examples/$stem.nc and .json"
}
