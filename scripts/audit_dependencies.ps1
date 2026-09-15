#Requires -Version 7.0
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Official audit source is explicit in NuGet.Config. Never substitute the development bridge.
# NU1900/NU1905 make unavailable audit data a failure; NU1901-4 cover every severity.
Push-Location $root
try {
    & $Dotnet restore CAD2NC.slnx --locked-mode --force --no-http-cache --configfile NuGet.Config `
        '-p:NuGetAudit=true' '-p:NuGetAuditMode=all' '-p:NuGetAuditLevel=low' `
        '-p:WarningsAsErrors=NU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904%3BNU1905'
    if ($LASTEXITCODE -ne 0) { throw "Dependency audit failed (exit $LASTEXITCODE). See the NuGet diagnostics above; dependencies were not upgraded." }
}
finally { Pop-Location }
