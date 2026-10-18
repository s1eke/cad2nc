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
$script:expectedCommit = 'a' * 40

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

function Write-FakeProps([string]$Root, [string]$Version = '2.7.0', [string]$AssemblyVersion = '$(Version).0', [string]$FileVersion = '$(Version).0') {
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
    $notes = Join-Path $root 'docs/RELEASE-2.7.0.md'
    [IO.File]::WriteAllText($notes, "# Test release`n`nOffline fixture only.`n", $utf8)
    $installer = Join-Path $artifacts 'CAD2NC-Setup-2.7.0-x64.exe'
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

function Reset-Gh([string]$ReleaseState = 'missing', [string]$FailOperation = '', [string]$RemoteCommit = $script:expectedCommit, [string]$ChangedCommit = '') {
    $script:ghState = [pscustomobject]@{
        ReleaseState = $ReleaseState
        FailOperation = $FailOperation
        Calls = [Collections.Generic.List[object]]::new()
        Published = $false
        RemoteCommit = $RemoteCommit
        ChangedCommit = $ChangedCommit
        ResolveCount = 0
    }
    $global:LASTEXITCODE = 0
}

# A script-local function shadows the external gh executable in child script
# scopes. Every operation is recorded; this harness cannot contact GitHub.
function gh {
    # Native invocation flattens array arguments; model that behavior when the
    # publishing script passes its verified asset list to this function.
    $arguments = @(foreach ($argument in $args) { foreach ($value in @($argument)) { [string]$value } })
    if ($arguments.Count -lt 2) { throw 'Unexpected mocked gh invocation.' }
    if ($arguments[0] -eq 'api' -and $arguments[1] -ceq 'repos/test-owner/test-repo/commits/v2.7.0') {
        $operation = 'resolve'
    }
    elseif ($arguments[0] -eq 'api' -and $arguments[1] -ceq 'repos/test-owner/test-repo/releases') { $operation = 'view' }
    elseif ($arguments[0] -eq 'release') { $operation = $arguments[1] }
    else { throw "Unexpected mocked gh invocation: $($arguments -join ' ')" }
    $ghState.Calls.Add([pscustomobject]@{ Operation = $operation; Arguments = $arguments })
    if ($ghState.FailOperation -eq $operation) {
        $global:LASTEXITCODE = 42
        return
    }
    $global:LASTEXITCODE = 0
    switch ($operation) {
        'resolve' {
            $ghState.ResolveCount++
            $jqIndex = [Array]::IndexOf($arguments, '--jq')
            if ($jqIndex -lt 0 -or $arguments[$jqIndex + 1] -cne '.sha') { throw 'Remote tag resolution must select its commit SHA.' }
            if (-not $ghState.RemoteCommit) { $global:LASTEXITCODE = 1; return }
            if ($ghState.ChangedCommit -and $ghState.ResolveCount -gt 1) { return $ghState.ChangedCommit }
            return $ghState.RemoteCommit
        }
        'view' {
            if ($arguments[0] -cne 'api' -or $arguments -cnotcontains '--paginate') { throw 'Existing releases must be read through the paginated API.' }
            $jqIndex = [Array]::IndexOf($arguments, '--jq')
            if ($jqIndex -lt 0 -or $arguments[$jqIndex + 1] -notmatch '\.tag_name\s*==\s*"v2\.7\.0"') { throw 'Release lookup must select the requested tag.' }
            if ($ghState.ReleaseState -eq 'missing') { return }
            @{ isDraft = ($ghState.ReleaseState -eq 'draft'); url = 'https://example.invalid/test/releases/v2.7.0' } | ConvertTo-Json -Compress
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
    & $publishScript -Tag 'v2.7.0' -CommitSha $script:expectedCommit -Repository 'test-owner/test-repo' -ArtifactsDirectory $Fixture.Artifacts -ProjectRoot $Fixture.Root
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
    $release = & $metadataScript -Tag 'v2.7.0' -ArtifactsDirectory 'artifacts' -ProjectRoot $fixture.Root
    Assert-Check ($release.Version -ceq '2.7.0' -and $release.Tag -ceq 'v2.7.0') 'Matching tag returns the application version'
    Assert-Check ($release.WindowsVersion -ceq '2.7.0.0') 'Windows version is derived from the single application version'
    Assert-Check ($release.NotesPath -ceq $fixture.Notes) 'Versioned notes are resolved from the chosen project root'
    Assert-Check ($release.Assets.Count -eq 4) 'Both downloads and their checksum files are required'
    foreach ($asset in $fixture.Assets) {
        Assert-Check ($release.Assets -ccontains $asset) "Resolved release assets contain $([IO.Path]::GetFileName($asset))"
    }

    foreach ($tag in @('v2.7.1', '2.7.0', 'V2.7.0', 'v2.7.0-rc.1')) {
        Assert-Throws { & $metadataScript -Tag $tag -ProjectRoot $fixture.Root } "Reject incorrect tag $tag" 'must match'
    }
    foreach ($version in @('2.7', '2.7.0.0', '2.7.0-rc.1', '02.7.0')) {
        $invalid = New-FakeProject
        Write-FakeProps $invalid.Root $version
        Assert-Throws { & $metadataScript -Tag "v$version" -ProjectRoot $invalid.Root } "Reject unsupported release version $version" 'major.minor.patch'
    }
    foreach ($version in @('65535.0.0', '0.65535.0', '0.0.65535', '999999999999999999999999.0.0')) {
        $invalid = New-FakeProject
        Write-FakeProps $invalid.Root $version
        Assert-Throws { & $metadataScript -ProjectRoot $invalid.Root } "Reject unsupported Windows version component $version" '65534'
    }
    $invalid = New-FakeProject
    Write-FakeProps $invalid.Root '65534.65534.65534'
    $largest = & $metadataScript -ProjectRoot $invalid.Root
    Assert-Check ($largest.WindowsVersion -ceq '65534.65534.65534.0') 'The maximum supported Windows version components remain valid'
    foreach ($property in @('AssemblyVersion', 'FileVersion')) {
        foreach ($value in @('2.7.0.0', '2.6.3.0', '$(Version)', '$(Version).1', '$(OtherVersion).0')) {
            $invalid = New-FakeProject
            if ($property -eq 'AssemblyVersion') { Write-FakeProps $invalid.Root '2.7.0' $value }
            else { Write-FakeProps $invalid.Root '2.7.0' '$(Version).0' $value }
            Assert-Throws { & $metadataScript -ProjectRoot $invalid.Root } "Reject $property value $value that can drift from Version" 'derived from Version'
        }
    }

    $invalid = New-FakeProject
    Remove-Item -LiteralPath $invalid.Notes
    Assert-Throws { & $metadataScript -Tag 'v2.7.0' -ProjectRoot $invalid.Root } 'Tagged builds require their release notes' 'notes missing'
    $untagged = & $metadataScript -ProjectRoot $invalid.Root
    Assert-Check ($untagged.Version -eq '2.7.0' -and $untagged.Assets.Count -eq 0) 'Ordinary CI can validate versions before release notes or downloads exist'

    for ($assetIndex = 0; $assetIndex -lt 4; $assetIndex++) {
        $invalid = New-FakeProject
        Remove-Item -LiteralPath $invalid.Assets[$assetIndex]
        Assert-Throws { & $metadataScript -Tag 'v2.7.0' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } "Reject missing asset $assetIndex" 'missing or empty'
        $invalid = New-FakeProject
        [IO.File]::WriteAllBytes($invalid.Assets[$assetIndex], [byte[]]@())
        Assert-Throws { & $metadataScript -Tag 'v2.7.0' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } "Reject empty asset $assetIndex" 'missing or empty'
    }
    foreach ($assetProperty in @('Installer', 'Source')) {
        $invalid = New-FakeProject
        [IO.File]::AppendAllText($invalid.$assetProperty, 'tampered')
        Assert-Throws { & $metadataScript -Tag 'v2.7.0' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } "Reject tampered $assetProperty bytes" 'SHA-256 mismatch'
    }
    $invalid = New-FakeProject
    Write-FakeChecksum $invalid.Installer 'another-installer.exe'
    Assert-Throws { & $metadataScript -Tag 'v2.7.0' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } 'A valid digest cannot name a different download' 'SHA-256 mismatch'
    [IO.File]::WriteAllText($invalid.Installer + '.sha256', "not-a-sha256  CAD2NC-Setup-2.7.0-x64.exe`n", $utf8)
    Assert-Throws { & $metadataScript -Tag 'v2.7.0' -ArtifactsDirectory $invalid.Artifacts -ProjectRoot $invalid.Root } 'Reject malformed checksum metadata' 'Invalid SHA-256'

    $env:GH_TOKEN = 'offline-test-token'
    $fixture = New-FakeProject
    Reset-Gh
    Invoke-FakePublish $fixture
    Assert-GhOperations 'resolve,view,create,upload,resolve,edit' 'A new release uploads all downloads and verifies the tag before publication'
    Assert-Check $script:ghState.Published 'Successful upload publishes the completed draft'
    $create = ($script:ghState.Calls | Where-Object Operation -EQ 'create').Arguments
    Assert-Check ($create -contains '--draft' -and $create -contains '--verify-tag') 'Creating a release requires an existing tag and begins as a draft'
    $notesIndex = [Array]::IndexOf($create, '--notes-file')
    Assert-Check ($notesIndex -ge 0 -and $create[$notesIndex + 1] -ceq $fixture.Notes) 'The release uses its versioned notes file'
    $upload = ($script:ghState.Calls | Where-Object Operation -EQ 'upload').Arguments
    Assert-Check ($upload -contains '--clobber') 'Interrupted draft uploads can replace incomplete assets'
    foreach ($asset in $fixture.Assets) {
        Assert-Check ($upload -ccontains $asset) "Upload includes verified $([IO.Path]::GetFileName($asset))"
    }
    foreach ($call in $script:ghState.Calls) {
        if ($call.Arguments[0] -ceq 'api') {
            Assert-Check ($call.Arguments[1].StartsWith('repos/test-owner/test-repo/', [StringComparison]::Ordinal)) 'Every API call targets the requested repository'
            Assert-Check (($call.Arguments -join ' ') -cmatch 'v2\.7\.0') 'Every API call targets the requested tag'
        }
        else {
            $repoIndex = [Array]::IndexOf($call.Arguments, '--repo')
            Assert-Check ($repoIndex -ge 0 -and $call.Arguments[$repoIndex + 1] -ceq 'test-owner/test-repo') 'Every GitHub release command targets the requested repository'
            Assert-Check ($call.Arguments -ccontains 'v2.7.0') 'Every GitHub release command targets the requested tag'
        }
    }

    Reset-Gh 'draft'
    Invoke-FakePublish $fixture
    Assert-GhOperations 'resolve,view,upload,resolve,edit' 'A rerun resumes an existing draft'
    Assert-Check $script:ghState.Published 'The resumed draft is published after upload'
    Reset-Gh 'published'
    Invoke-FakePublish $fixture
    Assert-GhOperations 'resolve,view' 'A published release is unchanged on rerun'
    Assert-Check (-not $script:ghState.Published) 'A published release is never republished or overwritten'

    Reset-Gh 'missing' 'view'
    Assert-Throws { Invoke-FakePublish $fixture } 'A failed release lookup cannot be treated as a missing release' 'Reading existing releases failed'
    Assert-GhOperations 'resolve,view' 'An API failure prevents draft creation and all asset changes'

    Reset-Gh 'missing' '' ('b' * 40)
    Assert-Throws { Invoke-FakePublish $fixture } 'A tag resolving to another commit cannot publish these artifacts' 'commit'
    Assert-GhOperations 'resolve' 'A mismatched tag is refused before inspecting or changing releases'
    Assert-Check (-not $script:ghState.Published) 'Mismatched commits never publish'
    Reset-Gh 'missing' '' ''
    Assert-Throws { Invoke-FakePublish $fixture } 'A remote tag must exist before publication' 'tag'
    Assert-GhOperations 'resolve' 'A missing remote tag is refused before inspecting or changing releases'
    Reset-Gh 'missing' 'resolve'
    Assert-Throws { Invoke-FakePublish $fixture } 'A failed remote tag lookup stops publication' 'tag'
    Assert-GhOperations 'resolve' 'A tag API failure never reaches release operations'

    Reset-Gh 'missing' '' $script:expectedCommit ('b' * 40)
    Assert-Throws { Invoke-FakePublish $fixture } 'Moving a tag during upload prevents publication' 'commit'
    Assert-GhOperations 'resolve,view,create,upload,resolve' 'The tag is verified again after uploading assets'
    Assert-Check ($script:ghState.ReleaseState -eq 'draft' -and -not $script:ghState.Published) 'A changed tag leaves the release unpublished'
    Reset-Gh 'draft' '' $script:expectedCommit ('b' * 40)
    Assert-Throws { Invoke-FakePublish $fixture } 'Resuming a draft also rechecks the tag before publication' 'commit'
    Assert-GhOperations 'resolve,view,upload,resolve' 'A resumed draft with a moved tag remains unpublished'

    Reset-Gh 'missing' 'create'
    Assert-Throws { Invoke-FakePublish $fixture } 'A draft creation failure stops publication' 'Creating the draft'
    Assert-GhOperations 'resolve,view,create' 'Creation failure prevents all asset changes'
    Assert-Check (-not $script:ghState.Published) 'Failed creation cannot publish'

    Reset-Gh 'missing' 'upload'
    Assert-Throws { Invoke-FakePublish $fixture } 'An asset upload failure stops publication' 'Uploading release assets'
    Assert-GhOperations 'resolve,view,create,upload' 'Failed upload never reaches publication'
    Assert-Check ($script:ghState.ReleaseState -eq 'draft' -and -not $script:ghState.Published) 'Failed upload leaves a resumable draft'
    $script:ghState.FailOperation = ''
    $script:ghState.Calls.Clear()
    Invoke-FakePublish $fixture
    Assert-GhOperations 'resolve,view,upload,resolve,edit' 'Retry after failed upload resumes the draft without creating another release'
    Assert-Check $script:ghState.Published 'Retry can complete publication after an interrupted upload'

    Reset-Gh 'draft' 'upload'
    Assert-Throws { Invoke-FakePublish $fixture } 'Existing draft upload failures also stop publication' 'Uploading release assets'
    Assert-GhOperations 'resolve,view,upload' 'A failed resumed upload never publishes'
    Reset-Gh 'draft' 'edit'
    Assert-Throws { Invoke-FakePublish $fixture } 'A publish API failure is reported' 'Publishing the completed draft'
    Assert-Check ($script:ghState.ReleaseState -eq 'draft' -and -not $script:ghState.Published) 'A failed publication remains a draft'

    Reset-Gh
    $env:GH_TOKEN = ''
    Assert-Throws { Invoke-FakePublish $fixture } 'Publishing requires a token' 'GH_TOKEN'
    Assert-GhOperations '' 'Missing authentication never invokes GitHub'
    $env:GH_TOKEN = 'offline-test-token'
    foreach ($commit in @('abc123', ('g' * 40), ('a' * 39), ('a' * 41))) {
        Reset-Gh
        Assert-Throws { & $publishScript -Tag 'v2.7.0' -CommitSha $commit -Repository 'test-owner/test-repo' -ArtifactsDirectory $fixture.Artifacts -ProjectRoot $fixture.Root } "Reject invalid build commit $commit" ''
        Assert-GhOperations '' 'Invalid commit identifiers never invoke GitHub'
    }
    Reset-Gh
    Assert-Throws { & $publishScript -Tag 'v2.7.1' -CommitSha $script:expectedCommit -Repository 'test-owner/test-repo' -ArtifactsDirectory $fixture.Artifacts -ProjectRoot $fixture.Root } 'Publication rejects an incorrect tag before contacting GitHub' 'must match'
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
    Assert-Check ($job -match '(?m)^\s+RELEASE_COMMIT:\s*\$\{\{\s*github\.sha\s*\}\}\s*$' -and $job -match '-CommitSha\s+\$env:RELEASE_COMMIT\b') 'Publication verifies the tag against the workflow commit through an environment variable'

    Write-Host "Release workflow tests passed: $script:assertions assertions; no network requests."
}
finally {
    $env:GH_TOKEN = $previousToken
    if ($previousExitCode) { $global:LASTEXITCODE = $previousExitValue }
    else { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
}
