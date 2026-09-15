#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $projectRoot '.tools/inno-setup/7.1.0/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Prepare the pinned Inno Setup compiler before testing installer modes.' }
$installerScript = Get-Content -LiteralPath (Join-Path $projectRoot 'installer/CAD2NC.iss') -Raw
$logicMatch = [regex]::Match($installerScript, '(?s)// BEGIN INSTALLER MODE LOGIC\r?\n(.*?)// END INSTALLER MODE LOGIC')
if (-not $logicMatch.Success) { throw 'Cannot locate the production installer mode functions.' }

# Compile the actual Pascal functions, not a second implementation of the rules.
# This harness has no files, icons, registry entries or uninstaller. Its only
# entry point writes the result inside build/ and always refuses installation.
$testRoot = Join-Path $projectRoot ('build/installer-modes-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$resultPath = Join-Path $testRoot 'result.txt'
$resultLiteral = $resultPath.Replace("'", "''")
$header = @'
[Setup]
AppName=CAD2NC installer logic tests
AppVersion=1.0
DefaultDirName={tmp}\CAD2NC-NoInstallation
PrivilegesRequired=lowest
SetupArchitecture=x64
CreateUninstallRegKey=no
Uninstallable=no
DisableStartupPrompt=yes
OutputBaseFilename=installer-mode-tests
[Code]
'@
$checks = @'
var
  TestCount: Integer;

procedure Require(const Condition: Boolean; const Description: String);
begin
  if not Condition then RaiseException(Description);
  TestCount := TestCount + 1;
end;

function SyntheticVersionAtDirectory(const Directory: String): String;
begin
  Result := '';
  if Directory = 'D:\Previous CAD2NC' then Result := '2.2.0'
  else if Directory = 'C:\Default CAD2NC' then Result := '2.4.0';
end;

procedure RunTests;
var
  Version: Int64;
begin
  Require(ResolveInstallMode(False, '', '2.3.0') = imInstall, 'Fresh installation');
  Require(ResolveInstallMode(False, '9.0', '2.3.0') = imInstall, 'Unregistered version is not an installation');
  Require(ResolveInstallMode(True, '2.2.0', '2.3.0') = imUpdate, 'Upgrade');
  Require(ResolveInstallMode(True, '2.3.0', '2.3.0') = imReinstall, 'Reinstall');
  Require(ResolveInstallMode(True, '2.3.0.0', '2.3.0') = imReinstall, 'Missing version components are zero');
  Require(ResolveInstallMode(True, ' 2.3.0 ', '2.3.0') = imReinstall, 'Trim registry version');
  Require(ResolveInstallMode(True, '2.4.0', '2.3.0') = imDowngrade, 'Refuse downgrade');
  Require(ResolveInstallMode(True, '2.10.0', '2.9.0') = imDowngrade, 'Numeric rather than text ordering');
  Require(ResolveInstallMode(True, '2.9.0', '2.10.0') = imUpdate, 'Numeric upgrade ordering');
  Require(ResolveInstallMode(True, '2.3.0.1', '2.3.0') = imDowngrade, 'Fourth component matters');
  Require(ResolveInstallMode(True, '', '2.3.0') = imRepairUnknown, 'Missing version permits repair');
  Require(ResolveInstallMode(True, 'broken', '2.3.0') = imRepairUnknown, 'Invalid version permits repair');
  Require(SelectInstalledVersion('2.4.0', '2.2.0', '2.1.0') = '2.4.0', 'Registry version takes priority');
  Require(SelectInstalledVersion('2.3.0', '', '') = '2.3.0', 'Missing binaries do not erase registered version');
  Require(SelectInstalledVersion('broken', '2.3.0.0', '2.2.0.0') = '2.3.0.0', 'EXE fallback');
  Require(SelectInstalledVersion('', '', '2.3.0.0') = '2.3.0.0', 'Missing EXE uses DLL');
  Require(SelectInstalledVersion('broken', 'broken', '2.3.0.0') = '2.3.0.0', 'Damaged EXE uses DLL');
  Require(SelectInstalledVersion('broken', '', '') = '', 'Unknown version remains unknown');
  Require(ResolveInstallMode(True, SelectInstalledVersion('', '2.4.0', ''), '2.3.0') = imDowngrade, 'EXE fallback prevents downgrade');
  Require(ResolveInstallMode(True, SelectInstalledVersion('', '', '2.4.0'), '2.3.0') = imDowngrade, 'DLL fallback prevents downgrade');
  Require(TryParseInstallVersion('2', Version), 'Single numeric component');
  Require(TryParseInstallVersion('65535.65535.65535.65535', Version), 'Maximum numeric components');
  Require(not TryParseInstallVersion('65536.1.1', Version), 'Reject overflowing component');
  Require(not TryParseInstallVersion('999999999999999999.1', Version), 'Reject integer overflow');
  Require(not TryParseInstallVersion('2.3.0.0.1', Version), 'Reject extra component');
  Require(not TryParseInstallVersion('2..3', Version), 'Reject missing component');
  Require(not TryParseInstallVersion('.2.3', Version), 'Reject leading dot');
  Require(not TryParseInstallVersion('2.3.', Version), 'Reject trailing dot');
  Require(not TryParseInstallVersion('2.3-preview', Version), 'Reject nonnumeric suffix');
  Require(not TryParseInstallVersion('-2.3', Version), 'Reject negative component');
  Require(not TryParseInstallVersion('2. 3', Version), 'Reject inner whitespace');
  Require(ResolveVersionProbeDirectory('', '') = '', 'Unknown startup directory remains unknown');
  Require(ResolveVersionProbeDirectory('D:\Previous CAD2NC', '') = 'D:\Previous CAD2NC', 'Startup probes an explicitly registered location');
  Require(ResolveVersionProbeDirectory('', 'D:\Previous CAD2NC') = 'D:\Previous CAD2NC', 'Missing InstallLocation uses the restored directory');
  Require(ResolveVersionProbeDirectory('D:\Previous CAD2NC', 'C:\Default CAD2NC') = 'D:\Previous CAD2NC', 'Registered location takes priority');
  Require(ResolveInstallMode(True, SyntheticVersionAtDirectory(ResolveVersionProbeDirectory('', '')), '2.3.0') = imRepairUnknown,
    'Startup does not mistake a newer default-directory leftover for the old installation');
  Require(ResolveInstallMode(True, SyntheticVersionAtDirectory(ResolveVersionProbeDirectory('', 'D:\Previous CAD2NC')), '2.3.0') = imUpdate,
    'Restored custom directory permits the actual upgrade');
  Require(ResolveInstallMode(True, SyntheticVersionAtDirectory(ResolveVersionProbeDirectory('', 'C:\Default CAD2NC')), '2.3.0') = imDowngrade,
    'A genuinely restored newer installation still rejects downgrade');
end;

function InitializeSetup: Boolean;
begin
  Result := False;
  try
    RunTests;
    SaveStringToFile('__RESULT_PATH__', 'PASS ' + IntToStr(TestCount), False);
  except
    SaveStringToFile('__RESULT_PATH__', 'FAIL ' + GetExceptionMessage, False);
  end;
end;
'@
$harness = $header + "`r`n" + $logicMatch.Groups[1].Value + "`r`n" + $checks.Replace('__RESULT_PATH__', $resultLiteral)
$harnessPath = Join-Path $testRoot 'installer-mode-tests.iss'
[IO.File]::WriteAllText($harnessPath, $harness, [Text.UTF8Encoding]::new($true))
& $compiler '/Qp' "/O$testRoot" $harnessPath
if ($LASTEXITCODE -ne 0) { throw 'Installer mode test harness did not compile.' }
$testExecutable = Join-Path $testRoot 'installer-mode-tests.exe'
$start = [Diagnostics.ProcessStartInfo]::new($testExecutable)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.WorkingDirectory = $testRoot
foreach ($argument in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=$(Join-Path $testRoot 'logic.log')")) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Installer logic test timed out.' }
# InitializeSetup=False intentionally returns a nonzero exit code. Assertions
# report separately; no route in this harness may reach the installation stage.
if ($process.ExitCode -eq 0) { throw 'Logic harness unexpectedly entered the installation workflow.' }
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw "Installer logic test produced no result. See $testRoot" }
$result = Get-Content -LiteralPath $resultPath -Raw
if ($result -notmatch '^PASS 38$') { throw "Installer logic tests: $result" }
Write-Host "Installer Pascal logic: $result. No application installation or registry mutation. Artifacts: $testRoot"
