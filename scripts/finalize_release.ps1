#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TestResultsPath,
    [Parameter(Mandatory)][string]$SmokeSummaryPath,
    [string]$Version,
    [int]$MinimumPassedTests = 613,
    [string]$PublishDirectory = 'dist/CAD2NC',
    [string]$PortableArchive = 'dist/CAD2NC-win-x64.zip',
    [string]$SourceArchive = 'dist/CAD2NC-source.zip',
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspacePrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Resolve-WorkspacePath([string]$Path, [switch]$RequireFile, [switch]$RequireDirectory) {
    $resolved = [IO.Path]::GetFullPath($Path, $projectRoot)
    if (-not $resolved.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path is outside the workspace: $resolved"
    }
    if ($RequireFile -and -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Required release file is missing: $resolved"
    }
    if ($RequireDirectory -and -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Required release directory is missing: $resolved"
    }
    return $resolved
}

function Get-RelativeWorkspacePath([string]$Path) {
    return [IO.Path]::GetRelativePath($projectRoot, $Path).Replace('\', '/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($algorithm.ComputeHash($Stream)).ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Write-AtomicUtf8([string]$Path, [string]$Content) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $Content, $utf8NoBom)
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Get-ZipFileMap([string]$Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }
            $name = $entry.FullName.Replace('\', '/')
            if ($entries.ContainsKey($name)) { throw "Duplicate archive entry: $name in $Path" }
            $stream = $entry.Open()
            try {
                $entries[$name] = [pscustomobject]@{
                    Length = $entry.Length
                    Sha256 = Get-StreamSha256 $stream
                }
            }
            finally {
                $stream.Dispose()
            }
        }
        return $entries
    }
    finally {
        $archive.Dispose()
    }
}

function Get-DirectoryFileMap([string]$Path) {
    $files = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File) {
        $name = [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\', '/')
        if ($files.ContainsKey($name)) { throw "Duplicate published file path: $name" }
        $files[$name] = [pscustomobject]@{
            Length = $file.Length
            Sha256 = Get-Sha256 $file.FullName
        }
    }
    return $files
}

function Assert-FileMapsEqual([hashtable]$Expected, [hashtable]$Actual, [string]$Description) {
    if ($Expected.Count -ne $Actual.Count) {
        throw "$Description file count differs: expected $($Expected.Count), actual $($Actual.Count)."
    }
    foreach ($name in $Expected.Keys) {
        if (-not $Actual.ContainsKey($name)) { throw "$Description is missing: $name" }
        $expectedFile = $Expected[$name]
        $actualFile = $Actual[$name]
        if ($expectedFile.Length -ne $actualFile.Length -or $expectedFile.Sha256 -ne $actualFile.Sha256) {
            throw "$Description differs: $name"
        }
    }
}

. (Join-Path $PSScriptRoot 'release_source_files.ps1')

function Get-SourceFileMap {
    $files = @{}
    foreach ($file in (Get-ReleaseSourceFiles -Root $projectRoot)) {
        $name = Get-RelativeWorkspacePath $file.FullName
        if ($files.ContainsKey($name)) { throw "Duplicate source archive input: $name" }
        $files[$name] = [pscustomobject]@{
            Length = $file.Length
            Sha256 = Get-Sha256 $file.FullName
        }
    }
    return $files
}

function Get-TrxCounts([string]$Path) {
    [xml]$document = [IO.File]::ReadAllText($Path)
    $namespace = $document.DocumentElement.NamespaceURI
    $manager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $manager.AddNamespace('t', $namespace)
    $summary = $document.SelectSingleNode('//t:ResultSummary', $manager)
    $counters = $document.SelectSingleNode('//t:ResultSummary/t:Counters', $manager)
    if ($null -eq $summary -or $null -eq $counters) { throw "TRX result counters are missing: $Path" }
    $values = [ordered]@{
        total = [int]$counters.GetAttribute('total')
        passed = [int]$counters.GetAttribute('passed')
        failed = [int]$counters.GetAttribute('failed')
        skipped = [int]$counters.GetAttribute('notExecuted')
    }
    if ($summary.GetAttribute('outcome') -ne 'Completed') { throw "TRX did not complete successfully: $Path" }
    foreach ($attribute in @('error', 'timeout', 'aborted', 'inconclusive', 'notRunnable', 'disconnected', 'warning')) {
        $value = $counters.GetAttribute($attribute)
        if ($value -and [int]$value -ne 0) { throw "TRX contains $attribute tests: $value" }
    }
    if ($values.total -le 0 -or $values.passed -ne $values.total -or $values.failed -ne 0 -or $values.skipped -ne 0) {
        throw "TRX is not fully passing: passed=$($values.passed), failed=$($values.failed), skipped=$($values.skipped), total=$($values.total)."
    }
    return [pscustomobject]$values
}

function Get-PreservedCompatibilityEvidence([string]$SelfCheckPath, [string]$ValidationPath) {
    $source = $null
    if (Test-Path -LiteralPath $SelfCheckPath -PathType Leaf) {
        $source = Get-Content -LiteralPath $SelfCheckPath -Raw | ConvertFrom-Json
    }
    elseif (Test-Path -LiteralPath $ValidationPath -PathType Leaf) {
        $validation = Get-Content -LiteralPath $ValidationPath -Raw | ConvertFrom-Json
        if ($validation.PSObject.Properties.Name -contains 'exe_compatibility') { $source = $validation.exe_compatibility }
    }
    if ($null -eq $source -or $source.PSObject.Properties.Name -notcontains 'legacy' -or
        $source.PSObject.Properties.Name -notcontains 'samples') {
        throw 'Existing legacy and compatibility sample evidence is required before finalizing the release.'
    }
    foreach ($sample in @($source.samples)) {
        if ($sample.PSObject.Properties.Name -notcontains 'fixture' -or
            $sample.PSObject.Properties.Name -notcontains 'fixture_sha256') {
            throw 'Compatibility sample evidence is incomplete.'
        }
        $fixture = Resolve-WorkspacePath $sample.fixture -RequireFile
        if ((Get-Sha256 $fixture) -ne $sample.fixture_sha256.ToLowerInvariant()) {
            throw "Compatibility fixture hash changed: $($sample.fixture)"
        }
    }
    return [pscustomobject]@{
        Legacy = $source.legacy
        Samples = @($source.samples)
    }
}

$propsPath = Join-Path $projectRoot 'Directory.Build.props'
if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $propsPath -Raw)).Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Release version must be numeric: $Version" }

$publish = Resolve-WorkspacePath $PublishDirectory -RequireDirectory
$portable = Resolve-WorkspacePath $PortableArchive -RequireFile
$source = Resolve-WorkspacePath $SourceArchive -RequireFile
$trx = Resolve-WorkspacePath $TestResultsPath -RequireFile
$smokePath = Resolve-WorkspacePath $SmokeSummaryPath -RequireFile
$installer = Resolve-WorkspacePath "dist/CAD2NC-Setup-$Version-x64.exe" -RequireFile
$selfCheckPath = Resolve-WorkspacePath 'build/qa-lnc-final/release-exe-self-check.json'
$validationPath = Resolve-WorkspacePath "dist/CAD2NC-$Version-validation.json"
$versionedSumsPath = Resolve-WorkspacePath "dist/SHA256SUMS-$Version.txt"
$latestSumsPath = Resolve-WorkspacePath 'dist/SHA256SUMS.txt'

$publishedExecutable = Join-Path $publish 'CAD2NC.exe'
$publishedApplication = Join-Path $publish 'CAD2NC.dll'
foreach ($required in @($publishedExecutable, $publishedApplication)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Published payload is incomplete: $required" }
}
$publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedApplication).ProductVersion.Split('+')[0]
if ($publishedVersion -ne $Version) { throw "Published version $publishedVersion does not match release version $Version." }

$forbiddenRuntime = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
    $_.Extension -match '^\.(py|pyc|pyo)$' -or
    $_.Name -match '^(python(?:\d+(?:\.\d+)*)?\.(?:dll|exe)|dwg2dxf\.exe|libredwg.*\.dll|_?libredwg.*\.(?:dll|pyd)|base_library\.zip)$'
})
if ($forbiddenRuntime.Count -ne 0) {
    throw "Published payload contains Python or LibreDWG runtime files: $($forbiddenRuntime.FullName -join ', ')"
}

$publishedFiles = Get-DirectoryFileMap $publish
$portableFiles = Get-ZipFileMap $portable
Assert-FileMapsEqual $publishedFiles $portableFiles 'Portable archive'

$expectedSourceFiles = Get-SourceFileMap
$archivedSourceFiles = Get-ZipFileMap $source
Assert-FileMapsEqual $expectedSourceFiles $archivedSourceFiles 'Source archive'

$testCounts = Get-TrxCounts $trx
$newestSourceWrite = @(Get-ReleaseSourceFiles -Root $projectRoot | ForEach-Object LastWriteTimeUtc | Sort-Object -Descending)[0]
if ((Get-Item -LiteralPath $trx).LastWriteTimeUtc -lt $newestSourceWrite) {
    throw 'TRX predates release source files; pass results from the final source state.'
}
if ($testCounts.passed -lt $MinimumPassedTests) {
    throw "TRX passed only $($testCounts.passed) tests; expected at least $MinimumPassedTests."
}
$smoke = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
foreach ($requiredProperty in @(
    'source',
    'output',
    'toolpaths',
    'machine_profile',
    'origin',
    'surface_z',
    'final_z',
    'depth_passes',
    'prioritize_part_stability',
    'cut_order',
    'automatic_safety_layering',
    'layered_cutting',
    'application_version'
)) {
    if ($smoke.PSObject.Properties.Name -notcontains $requiredProperty) {
        throw "Smoke summary is missing: $requiredProperty"
    }
}
$expectedSmokeSource = Resolve-WorkspacePath 'tests/fixtures/lnc_sheet_mm.dxf' -RequireFile
$actualSmokeSource = Resolve-WorkspacePath $smoke.source -RequireFile
$smokeOutput = Resolve-WorkspacePath $smoke.output -RequireFile
if (-not $actualSmokeSource.Equals($expectedSmokeSource, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected smoke source: $actualSmokeSource"
}
if ($smoke.application_version -ne $Version -or $smoke.machine_profile -ne 'LncRouter' -or
    $smoke.toolpaths -lt 1 -or $smoke.prioritize_part_stability -ne $true -or
    $smoke.cut_order -ne 'depth-major' -or $smoke.automatic_safety_layering -ne $false -or
    $smoke.layered_cutting -ne $false -or @($smoke.depth_passes).Count -ne 1) {
    throw 'Smoke summary does not prove the default stable order and single-pass LNC depth.'
}
if ((Get-Item -LiteralPath $smokePath).LastWriteTimeUtc -lt (Get-Item -LiteralPath $publishedApplication).LastWriteTimeUtc) {
    throw 'Smoke summary predates the published application; pass the smoke JSON from this release build.'
}

$compatibility = Get-PreservedCompatibilityEvidence $selfCheckPath $validationPath
$smokeOutputItem = Get-Item -LiteralPath $smokeOutput
$generatedAt = [DateTime]::UtcNow.ToString('o')
$selfCheck = [ordered]@{
    executable = Get-RelativeWorkspacePath $publishedExecutable
    version = $Version
    generated_at_utc = $generatedAt
    default_lnc = [ordered]@{
        result = 'passed'
        output = Get-RelativeWorkspacePath $smokeOutput
        summary = Get-RelativeWorkspacePath $smokePath
        profile = $smoke.machine_profile
        origin = @($smoke.origin)
        surface_z = $smoke.surface_z
        final_z = $smoke.final_z
        depth_passes = @($smoke.depth_passes)
        prioritize_part_stability = $smoke.prioritize_part_stability
        cut_order = $smoke.cut_order
        automatic_safety_layering = $smoke.automatic_safety_layering
        layered_cutting = $smoke.layered_cutting
        bytes = $smokeOutputItem.Length
        sha256 = Get-Sha256 $smokeOutput
        ui_nc_byte_equal = $null
    }
    compatibility_evidence = 'Preserved fixture evidence; the finalizer does not rerun external sample files.'
    legacy = $compatibility.Legacy
    samples = $compatibility.Samples
    all_passed = $true
}

$binaryNames = @('CAD2NC.exe', 'CAD2NC.dll', 'CAD2NC.pri', 'Cad2Nc.Core.dll', 'Cad2Nc.Application.dll')
$binaryHashes = @()
foreach ($name in $binaryNames) {
    $path = Join-Path $publish $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Published binary is missing: $name" }
    $binaryHashes += [ordered]@{
        name = $name
        bytes = (Get-Item -LiteralPath $path).Length
        sha256 = Get-Sha256 $path
        included_in_verified_payload = $true
    }
}

$artifactPaths = @($installer, $source, $portable)
$artifacts = @()
foreach ($path in $artifactPaths) {
    $item = Get-Item -LiteralPath $path
    $artifacts += [ordered]@{
        name = $item.Name
        bytes = $item.Length
        sha256 = Get-Sha256 $item.FullName
    }
}

$validation = [ordered]@{
    version = $Version
    generated_at_utc = $generatedAt
    all_software_checks_passed = $true
    release_tests = [ordered]@{
        trx = Get-RelativeWorkspacePath $trx
        passed = $testCounts.passed
        failed = $testCounts.failed
        skipped = $testCounts.skipped
        total = $testCounts.total
    }
    published_executable_smoke = [ordered]@{
        passed = $true
        summary = Get-RelativeWorkspacePath $smokePath
        prioritize_part_stability = $smoke.prioritize_part_stability
        cut_order = $smoke.cut_order
        automatic_safety_layering = $smoke.automatic_safety_layering
        depth_passes = @($smoke.depth_passes)
    }
    published_binaries = $binaryHashes
    corresponding_source = [ordered]@{
        files_verified = $expectedSourceFiles.Count
        every_file_equal = $true
    }
    portable_payload = [ordered]@{
        files_verified = $publishedFiles.Count
        every_file_equal = $true
    }
    runtime_scan = [ordered]@{
        published_files = $publishedFiles.Count
        python_or_libredwg_runtime_files = 0
    }
    exe_compatibility = $selfCheck
    artifacts = $artifacts
    ui = [ordered]@{
        automated_coverage_in_release_tests = $true
        manual_release_ui_recheck = $false
    }
    on_machine_validation = 'Pending: no physical machine connection, dry run or test cut performed.'
    installer_execution = 'Not installed, upgraded or uninstalled on this host during this release verification.'
}

$selfCheckText = ($selfCheck | ConvertTo-Json -Depth 12) + "`n"
$validationText = ($validation | ConvertTo-Json -Depth 12) + "`n"
$sidecars = @{}
foreach ($artifact in $artifacts) {
    $path = Join-Path (Split-Path -Parent $publish) $artifact.name
    $sidecars[$path + '.sha256'] = "$($artifact.sha256)  $($artifact.name)`n"
}
$sumLines = @($artifacts | ForEach-Object { "$($_.sha256)  $($_.name)" })
$sumsText = ($sumLines -join "`n") + "`n"
$genericSumLines = @($artifacts |
    Where-Object { $_.name -in @('CAD2NC-source.zip', 'CAD2NC-win-x64.zip') } |
    ForEach-Object { "$($_.sha256)  $($_.name)" })
$genericSumsText = ($genericSumLines -join "`n") + "`n"

# All validation and content generation completes before any release metadata is replaced.
if ($ValidateOnly) {
    Write-Host "Release metadata validation passed for CAD2NC $Version; no files were written."
    Write-Host "Tests: $($testCounts.passed) passed, $($testCounts.failed) failed, $($testCounts.skipped) skipped."
    Write-Host "Published files: $($publishedFiles.Count); source files: $($expectedSourceFiles.Count); forbidden runtime files: 0."
    return
}
Write-AtomicUtf8 $selfCheckPath $selfCheckText
Write-AtomicUtf8 $validationPath $validationText
foreach ($entry in $sidecars.GetEnumerator()) { Write-AtomicUtf8 $entry.Key $entry.Value }
Write-AtomicUtf8 $versionedSumsPath $sumsText
Write-AtomicUtf8 $latestSumsPath $genericSumsText

Write-Host "Release metadata finalized for CAD2NC $Version."
Write-Host "Tests: $($testCounts.passed) passed, $($testCounts.failed) failed, $($testCounts.skipped) skipped."
Write-Host "Published files: $($publishedFiles.Count); source files: $($expectedSourceFiles.Count); forbidden runtime files: 0."
Write-Host "Self-check: $(Get-RelativeWorkspacePath $selfCheckPath)"
Write-Host "Validation: $(Get-RelativeWorkspacePath $validationPath)"
Write-Host "SHA-256 manifest: $(Get-RelativeWorkspacePath $versionedSumsPath)"
