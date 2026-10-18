#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'dist/CAD2NC',
    [string]$NuGetConfig,
    [string]$InnoSetupCompiler,
    [switch]$NoRestore,
    [switch]$SkipTests,
    [switch]$IsolatedBuildProfile,
    [switch]$PortableZip
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = if ($InnoSetupCompiler) { [System.IO.Path]::GetFullPath($InnoSetupCompiler, $projectRoot) } else { Join-Path $projectRoot '.tools/inno-setup/7.1.0/ISCC.exe' }
# Tool preparation is explicit; a build never installs software or silently
# changes the pinned compiler version.
& (Join-Path $projectRoot 'scripts/prepare_inno.ps1') -VerifyOnly -CompilerPath $compiler
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$releaseNotes = Join-Path $projectRoot "docs/RELEASE-$appVersion.md"
if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) { throw "Release notes missing: $releaseNotes" }
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if ($dotnet -eq $localDotnet) {
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget/packages'
}
if ($IsolatedBuildProfile) {
    # Process-local build isolation; no Windows account settings are changed.
    $env:APPDATA = Join-Path $projectRoot '.tools/profile/AppData/Roaming'
    $env:LOCALAPPDATA = Join-Path $projectRoot '.tools/profile/AppData/Local'
    New-Item -ItemType Directory -Force -Path $env:APPDATA,$env:LOCALAPPDATA | Out-Null
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
$workspacePrefix = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
if (-not $output.StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Output directory must remain inside this workspace.' }
$stage = Join-Path $projectRoot ('build/publish-' + [Guid]::NewGuid().ToString('N'))
function Assert-NativeSuccess([string]$operation) { if ($LASTEXITCODE -ne 0) { throw "$operation failed (exit $LASTEXITCODE)." } }
Push-Location $projectRoot
try {
    if (-not $NoRestore) {
        $restoreArguments = @('restore','CAD2NC.slnx','--locked-mode','-p:NuGetAudit=false')
        if ($NuGetConfig) { $restoreArguments += @('--configfile', [System.IO.Path]::GetFullPath($NuGetConfig, $projectRoot)) }
        & $dotnet @restoreArguments
        Assert-NativeSuccess 'Restore'
    }
    & (Join-Path $projectRoot 'scripts/verify_licenses.ps1')
    if (-not $SkipTests) {
        & $dotnet test tests/Cad2Nc.Tests/Cad2Nc.Tests.csproj --no-restore -c $Configuration '-p:NuGetAudit=false' --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=release.trx' --results-directory (Join-Path $projectRoot 'build/test-results')
        Assert-NativeSuccess 'Tests'
    }
    & $dotnet publish src/Cad2Nc.App/Cad2Nc.App.csproj --no-restore -c $Configuration -r win-x64 --self-contained true -o $stage '-p:NuGetAudit=false'
    Assert-NativeSuccess 'Publish'
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/PORTABLE.md') -Destination (Join-Path $stage '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/PORTABLE.md') -Destination (Join-Path $stage 'PORTABLE.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/VALIDATION.md') -Destination (Join-Path $stage 'VALIDATION.md')
    Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $stage "更新说明-$appVersion.md")
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/INSTALL.md') -Destination (Join-Path $stage '安装说明.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/INSTALL.md') -Destination (Join-Path $stage 'INSTALL.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/THIRD_PARTY_V2.md') -Destination (Join-Path $stage 'THIRD_PARTY_NOTICES.md')
    $licenseDirectory = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
    $curatedLicenses = Join-Path $projectRoot 'LICENSES/v2'
    if (-not (Test-Path -LiteralPath $curatedLicenses)) { throw 'Missing release license texts: LICENSES/v2' }
    Copy-Item -Path (Join-Path $curatedLicenses '*') -Destination $licenseDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'installer/InnoSetup-LICENSE.txt') -Destination $licenseDirectory
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/Cad2Nc.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $package = $library.Name.Replace('/', '-')
        $packagePath = Join-Path $packageRoot $library.Value.path
        if (Test-Path -LiteralPath $packagePath) {
            foreach ($license in @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object Name -Match '(?i)license|copying|notice|eula')) {
                Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenseDirectory ($package + '-' + $license.Name))
            }
        }
    }
    $forbidden = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
        $_.Extension -match '^\.(py|pyc|pyo)$' -or
        $_.Name -match '^(python(?:\d+(?:\.\d+)*)?\.(?:dll|exe)|dwg2dxf\.exe|libredwg.*\.dll|_?libredwg.*\.(?:dll|pyd)|base_library\.zip)$'
    })
    if ($forbidden.Count -ne 0) { throw 'Unexpected Python or LibreDWG runtime in the C# release.' }
    $smokeBase = Join-Path $projectRoot ('build/smoke-' + [Guid]::NewGuid().ToString('N'))
    $smokeNc = $smokeBase + '.nc'; $smokeJson = $smokeBase + '.json'
    $start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $stage 'CAD2NC.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'; $start.WorkingDirectory = $stage
    foreach ($argument in @('--convert',(Join-Path $projectRoot 'tests/fixtures/lnc_sheet_mm.dxf'),'--output',$smokeNc,'--summary',$smokeJson,'--accept-import-diagnostics')) { $start.ArgumentList.Add($argument) }
    [void]$start.Environment.Remove('DOTNET_ROOT'); [void]$start.Environment.Remove('DOTNET_ROOT_X64')
    $process = [System.Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Published executable smoke test timed out.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $smokeJson)) { throw "Published executable smoke test failed ($($process.ExitCode))." }
    $smoke = Get-Content -LiteralPath $smokeJson -Raw | ConvertFrom-Json
    if ($smoke.toolpaths -lt 1 -or (Get-Item -LiteralPath $smokeNc).Length -lt 100) { throw 'Published executable produced an empty toolpath.' }
    if ($smoke.prioritize_part_stability -ne $true -or $smoke.cut_order -ne 'depth-major' -or
        $smoke.automatic_safety_layering -ne $false -or @($smoke.depth_passes).Count -ne 1) {
        throw 'Published executable did not apply the default stability-first order and single-pass LNC depth.'
    }
    $smokeBytes = [IO.File]::ReadAllBytes($smokeNc)
    $hasNonAscii = $false
    for ($byteIndex = 0; $byteIndex -lt $smokeBytes.Length; $byteIndex++) {
        if ($smokeBytes[$byteIndex] -gt 0x7f) { $hasNonAscii = $true; break }
    }
    if ($hasNonAscii) { throw 'Published executable emitted non-ASCII bytes in the LNC NC file.' }
    $smokeCode = [Text.Encoding]::ASCII.GetString($smokeBytes)
    if ([regex]::IsMatch($smokeCode, '(?<!\r)\n') -or [regex]::IsMatch($smokeCode, '\r(?!\n)')) {
        throw 'Published executable did not emit CRLF-only line endings in the LNC NC file.'
    }
    $expectedLncHeader = "G54 G49 G90`r`nT2`r`nM03 S18000`r`nG43 H2`r`n"
    if (-not $smokeCode.StartsWith($expectedLncHeader, [StringComparison]::Ordinal)) {
        throw 'Published executable did not emit the expected LNC header (G54/G49/G90, T2, M03 S18000, G43 H2).'
    }
    $hasForbiddenLncSyntax = $smokeCode -match '\bG(?:21|17|94|40|80)\b' -or
        $smokeCode -match '\bG00\s+G43\b' -or $smokeCode -match '[()]' -or
        $smokeCode -match '(?m)^\s*(?:%|O\d*\b)'
    if ($hasForbiddenLncSyntax) {
        throw 'Published executable emitted Fanuc-only syntax or comments in the LNC NC file.'
    }
    if ($smokeCode -notmatch 'Z45\.000' -or $smokeCode -notmatch 'M17\r?\nM30\r?\n?$') {
        throw 'Published executable did not emit the expected LNC safety height or terminal M17/M30.'
    }
    if ($smoke.origin[0] -ne 1000 -or $smoke.origin[1] -ne 2000) { throw 'Published executable did not use the sheet origin.' }
    # Compile only after the complete payload has passed its smoke test. Keep
    # incomplete installers in build/ so dist/ contains completed artifacts.
    $installerStage = Join-Path $projectRoot ('build/installer-' + [Guid]::NewGuid().ToString('N'))
    & (Join-Path $projectRoot 'scripts/build_installer.ps1') -PublishDirectory $stage -OutputDirectory $installerStage -InnoSetupCompiler $compiler -Version $appVersion
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    if (Test-Path -LiteralPath $output) {
        # Archive the previous build. All move targets are verified workspace paths.
        $backup = Join-Path $projectRoot ('build/previous-release-' + [Guid]::NewGuid().ToString('N'))
        if (-not ([System.IO.Path]::GetFullPath($backup).StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase))) { throw 'Backup directory must remain inside this workspace.' }
        Move-Item -LiteralPath $output -Destination $backup
    }
    if (-not ([System.IO.Path]::GetFullPath($stage).StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase))) { throw 'Publish staging directory must remain inside this workspace.' }
    Move-Item -LiteralPath $stage -Destination $output
    $installerName = "CAD2NC-Setup-$appVersion-x64.exe"
    $installerOutput = Join-Path (Split-Path -Parent $output) $installerName
    Move-Item -LiteralPath (Join-Path $installerStage $installerName) -Destination $installerOutput -Force
    Move-Item -LiteralPath (Join-Path $installerStage ($installerName + '.sha256')) -Destination ($installerOutput + '.sha256') -Force
    if ($PortableZip) {
        $archive = $output.TrimEnd('\','/') + '-win-x64.zip'
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    }
    $sourceStage = Join-Path $projectRoot ('build/source-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $sourceStage | Out-Null
    . (Join-Path $projectRoot 'scripts/release_source_files.ps1')
    $sourceFiles = @(Get-ReleaseSourceFiles -Root $projectRoot)
    foreach ($file in $sourceFiles) {
        $relative = [System.IO.Path]::GetRelativePath($projectRoot, $file.FullName)
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    $sourceArchive = $output.TrimEnd('\','/') + '-source.zip'
    if (Test-Path -LiteralPath $sourceArchive) { Remove-Item -LiteralPath $sourceArchive -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceStage, $sourceArchive)
    $sourceHash = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($sourceArchive + '.sha256', "$sourceHash  $([IO.Path]::GetFileName($sourceArchive))`n", [Text.UTF8Encoding]::new($false))
    & (Join-Path $projectRoot 'scripts/verify_source_archive.ps1') -ArchivePath $sourceArchive
    Write-Host "Published and verified: $output"
    Write-Host "Installer: $installerOutput"
    if ($PortableZip) { Write-Host "Portable archive: $archive" }
    Write-Host "Corresponding source: $sourceArchive"
}
finally { Pop-Location }
