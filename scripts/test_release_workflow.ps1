#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$metadataScript = Join-Path $PSScriptRoot 'release_metadata.ps1'
$publishScript = Join-Path $PSScriptRoot 'publish_github_release.ps1'
$testRoot = Join-Path $projectRoot ('build/release-workflow-tests-' + [Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
$script:assertions = 0
$script:caseNumber = 0
$script:ghState = $null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Release workflow test failed: $Message" }
    $script:assertions++
}

function Assert-Throws([scriptblock]$Action, [string]$Message, [string]$ExpectedMessage) {
    $failure = $null
    try { & $Action | Out-Null }
    catch { $failure = $_ }
    Assert-Check ($null -ne $failure) $Message
    if ($ExpectedMessage) {
        Assert-Check ($failure.Exception.Message.Contains($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase)) "$Message reports the cause: $($failure.Exception.Message)"
    }
}

function Write-FakeProps([string]$Root, [string]$Version = '2.6.4', [string]$AssemblyVersion = '2.6.4.0', [string]$FileVersion = '2.6.4.0') {
    $props = "<Project><PropertyGroup><Version>$Version</Version><AssemblyVersion>$AssemblyVersion</AssemblyVersion><FileVersion>$FileVersion</FileVersion></PropertyGroup></Project>"
    [IO.File]::WriteAllText((Join-Path $Root 'Directory.Build.props'), $props, $utf8)
}

function Write-FakeChecksum([string]$Path, [string]$Name = ([IO.Path]::GetFileName($Path))) {
    $digest = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($Path + '.sha256', "$digest  $Name`n", $utf8)
}

function New-FakeProject {
    $script:caseNumber++
    $root = Join-Path $testRoot "case-$($script:caseNumber)"
    $artifacts = Join-Path $root 'artifacts'
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'docs'))
    [void][IO.Directory]::CreateDirectory($artifacts)
    Write-FakeProps $root
    $notes = Join-Path $root 'docs/RELEASE-2.6.4.md'
    [IO.File]::WriteAllText($notes, "# Test release`n`nOffline fixture only.`n", $utf8)
    $installer = Join-Path $artifacts 'CAD2NC-Setup-2.6.4-x64.exe'
    $source = Join-Path $artifacts 'CAD2NC-source.zip'
    # Metadata validation checks byte integrity; these are intentionally tiny
    # fixtures, never executable installers or packages uploaded to GitHub.
    [IO.File]::WriteAllBytes($installer, [byte[]](0x4d, 0x5a, 1, 2, 3, 4))
    [IO.File]::WriteAllBytes($source, [byte[]](0x50, 0x4b, 3, 4, 5, 6))
    Write-FakeChecksum $installer
    Write-FakeChecksum $source
    return [pscustomobject]@{
        Root = $root
        Artifacts = $artifacts
        Notes = $notes
        Installer = $installer
        Source = $source
        Assets = @($installer, ($installer + '.sha256'), $source, ($source + '.sha256'))
    }
}

function Reset-Gh([string]$ReleaseState = 'missing', [string]$FailOperation = '') {
    $script:ghState = [pscustomobject]@{
        ReleaseState = $ReleaseState
        FailOperation = $FailOperation
        Calls = [Collections.Generic.List[object]]::new()
        Published = $false
    }
    $global:LASTEXITCODE = 0
}

# A script-local function shadows the external gh executable in child script
# scopes. Every operation is recorded; this harness cannot contact GitHub.
function gh {
    # Native invocation flattens array arguments; model that behavior when the
    # publishing script passes its verified asset list to this function.
    $arguments = @(foreach ($argument in $args) { foreach ($value in @($argument)) { [string]$value } })
    if ($arguments.Count -lt 2 -or $arguments[0] -ne 'release') { throw 'Unexpected mocked gh invocation.' }
    $operation = $arguments[1]
    $ghState.Calls.Add([pscustomobject]@{ Operation = $operation; Arguments = $arguments })
    if ($ghState.FailOperation -eq $operation) {
        $global:LASTEXITCODE = 42
        return
    }
    $global:LASTEXITCODE = 0
    switch ($operation) {
        'view' {
            if ($ghState.ReleaseState -eq 'missing') {
                $global:LASTEXITCODE = 1
                return
            }
            @{ isDraft = ($ghState.ReleaseState -eq 'draft'); url = 'https://example.invalid/test/releases/v2.6.4' } | ConvertTo-Json -Compress
        }
        'create' { $ghState.ReleaseState = 'draft' }
        'upload' {
            if ($ghState.ReleaseState -ne 'draft') { throw 'Assets must only be uploaded to a draft.' }
        }
        'edit' {
            if ($ghState.ReleaseState -ne 'draft') { throw 'Only an existing draft can be published.' }
            $ghState.ReleaseState = 'published'
            $ghState.Published = $true
        }
        default { throw "Unexpected mocked gh release operation: $operation" }
    }
}

function Invoke-FakePublish($Fixture) {
    & $publishScript -Tag 'v2.6.4' -Repository 'test-owner/test-repo' -ArtifactsDirectory $Fixture.Artifacts -ProjectRoot $Fixture.Root
}

function Assert-GhOperations([string]$Expected, [string]$Message) {
    $actual = @($script:ghState.Calls | ForEach-Object Operation) -join ','
    Assert-Check ($actual -ceq $Expected) "$Message (actual: $actual)"
}

$previousToken = $env:GH_TOKEN
$previousExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$previousExitValue = if ($previousExitCode) { $previousExitCode.Value } else { $null }
try {
    foreach ($scriptPath in @($metadataScript, $publishScript)) {
        $tokens = $null
        $parseErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
        Assert-Check ($parseErrors.Count -eq 0) "Production script parses: $scriptPath"
    }

    $fixture = New-FakeProject
    $release = & $metadataScript -Tag 'v2.6.4' -ArtifactsDirectory 'artifacts' -ProjectRoot $fixture.Root
    Assert-Check ($release.Version -ceq '2.6.4' -and $release.Tag -ceq 'v2.6.4') 'Matching tag returns the application version'
    Assert-Check ($release.NotesPath -ceq $fixture.Notes) 'Versioned notes are resolved from the chosen project root'
    Assert-Check ($release.Assets.Count -eq 4) 'Both downloads and their checksum files are required'
    foreach ($asset in $fixture.Assets) {
        Assert-Check ($release.Assets -ccontains $asset) "Resolved release assets contain $([IO.Path]::GetFileName($asset))"
    }

    foreach ($tag in @('v2.6.5', '2.6.4', 'V2.6.4', 'v2.6.4-rc.1')) {
        Assert-Throws { & $metadataScript -Tag $tag -ProjectRoot $fixture.Root } "Reject incorrect tag $tag" 'must match'
    }
    foreach ($version in @('2.6', '2.6.4.0', '2.6.4-rc.1', '02.6.4')) {
        $invalid = New-FakeProject
        Write-FakeProps $invalid.Root $version "$version.0" "$version.0"
        Assert-Throws { & $metadataScript -Tag "v$version" -ProjectRoot $invalid.Root } "Reject unsupported release version $version" 'major.minor.patch'
    }
    $invalid = New-FakeProject
    Write-FakeProps $invalid.Root '2.6.4' '2.6.3.0' '2.6.4.0'
    Assert-Throws { & $metadataScript -ProjectRoot $invalid.Root } 'Reject inconsistent assembly version' 'same release'
    Write-FakeProps $invalid.Root '2.6.4' '2.6.4.0' '2.6.5.0'
    Assert-Throws { & $metadataScript -ProjectRoot $invalid.Root } 'Reject inconsistent file version' 'same release'

    $invalid = New-FakeProject
    Remove-Item -LiteralPath $invalid.Notes
    Assert-Throws { & $metadataScript -Tag 'v2.6.4' -ProjectRoot $invalid.Root } 'Tagged builds require their release notes' 'notes missing'
    $untagged = & $metadataScript -ProjectRoot $invalid.Root
    Assert-Check ($untagged.Version -eq '2.6.4' -and $untagged.Assets.Count -eq 0) 'Ordinary CI can validate versions before release notes or downloads exist'

    for ($assetIndex = 0; $assetIndex -lt 4; $assetIndex++) {
        $invalid = New-FakeProject
        Remove-Item -LiteralPath $invalid.Assets[$assetIndex]
        Assert-Throws { & $metadataScript -Tag 'v2.6.4' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } "Reject missing asset $assetIndex" 'missing or empty'
        $invalid = New-FakeProject
        [IO.File]::WriteAllBytes($invalid.Assets[$assetIndex], [byte[]]@())
        Assert-Throws { & $metadataScript -Tag 'v2.6.4' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } "Reject empty asset $assetIndex" 'missing or empty'
    }
    foreach ($assetProperty in @('Installer', 'Source')) {
        $invalid = New-FakeProject
        [IO.File]::AppendAllText($invalid.$assetProperty, 'tampered')
        Assert-Throws { & $metadataScript -Tag 'v2.6.4' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } "Reject tampered $assetProperty bytes" 'SHA-256 mismatch'
    }
    $invalid = New-FakeProject
    Write-FakeChecksum $invalid.Installer 'another-installer.exe'
    Assert-Throws { & $metadataScript -Tag 'v2.6.4' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } 'A valid digest cannot name a different download' 'SHA-256 mismatch'
    [IO.File]::WriteAllText($invalid.Installer + '.sha256', "not-a-sha256  CAD2NC-Setup-2.6.4-x64.exe`n", $utf8)
    Assert-Throws { & $metadataScript -Tag 'v2.6.4' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } 'Reject malformed checksum metadata' 'Invalid SHA-256'

    $env:GH_TOKEN = 'offline-test-token'
    $fixture = New-FakeProject
    Reset-Gh
    Invoke-FakePublish $fixture
    Assert-GhOperations 'view,create,upload,edit' 'A new release uploads all downloads before publication'
    Assert-Check $script:ghState.Published 'Successful upload publishes the completed draft'
    $create = $script:ghState.Calls[1].Arguments
    Assert-Check ($create -contains '--draft' -and $create -contains '--verify-tag') 'Creating a release requires an existing tag and begins as a draft'
    $notesIndex = [Array]::IndexOf($create, '--notes-file')
    Assert-Check ($notesIndex -ge 0 -and $create[$notesIndex + 1] -ceq $fixture.Notes) 'The release uses its versioned notes file'
    $upload = $script:ghState.Calls[2].Arguments
    Assert-Check ($upload -contains '--clobber') 'Interrupted draft uploads can replace incomplete assets'
    foreach ($asset in $fixture.Assets) {
        Assert-Check ($upload -ccontains $asset) "Upload includes verified $([IO.Path]::GetFileName($asset))"
    }
    foreach ($call in $script:ghState.Calls) {
        $repoIndex = [Array]::IndexOf($call.Arguments, '--repo')
        Assert-Check ($repoIndex -ge 0 -and $call.Arguments[$repoIndex + 1] -ceq 'test-owner/test-repo') 'Every GitHub command targets the requested repository'
        Assert-Check ($call.Arguments -ccontains 'v2.6.4') 'Every GitHub command targets the requested tag'
    }

    Reset-Gh 'draft'
    Invoke-FakePublish $fixture
    Assert-GhOperations 'view,upload,edit' 'A rerun resumes an existing draft'
    Assert-Check $script:ghState.Published 'The resumed draft is published after upload'
    Reset-Gh 'published'
    Invoke-FakePublish $fixture
    Assert-GhOperations 'view' 'A published release is unchanged on rerun'
    Assert-Check (-not $script:ghState.Published) 'A published release is never republished or overwritten'

    Reset-Gh 'missing' 'create'
    Assert-Throws { Invoke-FakePublish $fixture } 'A draft creation failure stops publication' 'Creating the draft'
    Assert-GhOperations 'view,create' 'Creation failure prevents all asset changes'
    Assert-Check (-not $script:ghState.Published) 'Failed creation cannot publish'

    Reset-Gh 'missing' 'upload'
    Assert-Throws { Invoke-FakePublish $fixture } 'An asset upload failure stops publication' 'Uploading release assets'
    Assert-GhOperations 'view,create,upload' 'Failed upload never reaches publication'
    Assert-Check ($script:ghState.ReleaseState -eq 'draft' -and -not $script:ghState.Published) 'Failed upload leaves a resumable draft'
    $script:ghState.FailOperation = ''
    $script:ghState.Calls.Clear()
    Invoke-FakePublish $fixture
    Assert-GhOperations 'view,upload,edit' 'Retry after failed upload resumes the draft without creating another release'
    Assert-Check $script:ghState.Published 'Retry can complete publication after an interrupted upload'

    Reset-Gh 'draft' 'upload'
    Assert-Throws { Invoke-FakePublish $fixture } 'Existing draft upload failures also stop publication' 'Uploading release assets'
    Assert-GhOperations 'view,upload' 'A failed resumed upload never publishes'
    Reset-Gh 'draft' 'edit'
    Assert-Throws { Invoke-FakePublish $fixture } 'A publish API failure is reported' 'Publishing the completed draft'
    Assert-Check ($script:ghState.ReleaseState -eq 'draft' -and -not $script:ghState.Published) 'A failed publication remains a draft'

    Reset-Gh
    $env:GH_TOKEN = ''
    Assert-Throws { Invoke-FakePublish $fixture } 'Publishing requires a token' 'GH_TOKEN'
    Assert-GhOperations '' 'Missing authentication never invokes GitHub'
    $env:GH_TOKEN = 'offline-test-token'
    Reset-Gh
    Assert-Throws { & $publishScript -Tag 'v2.6.5' -Repository 'test-owner/test-repo' -ArtifactsDirectory $fixture.Artifacts -ProjectRoot $fixture.Root } 'Publication rejects an incorrect tag before contacting GitHub' 'must match'
    Assert-GhOperations '' 'Invalid tag never invokes GitHub'
    [IO.File]::AppendAllText($fixture.Source, 'changed after packaging')
    Assert-Throws { Invoke-FakePublish $fixture } 'Publication revalidates source archive integrity' 'SHA-256 mismatch'
    Assert-GhOperations '' 'Invalid downloads never invoke GitHub'

    # These check publication boundaries rather than exact workflow formatting.
    $workflow = Get-Content -LiteralPath (Join-Path $projectRoot '.github/workflows/ci.yml') -Raw
    $releaseJob = [regex]::Match($workflow, '(?ms)^  release:\r?\n(?<job>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)')
    Assert-Check $releaseJob.Success 'The workflow declares a release job'
    $job = $releaseJob.Groups['job'].Value
    Assert-Check ($job -match "(?m)^    if:.*github\.event_name\s*==\s*'push'.*startsWith\(github\.ref,\s*'refs/tags/v'\)") 'Only version tag pushes can publish a release'
    $needs = [regex]::Match($job, '(?m)^    needs:\s*\[(?<jobs>[^\]]+)\]')
    $dependencies = @($needs.Groups['jobs'].Value.Split(',') | ForEach-Object { $_.Trim() })
    Assert-Check ($dependencies -contains 'build' -and $dependencies -contains 'dependency-audit') 'Publication waits for both build verification and dependency audit'

    Write-Host "Release workflow tests passed: $script:assertions assertions; no network requests."
}
finally {
    $env:GH_TOKEN = $previousToken
    if ($previousExitCode) { $global:LASTEXITCODE = $previousExitValue }
    else { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
}
