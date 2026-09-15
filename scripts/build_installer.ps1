#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PublishDirectory = 'dist/CAD2NC',
    [string]$OutputDirectory = 'dist',
    [string]$InnoSetupCompiler,
    [string]$Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspacePrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
$publish = [IO.Path]::GetFullPath($PublishDirectory, $projectRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
foreach ($path in @($publish, $output)) {
    if (-not $path.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer input and output directories must remain inside this workspace.' }
}
if ($output.TrimEnd('\') -eq $publish.TrimEnd('\') -or $output.StartsWith($publish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer output must be outside the published payload directory.'
}
$compiler = if ($InnoSetupCompiler) { [IO.Path]::GetFullPath($InnoSetupCompiler, $projectRoot) } else { Join-Path $projectRoot '.tools/inno-setup/7.1.0/ISCC.exe' }
& (Join-Path $PSScriptRoot 'prepare_inno.ps1') -VerifyOnly -CompilerPath $compiler
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Installer version must be numeric: $Version" }
foreach ($required in @('CAD2NC.exe','CAD2NC.dll','CAD2NC.pri','CAD2NC.runtimeconfig.json','Assets/CAD2NC.ico','LICENSE','使用说明.md','安装说明.md','INSTALL.md','THIRD_PARTY_NOTICES.md','licenses/InnoSetup-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw "Incomplete published payload: $required" }
}
$payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'CAD2NC.dll')).ProductVersion.Split('+')[0]
if ($payloadVersion -ne $Version) { throw "Published payload version $payloadVersion does not match installer version $Version." }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $compiler '/Qp' "/DAppVersion=$Version" "/DPublishDirectory=$publish" "/DInstallerOutputDirectory=$output" (Join-Path $projectRoot 'installer/CAD2NC.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit $LASTEXITCODE)." }
$installer = Join-Path $output "CAD2NC-Setup-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Inno Setup did not produce the expected installer.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($installer + '.sha256', "$hash  $([IO.Path]::GetFileName($installer))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Installer: $installer"
Write-Host "SHA-256: $hash"
