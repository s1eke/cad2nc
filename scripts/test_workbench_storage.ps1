#Requires -Version 7.0
<#
.SYNOPSIS
Checks current WinUI task/NC saving and legacy view settings with isolated output folders.
.DESCRIPTION
The default run checks direct save/export, task reopen, collision cancel/overwrite,
task save-picker open/cancel, in-page directory picker cancellation, and immediate
persistence of directory resets without changing task or generated-program state.
All writes stay in a unique workspace QA directory. Existing CAD2NC processes are
left untouched. Only the process created by this script is closed.
.PARAMETER NativePickers
Also attempts native filename editing/submission and selecting a new folder.
These optional steps have NOT passed in the Codex sandbox: the native Edit may
expose no UIA ValuePattern, and direct control text updates can leave the shell's
committed filename unchanged. Use this switch only in an environment where native
picker automation works. Skipped native submission checks are never marked passed.
.NOTES
No global keyboard or mouse input is sent. Use -Dwg to additionally check a DWG
before the bundled DXF. -KeepOpen retains this script's process for deliberate QA
handoff; without it, the exact owned process is closed even if a check fails.
UIA invocation verifies command behavior, not physical mouse hit testing.
#>
[CmdletBinding()]
param(
    [string]$Executable = 'dist/CAD2NC/CAD2NC.exe',
    [string]$Dwg,
    [switch]$NativePickers,
    [switch]$KeepOpen
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$exe = [IO.Path]::GetFullPath($Executable, $workspace)
$qa = Join-Path $workspace ('build/qa-storage-canvas-' + [Guid]::NewGuid().ToString('N'))
$settingsDirectory = Join-Path $qa 'settings'
$taskDirectory = Join-Path $qa '任务'
$ncDirectory = Join-Path $qa 'NC'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$script:ownedProcess = $null
$script:window = $null
$script:checks = [Collections.Generic.List[string]]::new()
$script:pickerCapture = 0
$script:nativeRenameVerified = $false
$script:directorySelectionVerified = $false

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StorageQaNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, string text);
}
'@

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks.Add($Message)
    Write-Output "PASS $Message"
}
function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:ownedProcess -and $script:ownedProcess.HasExited) { throw "CAD2NC exited: $($script:ownedProcess.ExitCode)" }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Message"
}
function Find-Name([string]$Name, $Root = $script:window) {
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name))
}
function Find-Id([string]$Id, $Root = $script:window) {
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Invoke-Element($Element) {
    if ($null -eq $Element) { throw 'Missing UI control' }
    if ($Element.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Refusing to invoke another process UI' }
    $scroll = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) { $scroll.ScrollIntoView() }
    $invoke = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke(); return }
    $toggle = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) { $toggle.Toggle(); return }
    throw 'Control has no invoke or toggle pattern'
}
function Invoke-Name([string]$Name, $Root = $script:window) {
    $element = Find-Name $Name $Root
    if ($null -eq $element) { throw "Missing UI control: $Name" }
    Invoke-Element $element
}
function Invoke-Command([string]$Name) {
    $button = Find-Name $Name
    if ($null -eq $button -or $button.Current.IsOffscreen) {
        $more = Find-Id 'MoreButton'
        if ($null -eq $more) { throw "Command is hidden and overflow button is missing: $Name" }
        Invoke-Element $more
        Wait-Until { $command = Find-Name $Name; $null -ne $command -and -not $command.Current.IsOffscreen -and $command.Current.IsEnabled } "Overflow command $Name"
        $button = Find-Name $Name
    }
    Invoke-Element $button
}
function Invoke-OverflowCommandId([string]$Id) {
    $button = Find-Id $Id
    if ($null -eq $button -or $button.Current.IsOffscreen) {
        $more = Find-Id 'MoreButton'
        if ($null -eq $more) { throw "Command is hidden and overflow button is missing: $Id" }
        Invoke-Element $more
        Wait-Until {
            $command = Find-Id $Id
            $null -ne $command -and -not $command.Current.IsOffscreen -and $command.Current.IsEnabled
        } "Overflow command $Id"
        $button = Find-Id $Id
    }
    Invoke-Element $button
}
function Read-Ui {
    @($script:window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object {
            try { [ordered]@{ name = $_.Current.Name; id = $_.Current.AutomationId; type = $_.Current.ControlType.ProgrammaticName; enabled = $_.Current.IsEnabled; offscreen = $_.Current.IsOffscreen; handle = $_.Current.NativeWindowHandle } }
            catch { [ordered]@{ unavailable = $_.Exception.Message } }
        })
}
function Capture([string]$Name, [IntPtr]$Handle = [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 300
    $script:ownedProcess.Refresh()
    $rect = [StorageQaNative+RECT]::new()
    if ($Handle -eq [IntPtr]::Zero) { $Handle = $script:ownedProcess.MainWindowHandle }
    [void][StorageQaNative]::GetWindowRect($Handle, [ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { [void][StorageQaNative]::PrintWindow($Handle, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc) }
    try { $bitmap.Save((Join-Path $qa $Name)) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Start-OwnedApp([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = 'Hidden'
    $start.WorkingDirectory = $qa
    $start.Environment['CAD2NC_SETTINGS_DIRECTORY'] = $settingsDirectory
    [void]$start.Environment.Remove('DOTNET_ROOT')
    [void]$start.Environment.Remove('DOTNET_ROOT_X64')
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    [Diagnostics.Process]::Start($start)
}
function Open-ThroughActivation([string]$Path) {
    $secondary = Start-OwnedApp @($Path)
    if (-not $secondary.WaitForExit(30000)) {
        $secondary.Kill(); $secondary.WaitForExit()
        throw 'Owned activation process failed to exit'
    }
    Assert-Check ($secondary.ExitCode -eq 0) 'File activation redirects to the owned window'
}
function Wait-Imported([string]$Name) {
    Wait-Until { $null -ne (Find-Name $Name) -and (Find-Id 'GenerateButton').Current.IsEnabled } "Import $Name" 30
}
function Native-Picker {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ownedProcess.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770'))
    $element = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -ne $element) {
        # Re-root at the native HWND instead of using WinUI's projected descendant tree.
        [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$element.Current.NativeWindowHandle)
    }
}
function Fill-NativePicker([string]$Path) {
    Wait-Until { $null -ne (Native-Picker) } 'Native file picker'
    $picker = Native-Picker
    $editCondition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, '1001'),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, 'Edit'))
    $edit = $picker.FindFirst([Windows.Automation.TreeScope]::Descendants, $editCondition)
    if ($null -eq $edit) { $edit = Find-Id '1152' $picker }
    if ($null -eq $edit) { throw 'Native picker filename control is missing' }
    if ($edit.Current.NativeWindowHandle -ne 0) { $edit = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$edit.Current.NativeWindowHandle) }
    if ($edit.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Unexpected filename control owner' }
    $value = $null
    if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value)) { $value.SetValue($Path) }
    elseif ($edit.Current.NativeWindowHandle -ne 0) {
        $set = [StorageQaNative]::SendMessage([IntPtr]$edit.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $Path)
        if ($set -eq [IntPtr]::Zero) { throw 'Native filename WM_SETTEXT failed' }
    }
    else { throw 'Native filename control is not editable' }
    $script:pickerCapture++
    Capture ("native-picker-{0:00}.png" -f $script:pickerCapture) ([IntPtr]$picker.Current.NativeWindowHandle)
    $save = Find-Id '1' $picker
    if ($null -eq $save) { throw 'Native picker confirmation button is missing' }
    # This message is directed to the owned dialog button, never the active desktop window.
    if ($save.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Unexpected picker owner' }
    [void][StorageQaNative]::PostMessage([IntPtr]$save.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { $null -eq (Native-Picker) } 'Native picker closes'
}
function Cancel-NativePicker([string]$ReadyId = 'SaveButton') {
    $picker = Native-Picker
    if ($null -eq $picker) { throw 'No owned native picker to cancel' }
    if ($picker.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Unexpected native picker owner' }
    [void][StorageQaNative]::PostMessage([IntPtr]$picker.Current.NativeWindowHandle, 0x0111, [IntPtr]::new(2), [IntPtr]::Zero)
    Wait-Until { $null -eq (Native-Picker) } 'Native picker cancellation'
    Wait-Until { $ready = Find-Id $ReadyId; $null -ne $ready -and $ready.Current.IsEnabled } 'Controls restored after picker cancellation'
}
function Read-Settings { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json }
function Write-State([string]$Status, [string]$ErrorText = '') {
    [ordered]@{
        status = $Status; error = $ErrorText
        processId = if ($script:ownedProcess) { $script:ownedProcess.Id } else { $null }
        qaDirectory = $qa; executable = $exe; settingsDirectory = $settingsDirectory
        checks = $script:checks.ToArray()
        screenshots = @(Get-ChildItem -LiteralPath $qa -File -Filter '*.png' | Select-Object -ExpandProperty Name)
        nativePickersRequested = $NativePickers.IsPresent
        nativeRenameSubmissionVerified = $script:nativeRenameVerified
        nativeDirectorySelectionVerified = $script:directorySelectionVerified
        physicalMouseHitTestingVerified = $false
        skippedChecks = if (-not $NativePickers) { @('Native filename submission and final normalized-path overwrite', 'Native folder selection and changed-directory persistence') } else { @() }
        nativeAutomationLimitation = 'Native filename and folder submission has not passed in the Codex sandbox; opening/cancelling a picker is a separate check.'
    } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $qa 'result.json') -Encoding utf8
}

if (Get-Process CAD2NC -ErrorAction SilentlyContinue) { throw 'An existing CAD2NC instance is running; it was left untouched.' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable not found: $exe" }
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
# Model an existing v2 profile: legal non-default CAD widths, with no optional
# ncReviewWidth property. The app must preserve the widths and supply the NC default.
$data = [ordered]@{ schemaVersion = 1; lastConfig = @{ tool = @{}; machining = @{} }; lastView = @{ leftWidth = 280; rightWidth = 360 }; theme = 'Dark'; presets = @(); recentTasks = @(); taskDirectory = $taskDirectory; ncDirectory = $ncDirectory }
$data | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settingsPath -Encoding utf8
$dxf = Join-Path $qa '中文 验收板.DXF'
Copy-Item -LiteralPath (Join-Path $workspace 'tests/fixtures/smoke_mm.dxf') -Destination $dxf
$initial = $dxf
if ($Dwg) {
    $initial = Join-Path $qa '中文 异型板.DWG'
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($Dwg, $workspace)) -Destination $initial
}
Write-Output "QA_DIRECTORY $qa"
try {
    $script:ownedProcess = Start-OwnedApp @($initial)
    Wait-Until { $script:ownedProcess.Refresh(); $script:ownedProcess.MainWindowHandle -ne 0 } 'Initial window'
    $script:window = [Windows.Automation.AutomationElement]::FromHandle($script:ownedProcess.MainWindowHandle)
    Wait-Imported ([IO.Path]::GetFileName($initial))
    Assert-Check (-not (Test-Path -LiteralPath $taskDirectory)) 'Import does not create the task output directory'
    Invoke-Element (Find-Id 'SaveButton')
    $initialTask = Join-Path $taskDirectory ([IO.Path]::GetFileNameWithoutExtension($initial) + '.cad2nc.json')
    Wait-Until { Test-Path -LiteralPath $initialTask -PathType Leaf } 'Direct task save'
    Assert-Check ($null -eq (Native-Picker)) 'Direct task save does not open a picker'
    $savedTask = Get-Content -LiteralPath $initialTask -Raw | ConvertFrom-Json
    Assert-Check ($savedTask.schemaVersion -eq 3) 'Saved task uses the current v3 schema'
    Assert-Check ($savedTask.config.machining.prioritizePartStability -eq $true) 'Saved v3 task persists the stability-first default'
    Assert-Check ($savedTask.view.leftWidth -eq 280 -and $savedTask.view.rightWidth -eq 360) 'Existing legal CAD sidebar widths survive the v2.5 layout defaults'
    Capture '01-direct-task.png'
    Open-ThroughActivation $initialTask
    Wait-Imported ([IO.Path]::GetFileName($initial))
    Assert-Check ($null -eq (Find-Name '导入失败')) 'Task reopens its referenced CAD successfully'

    if ($Dwg) {
        Open-ThroughActivation $dxf
        Start-Sleep -Milliseconds 300
        if ($null -ne (Find-Name '保存当前加工任务？')) { Invoke-Name '保存' }
        Wait-Imported ([IO.Path]::GetFileName($dxf))
        Invoke-Element (Find-Id 'SaveButton')
    }
    $dxfTask = Join-Path $taskDirectory '中文 验收板.cad2nc.json'
    Wait-Until { Test-Path -LiteralPath $dxfTask -PathType Leaf } 'DXF task save'
    Invoke-Element (Find-Id 'GenerateButton')
    Wait-Until { (Find-Id 'GenerateButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'NC generation' 30
    Wait-Until { (Find-Id 'ExportButton').Current.IsEnabled -and -not (Find-Id 'ExportButton').Current.IsOffscreen } 'Valid NC can be exported directly from the workbench'
    Assert-Check ((Find-Id 'ExportButton').Current.Name -eq '导出 NC') 'The fixed toolbar uses the 导出 NC command label'
    $savedTitle = (Find-Id 'DocumentTitle').Current.Name
    $savedTaskHash = (Get-FileHash -LiteralPath $dxfTask -Algorithm SHA256).Hash
    $stale = Find-Id 'StalePlanBadge'
    Assert-Check ($null -eq $stale -or $stale.Current.IsOffscreen) 'A newly generated result is current'
    # Results presentation must not save the task or invalidate its output.
    $ncTab = Find-Id 'NcCodeTab'
    if ($null -eq $ncTab -or $ncTab.Current.IsOffscreen) { Invoke-Element (Find-Id 'OpenResultsButton') }
    (Find-Id 'NcCodeTab').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Element (Find-Id 'ExpandResultsButton')
    Wait-Until { $button = Find-Id 'CopyNcButton'; $null -ne $button -and -not $button.Current.IsOffscreen } 'Source large view exposes valid code'
    Invoke-Element (Find-Id 'ExpandResultsButton')
    Wait-Until { $button = Find-Id 'PlaybackButton'; $null -ne $button -and -not $button.Current.IsOffscreen } 'Source large view returns to the preview'
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $savedTitle -and (Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled) 'Source presentation changes preserve saved state and output eligibility'
    Assert-Check ((Get-FileHash -LiteralPath $dxfTask -Algorithm SHA256).Hash -eq $savedTaskHash) 'Source presentation changes do not rewrite the saved task'
    Invoke-Element (Find-Id 'ExportButton')
    $nc = Join-Path $ncDirectory '中文 验收板.nc'
    Wait-Until { Test-Path -LiteralPath $nc -PathType Leaf } 'Direct NC export'
    Assert-Check ($null -eq (Native-Picker)) 'Direct NC export does not open a picker'
    Assert-Check ((Get-Content -LiteralPath $nc -Raw) -match 'G0[01]') 'Direct NC output contains motion commands'
    Capture '02-direct-nc.png'

    [IO.File]::WriteAllText($nc, 'EXISTING_NC_SENTINEL')
    Invoke-Element (Find-Id 'ExportButton')
    Wait-Until { $null -ne (Find-Name '文件已存在') } 'NC collision confirmation'
    Invoke-Name '取消'
    Assert-Check ((Get-Content -LiteralPath $nc -Raw) -eq 'EXISTING_NC_SENTINEL') 'Cancel preserves an existing NC file'
    Invoke-Element (Find-Id 'ExportButton')
    Wait-Until { $null -ne (Find-Name '文件已存在') } 'NC overwrite confirmation'
    Invoke-Name '覆盖'
    Wait-Until { (Get-Content -LiteralPath $nc -Raw) -ne 'EXISTING_NC_SENTINEL' } 'Confirmed NC overwrite'
    Assert-Check $true 'Overwrite replaces only the confirmed NC destination'

    Invoke-Command '加工任务另存为…'
    Wait-Until { $null -ne (Native-Picker) } 'Task save-as native picker opens'
    Capture '03-task-picker-open.png' ([IntPtr](Native-Picker).Current.NativeWindowHandle)
    Assert-Check $true 'Task save-as opens a native picker without the compound-extension error'
    $taskHashBeforeCancel = (Get-FileHash -LiteralPath $dxfTask).Hash
    Cancel-NativePicker
    Assert-Check ((Get-FileHash -LiteralPath $dxfTask).Hash -eq $taskHashBeforeCancel) 'Cancelling the task picker preserves the saved task'

    if ($NativePickers) {
        Invoke-Element (Find-Id 'ExportButton')
        Wait-Until { $null -ne (Find-Name '文件已存在') } 'NC save-as collision choice'
        Invoke-Name '另存为…'
        $alternateNc = Join-Path $qa '另存 程序.nc'
        Fill-NativePicker $alternateNc
        Wait-Until { Test-Path -LiteralPath $alternateNc -PathType Leaf } 'NC native save-as'
        Assert-Check ((Get-Content -LiteralPath $alternateNc -Raw) -match 'G0[01]') 'NC collision offers a working native save-as picker'

        $selectedJson = Join-Path $qa '改名.v2.json'
        $normalizedTask = Join-Path $qa '改名.v2.cad2nc.json'
        [IO.File]::WriteAllText($selectedJson, 'ORIGINAL_JSON_SENTINEL')
        [IO.File]::WriteAllText($normalizedTask, 'FINAL_TASK_SENTINEL')
        Invoke-Command '加工任务另存为…'
        Fill-NativePicker $selectedJson
        Wait-Until { $null -ne (Find-Name '文件已存在') } 'Normalized task collision confirmation'
        Assert-Check ($null -ne (Find-Name $normalizedTask)) 'Overwrite confirmation displays the final compound-suffix path'
        Invoke-Name '取消'
        Assert-Check ((Get-Content -LiteralPath $selectedJson -Raw) -eq 'ORIGINAL_JSON_SENTINEL' -and (Get-Content -LiteralPath $normalizedTask -Raw) -eq 'FINAL_TASK_SENTINEL') 'Cancel preserves both the chosen JSON and normalized task destination'
        Invoke-Command '加工任务另存为…'
        Fill-NativePicker $selectedJson
        Wait-Until { $null -ne (Find-Name '文件已存在') } 'Second normalized task confirmation'
        Invoke-Name '覆盖'
        Wait-Until { (Get-Content -LiteralPath $normalizedTask -Raw) -ne 'FINAL_TASK_SENTINEL' } 'Normalized task overwrite'
        Assert-Check ((Get-Content -LiteralPath $selectedJson -Raw) -eq 'ORIGINAL_JSON_SENTINEL') 'Task save-as leaves the original .json file unchanged'
        $savedAsTask = Get-Content -LiteralPath $normalizedTask -Raw | ConvertFrom-Json
        Assert-Check ($savedAsTask.schemaVersion -eq 3 -and $savedAsTask.config.machining.prioritizePartStability -eq $true) 'Native task save-as writes a valid v3 task with stability ordering'
        $script:nativeRenameVerified = $true
        Capture '03-task-save-as.png'
    }
    else { Write-Output 'SKIP Native filename submission and normalized-target overwrite: use -NativePickers only where native automation is supported.' }

    $titleBeforeSettings = (Find-Id 'DocumentTitle').Current.Name
    Invoke-OverflowCommandId 'SettingsMenuButton'
    Wait-Until {
        $settings = Find-Id 'SettingsTaskDirectory'
        $back = Find-Id 'SettingsBackButton'
        $null -ne $settings -and -not $settings.Current.IsOffscreen -and $null -ne $back -and -not $back.Current.IsOffscreen
    } 'Settings page'
    Assert-Check ((Find-Id 'SettingsTaskDirectory').Current.Name -eq $taskDirectory -and (Find-Id 'SettingsNcDirectory').Current.Name -eq $ncDirectory) 'Settings displays both complete output directory paths'
    Capture '04-directory-settings.png'
    foreach ($name in @('更改默认任务文件夹', '更改默认 NC 文件夹')) {
        Invoke-Name $name
        Wait-Until { $null -ne (Native-Picker) } "Directory picker opens: $name"
        Cancel-NativePicker 'SettingsTheme'
        Assert-Check (-not (Find-Id 'SettingsBackButton').Current.IsOffscreen -and -not (Find-Id 'SettingsTaskDirectory').Current.IsOffscreen) 'Cancelling a directory picker keeps the settings page selected'
        Assert-Check ((Read-Settings).taskDirectory -eq $taskDirectory -and (Read-Settings).ncDirectory -eq $ncDirectory) 'Cancelling a directory picker preserves both directory preferences'
    }
    if ($NativePickers) {
        $changedTaskDirectory = Join-Path $qa '更改后任务'
        New-Item -ItemType Directory -Path $changedTaskDirectory -Force | Out-Null
        Invoke-Name '更改默认任务文件夹'
        Fill-NativePicker $changedTaskDirectory
        Wait-Until { (Read-Settings).taskDirectory -eq $changedTaskDirectory } 'Directory update persisted from UI'
        Assert-Check ((Read-Settings).ncDirectory -eq $ncDirectory) 'Changing task directory preserves the NC directory'
        $script:directorySelectionVerified = $true
    }
    else { Write-Output 'SKIP Native folder selection: persistence logic is covered separately by unit tests.' }
    Invoke-Name '恢复默认任务文件夹'
    Wait-Until { $null -eq (Read-Settings).taskDirectory } 'Task directory reset persisted from UI'
    Invoke-Name '恢复默认 NC 文件夹'
    Wait-Until { $null -eq (Read-Settings).ncDirectory } 'NC directory reset persisted from UI'
    # Saving a task does not rewrite the settings file. The first explicit
    # settings save is the point where the optional legacy default is persisted.
    Assert-Check ((Read-Settings).ncReviewWidth -eq 480) 'Saving legacy settings persists the 480 DIP NC reading-width default'
    Assert-Check $true 'Settings resets persist without writing into the host Documents folders'
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $titleBeforeSettings) 'Directory changes preserve the task saved state'
    Capture '05-directory-reset.png'
    Invoke-Element (Find-Id 'SettingsBackButton')
    Wait-Until { -not (Find-Id 'WorkbenchContextBar').Current.IsOffscreen -and -not (Find-Id 'ExportButton').Current.IsOffscreen } 'Return to workbench'
    Assert-Check ((Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled) 'Directory changes retain the valid NC result and playback'
    Write-State 'passed'
    Write-Output "STORAGE_UI_CHECK_COMPLETE processId=$($script:ownedProcess.Id) qa=$qa"
}
catch {
    if ($script:window) {
        try { Capture 'failure.png'; Read-Ui | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qa 'failure-ui.json') -Encoding utf8 } catch { }
    }
    Write-State 'failed' $_.Exception.Message
    throw
}
finally {
    if (-not $KeepOpen -and $script:ownedProcess -and -not $script:ownedProcess.HasExited) {
        # Only terminate the exact process created by this test, never other CAD2NC instances.
        $script:ownedProcess.Kill(); $script:ownedProcess.WaitForExit()
    }
}
