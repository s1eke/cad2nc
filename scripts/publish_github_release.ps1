#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'release_metadata.ps1') -Tag $Tag -ArtifactsDirectory $ArtifactsDirectory -ProjectRoot $ProjectRoot
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is required to publish a release.' }

$existing = & gh release view $Tag --repo $Repository --json isDraft,url 2>$null
if ($LASTEXITCODE -eq 0) {
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
& gh release edit $Tag --repo $Repository --draft=false
if ($LASTEXITCODE -ne 0) { throw 'Publishing the completed draft release failed.' }
