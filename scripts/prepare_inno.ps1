#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$InstallerPath,
    [switch]$VerifyOnly,
    [string]$CompilerPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = '7.1.0'
$downloadUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
# Official immutable GitHub release asset digest, verified on 2026-09-04.
$expectedHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$destination = Join-Path $projectRoot ".tools/inno-setup/$version"
$compiler = if ($CompilerPath) { [IO.Path]::GetFullPath($CompilerPath, $projectRoot) } else { Join-Path $destination 'ISCC.exe' }

function Assert-Publisher([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Pyrsys B\.V\.(,|$)') {
        throw "Invalid Inno Setup Authenticode signature or unexpected publisher: $Path ($($signature.Status))."
    }
}

function Assert-Compiler {
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "Inno Setup $version compiler missing. Run: pwsh -File scripts/prepare_inno.ps1"
    }
    Assert-Publisher $compiler
    $compilerDirectory = Split-Path -Parent $compiler
    foreach ($dependency in @('ISCmplr.dll', 'ISPP.dll')) {
        Assert-Publisher (Join-Path $compilerDirectory $dependency)
    }
    # ISCC's PE version resource is 0.0.0.0; ask the verified engine instead.
    $actualVersion = (& $compiler '--version' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualVersion -ne $version) {
        throw "Inno Setup $version is required; found $actualVersion`: $compiler"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $compilerDirectory 'Languages/ChineseSimplified.isl'))) {
        throw 'The pinned Inno Setup Simplified Chinese language file is missing.'
    }
}

if ($VerifyOnly -or $CompilerPath) {
    Assert-Compiler
    Write-Host "Verified Inno Setup $version compiler: $compiler"
    return
}
if (Test-Path -LiteralPath $compiler) {
    Assert-Compiler
    Write-Host "Verified Inno Setup $version compiler already available: $compiler"
    return
}

$cache = Join-Path $projectRoot '.tools/downloads'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$installer = if ($InstallerPath) { [IO.Path]::GetFullPath($InstallerPath, $projectRoot) } else { Join-Path $cache "innosetup-$version-x64.exe" }
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    if ($InstallerPath) { throw "Installer not found: $installer" }
    Write-Host "Downloading official Inno Setup $version compiler..."
    try { Invoke-WebRequest -Uri $downloadUrl -OutFile $installer }
    catch { throw "Download failed. Download $downloadUrl through a trusted connection, then run this script with -InstallerPath <file>. Original error: $_" }
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expectedHash) {
    throw "Inno Setup SHA-256 verification failed: $installer"
}
Assert-Publisher $installer

# Inno Setup's own /PORTABLE=1 mode disables uninstall registration, file
# associations and shortcuts. Only the compiler files are extracted here.
$workspacePrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (-not ([IO.Path]::GetFullPath($destination).StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Compiler directory must remain inside this workspace.'
}
$temporaryDirectory = Join-Path $projectRoot '.tools/inno-temp'
New-Item -ItemType Directory -Force -Path $temporaryDirectory | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new($installer)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.Environment['TEMP'] = $temporaryDirectory
$start.Environment['TMP'] = $temporaryDirectory
foreach ($argument in @('/CURRENTUSER','/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',"/DIR=$destination","/LOG=$(Join-Path $temporaryDirectory 'prepare-inno.log')")) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Preparing Inno Setup timed out.' }
if ($process.ExitCode -ne 0) { throw "Preparing Inno Setup failed (exit $($process.ExitCode)). See .tools/inno-temp/prepare-inno.log." }
Assert-Compiler
Write-Host "Verified portable Inno Setup $version compiler: $compiler"
