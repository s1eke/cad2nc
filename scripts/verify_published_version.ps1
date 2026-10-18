#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = & (Join-Path $PSScriptRoot 'release_metadata.ps1')
$publish = [IO.Path]::GetFullPath($PublishDirectory, $root)
foreach ($name in @('CAD2NC.exe', 'CAD2NC.dll')) {
    $path = Join-Path $publish $name
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($info.FileVersion -cne $release.WindowsVersion -or $info.ProductVersion.Split('+')[0] -cne $release.Version) {
        throw "Published binary version mismatch: $name"
    }
}
$assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'CAD2NC.dll'))
if ($assembly.Version.ToString() -cne $release.WindowsVersion) { throw 'Published AssemblyVersion mismatch.' }

# Use the pinned Windows SDK tool already restored for the application to
# inspect the native manifest embedded in the executable, not just the template.
$assets = Get-Content -LiteralPath (Join-Path $root 'src/Cad2Nc.App/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$sdk = @($assets.libraries.GetEnumerator() | Where-Object { $_.Key -like 'Microsoft.Windows.SDK.BuildTools/*' })
if ($sdk.Count -ne 1) { throw 'Cannot determine the restored Windows SDK BuildTools version.' }
$manifestTool = $null
foreach ($folder in $assets.packageFolders.Keys) {
    $sdkDirectory = Join-Path $folder $sdk[0].Value.path
    if (-not (Test-Path -LiteralPath $sdkDirectory -PathType Container)) { continue }
    $manifestTool = Get-ChildItem -LiteralPath $sdkDirectory -Filter mt.exe -Recurse -File |
        Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if ($manifestTool) { break }
}
if (-not $manifestTool) { throw 'The restored Windows SDK manifest tool is missing.' }
$evidence = Join-Path $root ('build/published-manifest-' + [Guid]::NewGuid().ToString('N') + '.xml')
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $evidence) | Out-Null
& $manifestTool.FullName '-nologo' "-inputresource:$(Join-Path $publish 'CAD2NC.exe');#1" "-out:$evidence"
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract the published executable manifest.' }
$manifest = [xml](Get-Content -LiteralPath $evidence -Raw)
if ($manifest.assembly.assemblyIdentity.version -cne $release.WindowsVersion) {
    throw 'Published native manifest version mismatch.'
}
if ($manifest.assembly.trustInfo.security.requestedPrivileges.requestedExecutionLevel.level -cne 'asInvoker') {
    throw 'Published executable must retain asInvoker privileges.'
}
Write-Host "Verified published product $($release.Version), file/assembly/native manifest $($release.WindowsVersion)."
