#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArchiveDirectory = 'LICENSES/v2',
    [string]$AssetsPath = 'src/Cad2Nc.App/obj/project.assets.json'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$archive = [IO.Path]::GetFullPath($ArchiveDirectory, $root)
$assetsFile = [IO.Path]::GetFullPath($AssetsPath, $root)
$manifest = Get-Content -LiteralPath (Join-Path $archive 'manifest.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
$packages = @{}
$paths = @{}
foreach ($package in $manifest.packages) {
    $key = "$($package.package)/$($package.version)"
    if ($packages.ContainsKey($key)) { throw "Duplicate license package: $key" }
    $packages[$key] = $true
    $hasLicense = $false
    $hasMetadata = $false
    foreach ($file in $package.files) {
        $path = [IO.Path]::GetFullPath($file.path, $archive)
        if (-not $path.StartsWith($archive.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "License path escapes archive: $($file.path)"
        }
        if ($paths.ContainsKey($path)) { throw "Duplicate license file: $($file.path)" }
        $paths[$path] = $true
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "License bytes/hash differ: $($file.path)"
        }
        if ($item.Name -match '(?i)license|copying|eula') { $hasLicense = $true }
        if ($item.Extension -eq '.nuspec') { $hasMetadata = $true }
    }
    if (-not $hasLicense -or -not $hasMetadata) { throw "Missing license or NuGet metadata: $key" }
}
$required = @{}
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -eq 'package') { $required[$library.Name] = $true }
}
foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
    if ($framework.PSObject.Properties.Name -contains 'downloadDependencies') {
        foreach ($dependency in $framework.downloadDependencies) {
            $bounds = @($dependency.version.Trim('[',']').Split(',') | ForEach-Object { $_.Trim() })
            if ($bounds.Count -gt 2 -or ($bounds.Count -eq 2 -and $bounds[0] -ne $bounds[1])) {
                throw "Download dependency must have one exact version: $($dependency.name)/$($dependency.version)"
            }
            $version = $bounds[0]
            $required["$($dependency.name)/$version"] = $true
        }
    }
}
foreach ($key in $required.Keys) {
    if (-not $packages.ContainsKey($key)) { throw "Dependency has no version-matched license archive: $key" }
}
Write-Host "Verified $($required.Count) dependencies and $($paths.Count) original license/metadata files."
