#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CommitSha,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'release_metadata.ps1') -Tag $Tag -ArtifactsDirectory $ArtifactsDirectory -ProjectRoot $ProjectRoot
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is required to publish a release.' }

function Assert-ReleaseCommit {
    $remoteCommit = & gh api "repos/$Repository/commits/$Tag" --jq .sha
    if ($LASTEXITCODE -ne 0) { throw 'Resolving the remote release tag failed.' }
    if (($remoteCommit | Out-String).Trim() -ine $CommitSha) {
        throw 'Release tag no longer matches the tested commit; no release will be published.'
    }
}

Assert-ReleaseCommit
$query = '.[] | select(.tag_name == "' + $Tag + '") | {isDraft: .draft, url: .html_url}'
$existing = & gh api "repos/$Repository/releases" --paginate --jq $query
if ($LASTEXITCODE -ne 0) { throw 'Reading existing releases failed.' }
if (-not [string]::IsNullOrWhiteSpace(($existing | Out-String))) {
    $state = $existing | ConvertFrom-Json
    if (-not $state.isDraft) {
        Write-Host "Release already published; keeping existing downloads: $($state.url)"
        return
    }
}
else {
    & gh release create $Tag --repo $Repository --draft --verify-tag --title "CAD2NC $($release.Version)" --notes-file $release.NotesPath
    if ($LASTEXITCODE -ne 0) { throw 'Creating the draft release failed.' }
}

# Only drafts are changed. A failed upload leaves a resumable draft, never a
# published release with an incomplete set of downloads.
& gh release upload $Tag @($release.Assets) --repo $Repository --clobber
if ($LASTEXITCODE -ne 0) { throw 'Uploading release assets failed; the release remains a draft.' }
Assert-ReleaseCommit
& gh release edit $Tag --repo $Repository --draft=false
if ($LASTEXITCODE -ne 0) { throw 'Publishing the completed draft release failed.' }
