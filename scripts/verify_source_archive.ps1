#Requires -Version 7.0
[CmdletBinding()]
param([string]$ArchivePath = 'dist/CAD2NC-source.zip')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release_source_files.ps1')
$expected = @{}
foreach ($file in (Get-ReleaseSourceFiles -Root $root)) {
    $name = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
    if ($expected.ContainsKey($name)) { throw "Duplicate source input: $name" }
    $expected[$name] = $file
}
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath, $root))
try {
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $name = $entry.FullName.Replace('\','/')
        if (-not $expected.ContainsKey($name) -or $seen.ContainsKey($name)) { throw "Unexpected or duplicate source ZIP entry: $name" }
        $seen[$name] = $true
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($entry.Length -ne $expected[$name].Length -or $hash -ne (Get-FileHash -LiteralPath $expected[$name].FullName).Hash) {
            throw "Source ZIP differs from workspace: $name"
        }
    }
    foreach ($name in $expected.Keys) {
        if (-not $seen.ContainsKey($name)) { throw "Source ZIP missing: $name" }
    }
    Write-Host "Verified $($seen.Count) source ZIP entries byte-for-byte, including hidden repository configuration."
}
finally { $archive.Dispose() }
