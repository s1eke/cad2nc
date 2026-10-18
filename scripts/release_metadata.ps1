#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$ArtifactsDirectory,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ProjectRoot)
$props = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup
$version = [string]$props.Version
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Release version must be major.minor.patch: $version"
}
foreach ($part in $version.Split('.')) {
    $number = 0
    if (-not [int]::TryParse($part, [ref]$number) -or $number -gt 65534) {
        throw 'Version components must be in 0..65534 for Windows and .NET versions.'
    }
}
if ([string]$props.AssemblyVersion -cne '$(Version).0' -or [string]$props.FileVersion -cne '$(Version).0') {
    throw 'AssemblyVersion and FileVersion must be derived from Version using $(Version).0.'
}
if ($Tag -and $Tag -cne "v$version") { throw "Tag '$Tag' must match the application version: v$version" }
$notes = Join-Path $root "docs/RELEASE-$version.md"
if ($Tag -and -not (Test-Path -LiteralPath $notes -PathType Leaf)) { throw "Release notes missing: $notes" }

$assets = [Collections.Generic.List[string]]::new()
if ($ArtifactsDirectory) {
    $directory = [IO.Path]::GetFullPath($ArtifactsDirectory, $root)
    foreach ($name in @("CAD2NC-Setup-$version-x64.exe", 'CAD2NC-source.zip')) {
        $path = Join-Path $directory $name
        $sidecar = $path + '.sha256'
        foreach ($required in @($path, $sidecar)) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf) -or (Get-Item -LiteralPath $required).Length -eq 0) {
                throw "Release asset missing or empty: $required"
            }
        }
        $checksum = [IO.File]::ReadAllText($sidecar)
        if ($checksum -notmatch '\A([a-fA-F0-9]{64})  ([^\r\n]+)\r?\n?\z') {
            throw "Invalid SHA-256 sidecar: $sidecar"
        }
        $expectedHash = $Matches[1]; $expectedName = $Matches[2]
        if ($expectedName -cne $name -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expectedHash) {
            throw "Release asset SHA-256 mismatch: $name"
        }
        $assets.Add($path); $assets.Add($sidecar)
    }
}
[pscustomobject]@{ Version = $version; WindowsVersion = "$version.0"; Tag = $Tag; NotesPath = $notes; Assets = $assets.ToArray() }
