#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [switch]$StaticOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspacePrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'

function Assert-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path, $projectRoot)
    if (-not $resolved.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Test path is outside the workspace: $resolved" }
    return $resolved
}

$installer = Assert-WorkspacePath $InstallerPath
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer not found: $installer" }
$expected = (Get-Content -LiteralPath ($installer + '.sha256') -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
$actual = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or $actual -ne $expected) { throw 'Installer SHA-256 sidecar does not match.' }
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer)
if ($metadata.ProductName.Trim() -ne 'CAD2NC' -or $metadata.ProductVersion.Trim() -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Unexpected installer product metadata.' }
$script = Get-Content -LiteralPath (Join-Path $projectRoot 'installer/CAD2NC.iss') -Raw
foreach ($required in @('(?m)^AppId=CAD2NC\r?$', '(?m)^PrivilegesRequired=lowest\r?$', '(?m)^AppMutex=Local\\CAD2NC\.Gui\.InstallLock\r?$', '(?m)^SetupMutex=Local\\CAD2NC\.Setup\.InstallLock\r?$', '(?m)^CloseApplications=no\r?$', '(?m)^UsePreviousAppDir=yes\r?$', '(?m)^UsePreviousTasks=yes\r?$', '(?m)^UsePreviousGroup=yes\r?$')) {
    if ($script -notmatch $required) { throw "Installer safety contract changed: $required" }
}
if ($script -match '(?im)^\[(Registry|InstallDelete|UninstallDelete)\]') { throw 'Review newly added registry or deletion actions before running installer validation.' }
$iconEntries = @($script -split '\r?\n' | Where-Object { $_ -match '^Name: "\{user(programs|desktop)\}' })
if ($iconEntries.Count -ne 2 -or @($iconEntries | Where-Object { $_ -notmatch 'Check: not WizardNoIcons' }).Count -ne 0) {
    throw 'The no-shortcuts test mode must explicitly skip every user shortcut.'
}
foreach ($required in @('RegKeyExists\(HKCU64, Cad2NcUninstallKey\)', 'RegQueryStringValue\(HKCU64, Cad2NcUninstallKey, ''DisplayVersion''', 'function InitializeSetup: Boolean;', 'function PrepareToInstall\(var NeedsRestart: Boolean\): String;', 'if InstallMode = imDowngrade then Result := DowngradeMessage;', 'SuppressibleMsgBox\(DowngradeMessage', 'PageID = wpSelectTasks', 'GetVersionNumbersString.*CAD2NC\.exe', 'GetVersionNumbersString.*CAD2NC\.dll')) {
    if ($script -notmatch $required) { throw "Installer mode contract changed: $required" }
}
foreach ($required in @('(?s)function InitializeSetup: Boolean;.*?DetectInstallation\(''''\);', 'RestoredDirectory := WizardForm\.PrevAppDir;', 'DetectInstallation\(RestoredDirectory\);', 'if PreviousDirectory <> '''' then begin', '(?s)function PrepareToInstall\(var NeedsRestart: Boolean\): String;.*?DetectInstallation\(WizardDirValue\);')) {
    if ($script -notmatch $required) { throw "Installer directory fallback contract changed: $required" }
}
if ($script -match 'DetectInstallation\(ExpandConstant') { throw 'Startup must not infer an installed version from the default program directory.' }
Write-Host "Static checks passed: product $($metadata.ProductVersion.Trim()), SHA-256, per-user install, GUI/setup locks, no forced close, no data deletion, /NOICONS, and offline installer mode guards."
if ($StaticOnly) { return }

# Do not trust USERNAME, USERPROFILE or LOCALAPPDATA: Codex may expose the
# owner's environment while executing under a sandbox token. Query the real
# token and the actual registry handle before allowing ANY installation.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Cad2NcInstallerRegistryIdentity {
    [DllImport("advapi32.dll")] static extern int RegOpenCurrentUser(int desired, out IntPtr key);
    [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
    [DllImport("ntdll.dll")] static extern int NtQueryKey(IntPtr key, int kind, IntPtr buffer, int length, out int resultLength);
    public static string CurrentUserPath() {
        IntPtr key;
        int error = RegOpenCurrentUser(0x20019, out key);
        if (error != 0) throw new InvalidOperationException("RegOpenCurrentUser failed: " + error);
        try {
            int length;
            NtQueryKey(key, 3, IntPtr.Zero, 0, out length);
            if (length < 4) throw new InvalidOperationException("Cannot resolve HKCU registry identity.");
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try {
                int result = NtQueryKey(key, 3, buffer, length, out length);
                if (result != 0) throw new InvalidOperationException("NtQueryKey failed: " + result);
                return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4), Marshal.ReadInt32(buffer) / 2);
            } finally { Marshal.FreeHGlobal(buffer); }
        } finally { RegCloseKey(key); }
    }
}
'@
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$registryIdentity = [Cad2NcInstallerRegistryIdentity]::CurrentUserPath()
$expectedRegistryIdentity = '\Registry\User\' + $identity.User.Value
if ($identity.Name -notmatch '\\CodexSandbox(Online|Offline)$' -or $registryIdentity -ne $expectedRegistryIdentity) {
    throw "Installation test refused: token=$($identity.Name), HKCU=$registryIdentity. Requires an isolated sandbox account with its own SID hive; no install or registry mutation was performed."
}
$profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([IO.Path]::GetFileName($profile.TrimEnd('\')) -ne ($identity.Name -split '\\')[-1]) {
    throw "Installation test refused: profile $profile does not belong to $($identity.Name). No installation was performed."
}
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CAD2NC_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An installation already exists in this sandbox hive; it will not be changed.' }
$existingGuiLock = $null
if ([Threading.Mutex]::TryOpenExisting('Local\CAD2NC.Gui.InstallLock', [ref]$existingGuiLock)) {
    $existingGuiLock.Dispose()
    throw 'CAD2NC is running in this session; stop it normally before installer validation.'
}
$testRoot = Assert-WorkspacePath (Join-Path $projectRoot ('build/install-smoke-' + [Guid]::NewGuid().ToString('N')))
$installDirectory = Assert-WorkspacePath (Join-Path $testRoot '中文 空格 安装')
$tempDirectory = Assert-WorkspacePath (Join-Path $testRoot 'temp')
New-Item -ItemType Directory -Force -Path $testRoot,$tempDirectory | Out-Null

function Invoke-TestProcess([string]$Executable, [string[]]$Arguments, [switch]$ExpectFailure) {
    $executablePath = Assert-WorkspacePath $Executable
    $start = [Diagnostics.ProcessStartInfo]::new($executablePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = 'Hidden'
    $start.WorkingDirectory = Split-Path -Parent $executablePath
    $start.Environment['TEMP'] = $tempDirectory
    $start.Environment['TMP'] = $tempDirectory
    [void]$start.Environment.Remove('DOTNET_ROOT')
    [void]$start.Environment.Remove('DOTNET_ROOT_X64')
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Installer validation process timed out.' }
    if ($ExpectFailure) {
        if ($process.ExitCode -eq 0) { throw 'Setup unexpectedly succeeded while the GUI mutex was held.' }
    } elseif ($process.ExitCode -ne 0) { throw "Installer validation failed (exit $($process.ExitCode)). Logs: $testRoot" }
}

$installArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=',"/DIR=$installDirectory")
$guiLock = [Threading.Mutex]::new($false, 'Local\CAD2NC.Gui.InstallLock')
try {
    Invoke-TestProcess $installer ($installArguments + "/LOG=$(Join-Path $testRoot 'running-app.log')") -ExpectFailure
    if (Test-Path -LiteralPath (Join-Path $installDirectory 'CAD2NC.exe')) { throw 'The running-app guard modified installation files.' }
} finally { $guiLock.Dispose() }

$uninstaller = Join-Path $installDirectory 'unins000.exe'
try {
    Invoke-TestProcess $installer ($installArguments + "/LOG=$(Join-Path $testRoot 'install.log')")
    $installed = Get-ItemProperty -LiteralPath $uninstallKey
    if ($installed.InstallLocation.TrimEnd('\') -ne $installDirectory -or $installed.DisplayVersion -ne $metadata.ProductVersion.Trim()) { throw 'Unexpected uninstall registration.' }
    # Reinstall the same artifact and retain a user-created file that is not
    # part of the installer payload. All mutations remain in this test tree.
    $userFile = Join-Path $installDirectory '用户保存的任务.cad2nc.json'
    [IO.File]::WriteAllText($userFile, '{"test":"preserve-user-file"}')
    Invoke-TestProcess $installer ($installArguments + "/LOG=$(Join-Path $testRoot 'reinstall.log')")
    if (-not (Test-Path -LiteralPath $userFile)) { throw 'Reinstallation removed a user-created file.' }
    $summary = Join-Path $testRoot 'installed-smoke.json'
    Invoke-TestProcess (Join-Path $installDirectory 'CAD2NC.exe') @('--convert',(Join-Path $projectRoot 'tests/fixtures/smoke_mm.dxf'),'--output',(Join-Path $testRoot 'installed-smoke.nc'),'--summary',$summary,'--accept-import-diagnostics')
    if ((Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json).toolpaths -lt 1) { throw 'Installed executable did not generate toolpaths.' }
    Invoke-TestProcess $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$(Join-Path $testRoot 'uninstall.log')")
    if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath (Join-Path $installDirectory 'CAD2NC.exe')) -or -not (Test-Path -LiteralPath $userFile)) {
        throw 'Uninstall did not remove its registration/files while preserving user-created files.'
    }
    Write-Host "Install, running-app guard, reinstall, installed EXE conversion, and uninstall passed. Logs: $testRoot"
} finally {
    # Cleanup only the installation created above, using its own uninstaller.
    if ((Test-Path -LiteralPath $uninstaller) -and (Test-Path -LiteralPath $uninstallKey)) {
        $remaining = Get-ItemProperty -LiteralPath $uninstallKey
        if ($remaining.InstallLocation.TrimEnd('\') -eq $installDirectory) {
            Invoke-TestProcess $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$(Join-Path $testRoot 'cleanup.log')")
        }
    }
}
