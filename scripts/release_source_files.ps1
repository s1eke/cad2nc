#Requires -Version 7.0
# Shared by the builder and release verifier. Only public source directories belong here.
function Get-ReleaseSourceFiles {
    param([string]$Root = (Split-Path -Parent $PSScriptRoot))
    $excluded = '[\\/](bin|obj|\.vs|__pycache__|TestResults)[\\/]|\.(user|suo|pfx|p12|pem|key|pyc)$|[\\/]\.env(?:\.|$)'
    foreach ($directory in @('src', 'tests', 'LICENSES', 'installer', 'docs', 'scripts', 'examples', '.github')) {
        Get-ChildItem -LiteralPath (Join-Path $Root $directory) -Recurse -File -Force |
            Where-Object FullName -NotMatch $excluded
    }
    foreach ($relative in @('.gitignore', '.gitattributes', 'CAD2NC.slnx', 'Directory.Build.props',
        'global.json', 'NuGet.Config', 'build.ps1', 'README.md', 'LICENSE')) {
        Get-Item -LiteralPath (Join-Path $Root $relative) -Force
    }
}
