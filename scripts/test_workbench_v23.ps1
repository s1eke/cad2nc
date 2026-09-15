#Requires -Version 7.0
<#
.SYNOPSIS
HISTORICAL v2.5 tool: not a v2.6.4 acceptance suite. See docs/DEVELOPMENT.md.
Checks the v2.5 operator workflow using one owned CAD2NC process and an isolated profile.
.DESCRIPTION
Uses the supplied real DWG/DXF, copies it into a unique workspace QA directory, and
checks single-page navigation, fixed startup defaults, separate T/H controls,
immediate export of valid results, independent results and parameter panels,
guidance, invalidation, settings actions, NC viewing, and responsive theme layouts.
All application saves use QA-specific task/NC directories. This script never resets
those directories, submits a native file picker, copies NC to the clipboard, sends
global mouse/keyboard input, or interacts with an existing CAD2NC process.
Screenshots are evidence for visual review, not an automatic assertion of layout quality
or physical mouse hit testing. Requested sizes are client DIP, including exact breakpoints.
Native configuration pickers are opened and cancelled only when owned by this process.
.PARAMETER Part
All runs the complete workflow, including writing a generated NC into the QA directory.
Dialogs skips workflow assertions and runs settings parameter pickers and layout captures.
NcPreview runs only external arc/helix and single-point Z playback checks, without a drawing.
#>
[CmdletBinding()]
param(
    [string]$Executable = 'dist/CAD2NC/CAD2NC.exe',
    [string]$Drawing,
    [ValidateSet('All', 'Dialogs', 'NcPreview')][string]$Part = 'All'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$exe = [IO.Path]::GetFullPath($Executable, $workspace)
if ($Part -ne 'NcPreview' -and [string]::IsNullOrWhiteSpace($Drawing)) { throw 'Supply -Drawing <DWG/DXF> for this historical v2.5 test; NcPreview does not require a drawing.' }
$sourceDrawing = if ($Part -eq 'NcPreview') { '' } else { [IO.Path]::GetFullPath($Drawing, $workspace) }
if (Get-Process CAD2NC -ErrorAction SilentlyContinue) { throw 'An existing CAD2NC process is running; it will not be interrupted.' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable does not exist: $exe" }
if ($Part -ne 'NcPreview') {
    if (-not (Test-Path -LiteralPath $sourceDrawing -PathType Leaf)) { throw "Drawing does not exist: $sourceDrawing" }
    if ([IO.Path]::GetExtension($sourceDrawing).ToLowerInvariant() -notin @('.dwg', '.dxf')) { throw 'Drawing must be DWG or DXF.' }
}

$qa = [IO.Path]::GetFullPath((Join-Path $workspace ('build/qa-workbench-canvas-' + [Guid]::NewGuid().ToString('N'))))
if (-not $qa.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'QA directory escaped the workspace.' }
$settingsDirectory = Join-Path $qa 'profile'
$taskDirectory = Join-Path $qa '任务'
$ncDirectory = Join-Path $qa 'NC'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$drawingPath = Join-Path $qa ('实际验收图纸' + [IO.Path]::GetExtension($sourceDrawing))
$taskPath = Join-Path $taskDirectory '实际验收图纸.cad2nc.json'
$ncPath = Join-Path $ncDirectory '实际验收图纸.nc'
$savedNcHash = $null
$script:ownedProcess = $null
$script:window = $null
$script:checks = [Collections.Generic.List[string]]::new()
$script:captures = [Collections.Generic.List[string]]::new()
$script:measurements = [Collections.Generic.List[object]]::new()
$sourceHash = if ($Part -ne 'NcPreview') { (Get-FileHash -LiteralPath $sourceDrawing -Algorithm SHA256).Hash } else { $null }

New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
if ($Part -ne 'NcPreview') { Copy-Item -LiteralPath $sourceDrawing -Destination $drawingPath }
# These valid non-default values must not become the new session's machining values.
$settings = @{
    schemaVersion = 1
    lastConfig = @{
        tool = @{ toolNumber = 9; lengthOffset = 11; toolType = 'QA previous tool'; diameter = 12; spindleDirection = 'Clockwise' }
        machining = @{ spindleSpeed = 12000; cuttingFeed = 7777; cuttingDepth = -7; cutDirection = 'Clockwise'; edgeAllowance = 2; safeZ = 25; plungeFeed = 500; maxStepDown = 2 }
    }
    lastView = @{}
    theme = 'Dark'
    presets = @()
    recentTasks = @()
    taskDirectory = $taskDirectory
    ncDirectory = $ncDirectory
}
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding utf8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
if (-not ('WorkbenchV23QaNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WorkbenchV23QaNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder value, int capacity);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
'@
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks.Add($Message)
    Write-Output "PASS $Message"
}
function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:ownedProcess -and $script:ownedProcess.HasExited) { throw "Owned CAD2NC exited: $($script:ownedProcess.ExitCode)" }
        try { if (& $Condition) { return } }
        catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Message"
}
function Assert-Owned($Element) {
    if ($null -eq $Element) { throw 'Missing UI control.' }
    if ($Element.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Refusing to operate another process UI.' }
}
function Find-Property($Property, [string]$Value, $Root = $script:window) {
    $condition = [Windows.Automation.PropertyCondition]::new($Property, $Value)
    $found = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $found -and $Root -eq $script:window) {
        # WinUI popups may appear outside the main HWND's descendant tree.
        $ownedCondition = [Windows.Automation.AndCondition]::new($condition,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ownedProcess.Id))
        $found = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $ownedCondition)
    }
    return $found
}
function Find-Id([string]$Id, $Root = $script:window) { Find-Property ([Windows.Automation.AutomationElement]::AutomationIdProperty) $Id $Root }
function Find-Name([string]$Name, $Root = $script:window) { Find-Property ([Windows.Automation.AutomationElement]::NameProperty) $Name $Root }
function Bring-IntoView($Element) {
    Assert-Owned $Element
    $scrollItem = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scrollItem)) { $scrollItem.ScrollIntoView() }
}
function Invoke-Element($Element) {
    Assert-Owned $Element
    if (-not $Element.Current.IsEnabled) { throw "Control is disabled: $($Element.Current.AutomationId) $($Element.Current.Name)" }
    Bring-IntoView $Element
    $invoke = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke(); return }
    $toggle = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) { $toggle.Toggle(); return }
    throw "Control has no supported invoke pattern: $($Element.Current.Name)"
}
function Invoke-Id([string]$Id) {
    Wait-Until { $element = Find-Id $Id; $null -ne $element -and $element.Current.IsEnabled } "Control $Id ready"
    Invoke-Element (Find-Id $Id)
}
function Invoke-Name([string]$Name) { Invoke-Element (Find-Name $Name) }
function Find-ContentDialog([string]$Title) {
    $condition = [Windows.Automation.AndCondition]::new([Windows.Automation.Condition[]]@(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Title),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ownedProcess.Id)))
    $dialog = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $dialog) {
        $dialog = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    }
    return $dialog
}
function Close-ContentDialog([string]$Title, [string]$ButtonName) {
    # WinUI exposes these dialogs as named Window controls with a CloseButton.
    # Scope by title, control type, and exact PID, never the main window's Close button.
    $dialog = Find-ContentDialog $Title
    Assert-Owned $dialog
    $button = Find-Id 'CloseButton' $dialog
    if ($null -eq $button) { $button = Find-Name $ButtonName $dialog }
    Invoke-Element $button
    Wait-Until { $null -eq (Find-ContentDialog $Title) } "Content dialog closes: $Title"
}
function Toggle-State([string]$Id) {
    $element = Find-Id $Id; Assert-Owned $element
    return $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
}
function Is-Visible([string]$Id) {
    $element = Find-Id $Id
    return $null -ne $element -and -not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty
}
function Invoke-OverflowId([string]$Id) {
    $entry = Find-Id $Id
    if ($null -eq $entry -or $entry.Current.IsOffscreen) {
        Invoke-Id 'MoreButton'
        Wait-Until { $item = Find-Id $Id; $null -ne $item -and $item.Current.IsEnabled -and -not $item.Current.IsOffscreen } "Overflow item $Id appears"
    }
    Invoke-Id $Id
}
function Open-Guide {
    Invoke-OverflowId 'GuideMenuButton'
    Wait-Until { Is-Visible 'GuideBackButton' } 'Guidance page opens'
}
function Open-Settings {
    Invoke-OverflowId 'SettingsMenuButton'
    Wait-Until { Is-Visible 'SettingsBackButton' } 'Settings page opens'
}
function Ensure-Workbench {
    if (Is-Visible 'GuideBackButton') { Invoke-Id 'GuideBackButton' }
    elseif (Is-Visible 'SettingsBackButton') { Invoke-Id 'SettingsBackButton' }
    Wait-Until { Is-Visible 'WorkbenchContextBar' } 'Workbench is visible'
    Start-Sleep -Milliseconds 200
}
function Ensure-Settings {
    if (-not (Is-Visible 'SettingsBackButton')) { Ensure-Workbench; Open-Settings }
}
function Expand-Id([string]$Id) {
    $element = Find-Id $Id; Assert-Owned $element
    $pattern = $element.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($pattern.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { $pattern.Expand() }
    Wait-Until { $current = Find-Id $Id; $null -ne $current -and $current.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded } "$Id expands"
}
function Number-Provider([string]$Id) {
    $element = Find-Id $Id; Assert-Owned $element
    $candidates = @($element) + @($element.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
    foreach ($candidate in $candidates) {
        $pattern = $null
        if ($candidate.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$pattern)) { return @{ Kind = 'Range'; Pattern = $pattern; Element = $candidate } }
        if ($candidate.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return @{ Kind = 'Text'; Pattern = $pattern; Element = $candidate } }
    }
    throw "Number control has no readable UIA value: $Id"
}
function Read-Number([string]$Id) {
    $provider = Number-Provider $Id
    if ($provider.Kind -eq 'Range') { return [double]$provider.Pattern.Current.Value }
    $text = $provider.Pattern.Current.Value
    $value = 0.0
    if ([double]::TryParse($text, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::CurrentCulture, [ref]$value)) { return $value }
    return [double]::Parse($text, [Globalization.CultureInfo]::InvariantCulture)
}
function Set-Number([string]$Id, [double]$Value, [double]$ExpectedValue = [double]::NaN) {
    $provider = Number-Provider $Id; Assert-Owned $provider.Element; Bring-IntoView $provider.Element
    if ($provider.Pattern.Current.IsReadOnly) { throw "Number control is read-only: $Id" }
    if ($provider.Kind -eq 'Range') { $provider.Pattern.SetValue($Value) }
    else {
        $provider.Pattern.SetValue($Value.ToString([Globalization.CultureInfo]::CurrentCulture))
        # Leaving the owned NumberBox commits its text without global keyboard input.
        $focusTarget = Find-Id 'ToolTypeBox'; Assert-Owned $focusTarget; $focusTarget.SetFocus()
    }
    $expected = if ([double]::IsNaN($ExpectedValue)) { $Value } else { $ExpectedValue }
    Wait-Until { [Math]::Abs((Read-Number $Id) - $expected) -lt 0.000001 } "Set $Id to $expected"
    Start-Sleep -Milliseconds 250
}
function Confirmations {
    @($script:window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::CheckBox)) |
        Where-Object { $_.Current.AutomationId.StartsWith('inspect.confirm.', [StringComparison]::Ordinal) })
}
function Go-Parameters {
    Ensure-Workbench
    $close = Find-Name '收起参数面板'
    if ($null -eq $close -or $close.Current.IsOffscreen) {
        if (Is-Visible 'CompactNavigation') { Invoke-Id 'ParametersPanelButton' }
        else { Invoke-Id 'ShowRightButton' }
    }
    $box = Find-Id 'ToolNumberBox'
    if ($null -ne $box) { Bring-IntoView $box }
    Wait-Until { $button = Find-Name '收起参数面板'; $null -ne $button -and -not $button.Current.IsOffscreen } 'Machining parameter panel is visible'
}
function Select-ResultTab([string]$Name) {
    $tab = if ($Name -eq 'NC 代码') { Find-Id 'NcCodeTab' } else { Find-Name $Name }
    Assert-Owned $tab
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 200
}
function Assert-NcSourceSelected([string]$Context) {
    $tab = Find-Id 'NcCodeTab'; Assert-Owned $tab
    Assert-Check ($tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) "$Context defaults to the source tab"
    Assert-Check (Is-Visible 'CopyNcButton') "$Context exposes the source commands"
}
function Assert-NcReadingLayout([int]$WidthDip) {
    Resize-Owned $WidthDip 900
    if ($WidthDip -lt 900) {
        Assert-Check (Is-Visible 'CompactNavigation') "$WidthDip DIP NC exposes compact navigation"
        Invoke-Id 'ResultsPanelButton'
        Wait-Until { Is-Visible 'CopyNcButton' } 'Compact NC details opens'
        Assert-Check (-not (Is-Visible 'PlaybackButton')) "$WidthDip DIP NC program details replaces the preview"
        Invoke-Id 'PreviewPanelButton'
        Wait-Until { Is-Visible 'PlaybackButton' } 'Compact NC preview returns'
        Assert-Check (-not (Is-Visible 'CopyNcButton')) "$WidthDip DIP NC preview hides source details"
    }
    else {
        Assert-Check (-not (Is-Visible 'CompactNavigation') -and (Is-Visible 'PlaybackButton') -and (Is-Visible 'CopyNcButton')) "$WidthDip DIP NC displays preview and source together"
        $source = Find-Id 'ResultTabs'; Assert-Owned $source
        $playback = Find-Id 'PlaybackButton'; Assert-Owned $playback
        Assert-Check ($source.Current.BoundingRectangle.Left -gt $playback.Current.BoundingRectangle.Right) "$WidthDip DIP NC source sits to the right of the preview"
        Assert-Check (Is-Visible 'NcResize') "$WidthDip DIP NC exposes its independent reading-width divider"
    }
}
function Set-ContourQuery([string]$Query) {
    $search = Find-Id 'ContourSearch'; Assert-Owned $search
    $candidates = @($search) + @($search.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
    foreach ($candidate in $candidates) {
        $value = $null
        if ($candidate.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value) -and -not $value.Current.IsReadOnly) {
            Assert-Owned $candidate; $value.SetValue($Query)
            # AutoSuggestBox.TextChanged filters immediately. ListView itself
            # is not focusable while its items are being replaced.
            Start-Sleep -Milliseconds 250
            return
        }
    }
    throw 'Contour search has no writable value pattern.'
}
function Contour-SelectionProvider([string]$Id) {
    $label = Find-Name $Id (Find-Id 'ContourList'); Assert-Owned $label
    $selection = $null
    while (-not $label.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        $label = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($label); Assert-Owned $label
    }
    return $selection
}
function Clear-ContourSelection {
    $list = Find-Id 'ContourList'; Assert-Owned $list
    foreach ($selected in @($list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())) {
        Assert-Owned $selected
        $selected.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).RemoveFromSelection()
    }
}
function Test-ContourSelectionAndBatch {
    $baseline = Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    $rows = @($baseline.contours)
    Assert-Check ($rows.Count -ge 3) 'Contour interaction fixture has enough rows to test selected and unaffected contours'
    $originalTitle = (Find-Id 'DocumentTitle').Current.Name
    $originalIncluded = (Find-Id 'SelectionSummary').Current.Name
    Clear-ContourSelection
    Wait-Until {
        $outside = Find-Id 'SelectedOutsideButton'; $inside = Find-Id 'SelectedInsideButton'
        ($null -eq $outside -or -not $outside.Current.IsEnabled) -and ($null -eq $inside -or -not $inside.Current.IsEnabled)
    } 'No selection leaves both machining-side commands unavailable'
    Set-ContourQuery $rows[0].id
    Wait-Until { (Find-Id 'ContourList').FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)).Count -eq 1 } 'Searching an exact contour identifier filters the list to one row'
    (Contour-SelectionProvider $rows[0].id).Select()
    Wait-Until { (Find-Id 'SelectedInsideButton').Current.IsEnabled } 'A filtered contour can be selected for side editing'
    Assert-Check ((Find-Id 'SelectionSummary').Current.Name -eq $originalIncluded -and (Find-Id 'DocumentTitle').Current.Name -eq $originalTitle) 'Filtering and inspecting a contour does not change machining inclusion or saved state'
    Set-ContourQuery ''
    (Contour-SelectionProvider $rows[0].id).Select()
    (Contour-SelectionProvider $rows[1].id).AddToSelection()
    Wait-Until { (Find-Id 'ContourList').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count -eq 2 } 'Multiple contours remain selectable after filtering'
    Assert-Check ((Find-Id 'ContourSelectionSummary').Current.Name -match '2') 'Selected-side detail reports the two selected contours'
    Assert-Check ((Find-Id 'SelectionSummary').Current.Name -eq $originalIncluded -and (Find-Id 'DocumentTitle').Current.Name -eq $originalTitle) 'Multiple row selection leaves machining inclusion and saved state intact'
    $targetSide = if ($rows[0].side -eq 'Outside') { 'Inside' } else { 'Outside' }
    $command = if ($targetSide -eq 'Inside') { 'SelectedInsideButton' } else { 'SelectedOutsideButton' }
    Invoke-Id $command
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -match '未保存' } 'Selected-side editing is a machining change'
    Invoke-Id 'SaveButton'
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'Selected-side change saves to the isolated task'
    $changed = Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    foreach ($row in $rows) {
        $actual = @($changed.contours | Where-Object id -eq $row.id)[0]
        $expectedSide = if ($row.id -in @($rows[0].id, $rows[1].id)) { $targetSide } else { $row.side }
        Assert-Check ($actual.side -eq $expectedSide -and $actual.included -eq $row.included) "Selected-side batch changes only its intended contours and preserves inclusion for $($row.id)"
    }
    Invoke-OverflowCommand '撤销'
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -match '未保存' } 'Undo restores a machining state different from the saved batch'
    Invoke-Id 'SaveButton'
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'One undo restores and saves the batch baseline'
    $restored = Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    Assert-Check (($restored.contours | ConvertTo-Json -Depth 5 -Compress) -eq ($baseline.contours | ConvertTo-Json -Depth 5 -Compress)) 'One undo restores every contour side and machining checkbox from the batch edit'
    Clear-ContourSelection
    Capture '01d-contour-selection-batch-undone.png'
}
function Assert-ParameterPanel {
    # Layout-only panels do not always expose an automation peer.
    $panel = $script:window
    foreach ($id in @('ConfigSourceText', 'PresetList')) {
        Assert-Check ($null -eq (Find-Id $id $panel)) "Machining parameters do not expose $id"
    }
    foreach ($name in @('导入参数', '导出当前参数', '恢复默认参数', '导入参数配置…', '导出参数配置…', '查看固定默认参数', '保存为我的配置…')) {
        Assert-Check ($null -eq (Find-Name $name $panel)) "Machining parameters do not expose $name"
    }
    foreach ($id in @('ToolNumberBox', 'LengthOffsetBox', 'DiameterBox', 'SpindleSpeedBox', 'CuttingFeedBox', 'CuttingDepthBox', 'EdgeAllowanceBox', 'SafeZBox', 'PlungeFeedBox', 'StepDownBox')) {
        $number = Find-Id $id
        $description = Find-Id 'DescriptionPresenter' $number
        Assert-Check ($null -eq $description -or $description.Current.IsOffscreen -or [string]::IsNullOrWhiteSpace($description.Current.Name)) "Normal $id has no permanent description"
    }
    foreach ($description in @('机床调用的刀具编号', '机床刀长补偿表中的编号，可与刀号不同。', '刀具切削时的移动速度。', '每一层最多向下加工多少毫米。')) {
        Assert-Check ($null -eq (Find-Name $description $panel)) 'Parameter guidance is removed from the editing panel'
    }
}
function Assert-Guidance {
    Ensure-Workbench
    Open-Guide
    Assert-Check (Is-Visible 'GuideBackButton') 'Parameter guidance opens from the More menu and exposes Back to workbench'
    Assert-Check (@(Confirmations).Count -eq 0) 'Guidance has no manual confirmation controls'
    Assert-Check (-not (Find-Id 'ReviewProgressText').Current.IsOffscreen) 'Machine guidance description is visible'
}
function Open-ThroughActivation([string]$Path) {
    $activation = [Diagnostics.ProcessStartInfo]::new($exe)
    $activation.UseShellExecute = $false; $activation.CreateNoWindow = $true; $activation.WindowStyle = 'Hidden'; $activation.WorkingDirectory = $qa
    $activation.Environment['CAD2NC_SETTINGS_DIRECTORY'] = $settingsDirectory
    [void]$activation.Environment.Remove('DOTNET_ROOT'); [void]$activation.Environment.Remove('DOTNET_ROOT_X64')
    $activation.ArgumentList.Add($Path)
    $secondary = [Diagnostics.Process]::Start($activation)
    try {
        if (-not $secondary.WaitForExit(30000)) { $secondary.Kill(); $secondary.WaitForExit(); throw 'Owned file activation did not exit.' }
        Assert-Check ($secondary.ExitCode -eq 0) 'File activation redirects to the owned window'
    }
    finally { $secondary.Dispose() }
}
function Read-Ui {
    @($script:window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object {
            try { [ordered]@{ name = $_.Current.Name; id = $_.Current.AutomationId; type = $_.Current.ControlType.ProgrammaticName; enabled = $_.Current.IsEnabled; offscreen = $_.Current.IsOffscreen } }
            catch { [ordered]@{ unavailable = $_.Exception.Message } }
        })
}
function Capture([string]$Name, [IntPtr]$Handle = [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 350
    $script:ownedProcess.Refresh()
    if ($Handle -eq [IntPtr]::Zero) { $Handle = $script:ownedProcess.MainWindowHandle }
    [uint32]$owner = 0
    [void][WorkbenchV23QaNative]::GetWindowThreadProcessId($Handle, [ref]$owner)
    if ($owner -ne $script:ownedProcess.Id) { throw 'Refusing to capture another process window.' }
    $rect = [WorkbenchV23QaNative+RECT]::new()
    if (-not [WorkbenchV23QaNative]::GetWindowRect($Handle, [ref]$rect)) { throw 'Could not read owned window bounds.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $printed = [WorkbenchV23QaNative]::PrintWindow($Handle, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc) }
    try {
        if (-not $printed) { throw 'PrintWindow did not capture the owned window.' }
        $bitmap.Save((Join-Path $qa $Name)); $script:captures.Add($Name)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Resize-Owned([int]$WidthDip, [int]$HeightDip = 940) {
    $script:ownedProcess.Refresh()
    $handle = $script:ownedProcess.MainWindowHandle
    $scale = [WorkbenchV23QaNative]::GetDpiForWindow($handle) / 96.0
    if ($scale -le 0) { throw 'Could not read owned window DPI.' }
    $rect = [WorkbenchV23QaNative+RECT]::new()
    $client = [WorkbenchV23QaNative+RECT]::new()
    if (-not [WorkbenchV23QaNative]::GetWindowRect($handle, [ref]$rect) -or -not [WorkbenchV23QaNative]::GetClientRect($handle, [ref]$client)) { throw 'Could not read owned window/client bounds.' }
    $width = [int][Math]::Ceiling($WidthDip * $scale) + ($rect.Right - $rect.Left) - ($client.Right - $client.Left)
    $height = [int][Math]::Ceiling($HeightDip * $scale) + ($rect.Bottom - $rect.Top) - ($client.Bottom - $client.Top)
    if (-not [WorkbenchV23QaNative]::MoveWindow($handle, $rect.Left, $rect.Top, $width, $height, $true)) { throw 'Could not resize owned window.' }
    Start-Sleep -Milliseconds 450
    if (-not [WorkbenchV23QaNative]::GetClientRect($handle, [ref]$client)) { throw 'Could not verify owned client bounds.' }
    $actualWidth = ($client.Right - $client.Left) / $scale
    Assert-Check ($actualWidth -ge $WidthDip - .01 -and $actualWidth -lt $WidthDip + 1) "$WidthDip client DIP is measured at $([Math]::Round($actualWidth, 3)) DIP"
    $script:measurements.Add(@{ requestedWidthDip = $WidthDip; requestedHeightDip = $HeightDip; actualWidthDip = $actualWidth; actualHeightDip = ($client.Bottom - $client.Top) / $scale; dpi = $scale * 96 })
}
function Invoke-OverflowCommand([string]$Name) {
    $command = Find-Name $Name
    if ($null -eq $command -or $command.Current.IsOffscreen) {
        Invoke-Id 'MoreButton'
        Wait-Until { $item = Find-Name $Name; $null -ne $item -and $item.Current.IsEnabled -and -not $item.Current.IsOffscreen } "Overflow command $Name"
    }
    Invoke-Name $Name
}
function Select-ThemeOption([string]$Label) {
    Ensure-Settings
    Wait-Until { $null -ne (Find-Id 'SettingsTheme') } 'Settings page'
    $theme = Find-Id 'SettingsTheme'
    Bring-IntoView $theme
    $expand = $null
    while ($null -ne $theme -and -not $theme.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
        $theme = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($theme)
        if ($theme -and $theme.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Could not locate owned theme selector.' }
    }
    if ($null -eq $theme) { throw 'Theme selector has no expand pattern.' }
    Assert-Owned $theme; $expand.Expand()
    Wait-Until { $null -ne (Find-Name $Label) } "Theme option $Label"
    $option = Find-Name $Label; Assert-Owned $option
    $selection = $null
    while ($null -ne $option -and -not $option.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        $option = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($option)
        if ($option -and $option.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Could not locate owned theme option.' }
    }
    Assert-Owned $option; $selection.Select()
}
function Read-SelectedTheme {
    $theme = Find-Id 'SettingsTheme'; Assert-Owned $theme
    $selected = @($theme.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())
    if ($selected.Count -ne 1) { throw 'Theme selector does not expose exactly one selected option.' }
    Assert-Owned $selected[0]
    return $selected[0].Current.Name
}
function Set-Theme([string]$Label, [string]$StoredValue) {
    Select-ThemeOption $Label
    Wait-Until { (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).theme -eq $StoredValue } "Theme $StoredValue saved only to QA profile"
    Assert-Check (Is-Visible 'SettingsBackButton') 'Changing theme retains the settings page'
}
function Test-ThemeSaveFailure([string]$AttemptedLabel) {
    Ensure-Settings
    $previousTheme = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).theme
    $previousSelection = Read-SelectedTheme
    $previousTitle = (Find-Id 'DocumentTitle').Current.Name
    Assert-Check ($previousSelection -ne $AttemptedLabel) 'Theme failure case attempts a different theme'
    $settingsLock = $null
    try {
        # Only the isolated QA settings file is locked. Allow reading the old
        # value while denying writes and delete sharing needed by atomic replace.
        $settingsLock = [IO.File]::Open($settingsPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        Select-ThemeOption $AttemptedLabel
        Wait-Until { $errorText = Find-Id 'SettingsError'; $null -ne $errorText -and -not $errorText.Current.IsOffscreen -and -not [string]::IsNullOrWhiteSpace($errorText.Current.Name) } 'A blocked theme save displays an error on the settings page'
        Assert-Check ((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).theme -eq $previousTheme) 'Failed theme save preserves the previously stored theme'
        Wait-Until { (Read-SelectedTheme) -eq $previousSelection } 'Failed theme save restores the previous ComboBox selection'
        Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $previousTitle -and $previousTitle -notmatch '未保存') 'Failed theme save preserves the saved task state'
        Assert-Check (Is-Visible 'SettingsBackButton') 'Failed theme save keeps the settings page selected'
        Capture '04b-theme-save-failure.png'
    }
    finally {
        if ($null -ne $settingsLock) { $settingsLock.Dispose() }
    }
    Ensure-Workbench
    Assert-Check ((Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled) 'Failed theme save retains the valid NC result and playback'
}
function Is-OwnedVisiblePicker([IntPtr]$Handle) {
    if ($Handle -eq [IntPtr]::Zero -or $Handle -eq $script:ownedProcess.MainWindowHandle) { return $false }
    [uint32]$owner = 0
    [void][WorkbenchV23QaNative]::GetWindowThreadProcessId($Handle, [ref]$owner)
    if ($owner -ne $script:ownedProcess.Id -or -not [WorkbenchV23QaNative]::IsWindowVisible($Handle)) { return $false }
    $className = [Text.StringBuilder]::new(256)
    [void][WorkbenchV23QaNative]::GetClassName($Handle, $className, $className.Capacity)
    return $className.ToString() -eq '#32770'
}
function Native-Picker {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ownedProcess.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770'))
    $elements = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($element in $elements) {
        try {
            $handle = [IntPtr]$element.Current.NativeWindowHandle
            if (Is-OwnedVisiblePicker $handle) { return [Windows.Automation.AutomationElement]::FromHandle($handle) }
        }
        catch [Windows.Automation.ElementNotAvailableException] { }
    }
    return $null
}
function Cancel-NativePicker {
    $picker = Native-Picker; Assert-Owned $picker
    $handle = [IntPtr]$picker.Current.NativeWindowHandle
    if (-not (Is-OwnedVisiblePicker $handle)) { throw 'Refusing to cancel a window that is not the owned visible native picker.' }
    $cancel = Find-Id '2' $picker
    $invoke = $null
    if ($null -ne $cancel) {
        Assert-Owned $cancel
        if ($cancel.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke() }
    }
    Start-Sleep -Milliseconds 500
    if (Is-OwnedVisiblePicker $handle) {
        # IDCANCEL is posted to the revalidated native dialog, never the main app HWND.
        [void][WorkbenchV23QaNative]::PostMessage($handle, 0x0111, [IntPtr]2, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 500
    }
    if (Is-OwnedVisiblePicker $handle) {
        [void][WorkbenchV23QaNative]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    Wait-Until { $null -eq (Native-Picker) } 'Owned native picker cancels'
}
function Test-NcPreview {
    # A single external program covers an I/J semicircle, an R semicircle,
    # and an I/J full circle descending helically. Screenshots still require
    # visual review of the curves, current marker, and source-line highlight.
    $arcNc = Join-Path $qa '圆心半径与整圆螺旋.nc'
    [IO.File]::WriteAllText($arcNc, "O9003`r`nG21 G17 G90 G91.1 G94`r`nG0 X0 Y0 Z5`r`nG1 Z-1 F600`r`nG2 X20 Y0 I10 J0`r`nG3 X40 Y0 R10`r`nG2 X40 Y0 I-10 J0 Z-3`r`nG0 Z5`r`nM30`r`n")
    Open-ThroughActivation $arcNc
    Wait-Until { (Find-Id 'FileHeading').Current.Name -eq '圆心半径与整圆螺旋.nc' -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'External I/J, R, full-circle and helical motions are playable' 45
    Assert-NcSourceSelected 'Opening an external arc program'
    $arcProgress = Find-Id 'PlaybackProgress'; Assert-Owned $arcProgress
    $arcRange = $arcProgress.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
    Assert-Check ($arcRange.Current.Value -eq 0) 'Opening the arc program resets the previous NC position'
    Invoke-Id 'PlaybackButton'
    Wait-Until { $arcRange.Current.Value -gt 0 } 'External arc program advances during playback'
    $arcRange.SetValue(50)
    Wait-Until { (Find-Id 'PlaybackButton').Current.Name -eq '播放 NC' -and (Find-Id 'PlaybackStatusText').Current.Name -match '圆弧切削.*NC 第 6 行' } 'Seeking to fifty percent pauses on the radius-format arc source line'
    Assert-Check ([Math]::Abs($arcRange.Current.Value - 50) -lt .01) 'External arc seek retains the requested fifty-percent position'
    Capture '17b-nc-arcs-halfway.png'
    $arcRange.SetValue(75)
    Wait-Until { (Find-Id 'PlaybackStatusText').Current.Name -match '圆弧切削.*NC 第 7 行' } 'The full-circle helix retains its single NC source line'
    Assert-Check ((Find-Id 'PlaybackPositionText').Current.Name -match 'X .*Y .*Z -[12]\.\d{3} mm') 'The full-circle helix interpolates its descending Z coordinate'
    Capture '17c-nc-full-circle-helix.png'

    # This program has only one XY point, far from zero. It must retain a
    # playable Z motion and a visible marker when the canvas fits its bounds.
    $verticalNc = Join-Path $qa '大坐标单点下刀.nc'
    [IO.File]::WriteAllText($verticalNc, "G0 X10000 Y10000 Z5`r`nG1 Z-1 F100`r`nG0 Z5`r`nM30`r`n")
    Open-ThroughActivation $verticalNc
    Wait-Until { (Find-Id 'FileHeading').Current.Name -eq '大坐标单点下刀.nc' -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Z-only movement at a single distant XY point remains playable' 45
    Assert-NcSourceSelected 'Opening an external Z-only program'
    $verticalProgress = Find-Id 'PlaybackProgress'; Assert-Owned $verticalProgress
    $verticalRange = $verticalProgress.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
    Assert-Check ($verticalRange.Current.Value -eq 0) 'The single-point program does not inherit the arc playback position'
    Invoke-Id 'PlaybackButton'
    Wait-Until { $verticalRange.Current.Value -gt 0 } 'Z-only program advances during playback'
    $verticalRange.SetValue(50)
    Wait-Until { (Find-Id 'PlaybackButton').Current.Name -eq '播放 NC' -and (Find-Id 'PlaybackStatusText').Current.Name -match '下刀.*NC 第 2 行' } 'Single-point seek pauses during its Z descent'
    Assert-Check ((Find-Id 'PlaybackPositionText').Current.Name -match '^X 10000\.000  Y 10000\.000  Z \d\.\d{3} mm$') 'Z descent retains the actual distant XY position and a known Z value'
    Capture '17d-nc-single-point-z-halfway.png'
    $verticalRange.SetValue(100)
    Wait-Until { (Find-Id 'PlaybackPositionText').Current.Name -eq 'X 10000.000  Y 10000.000  Z 5.000 mm' } 'Z-only playback returns exactly to its programmed safe position'
    Assert-Check ((Find-Id 'PlaybackStatusText').Current.Name -match '空移 / 抬刀.*NC 第 3 行') 'The final Z-only rapid retains its source line'
}
function Write-Report([string]$Status, [string]$Failure = '') {
    [ordered]@{
        status = $Status; part = $Part; failure = $Failure; executable = $exe
        drawing = if ($Part -eq 'NcPreview') { $null } else { $sourceDrawing }
        copiedDrawing = if ($Part -eq 'NcPreview') { $null } else { $drawingPath }
        processId = if ($script:ownedProcess) { $script:ownedProcess.Id } else { $null }
        qaDirectory = $qa; checks = $script:checks.ToArray(); screenshots = $script:captures.ToArray()
        measurements = $script:measurements.ToArray(); sizeBasis = 'Client DIP'
        nativePickerSubmission = 'not attempted'; ncClipboardCopy = 'not attempted'; visualReview = 'required'
        physicalMouseHitTestingVerified = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $qa 'report.json') -Encoding utf8
}

try {
    # Recheck immediately before launch to avoid redirecting into a host session.
    if (Get-Process CAD2NC -ErrorAction SilentlyContinue) { throw 'CAD2NC started during preparation; refusing to launch into it.' }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'; $start.WorkingDirectory = $qa
    $start.Environment['CAD2NC_SETTINGS_DIRECTORY'] = $settingsDirectory
    [void]$start.Environment.Remove('DOTNET_ROOT'); [void]$start.Environment.Remove('DOTNET_ROOT_X64')
    $script:ownedProcess = [Diagnostics.Process]::Start($start)
    Wait-Until { $script:ownedProcess.Refresh(); $script:ownedProcess.MainWindowHandle -ne 0 } 'Owned window starts'
    $script:window = [Windows.Automation.AutomationElement]::FromHandle($script:ownedProcess.MainWindowHandle)
    Wait-Until { Is-Visible 'WorkbenchContextBar' } 'Empty compact workbench is ready'
    Resize-Owned 1440
    Assert-Check (Is-Visible 'WorkbenchContextBar') 'Startup opens directly into the compact workbench'
    foreach ($removedTab in @('WorkbenchTab', 'GuideTab', 'SettingsTab', 'PageNavigation')) {
        Assert-Check ($null -eq (Find-Id $removedTab)) "Permanent page navigation $removedTab is absent"
    }
    foreach ($removed in @('OpenStep', 'SetupStep', 'InspectStep', 'OutputStep')) {
        Assert-Check ($null -eq (Find-Id $removed)) "Obsolete workflow step $removed is absent"
    }
    Assert-Check ((Find-Id 'ExportButton').Current.IsOffscreen -eq $false -and -not (Find-Id 'ExportButton').Current.IsEnabled) 'Empty workbench shows disabled NC saving in the main toolbar'
    Assert-Check ((Find-Id 'ExportButton').Current.Name -eq '导出 NC') 'The primary output command is labelled 导出 NC'
    if ($Part -eq 'NcPreview') {
        Test-NcPreview
        Read-Ui | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qa 'final-ui.json') -Encoding utf8
        Write-Report 'passed'
        Write-Output "WORKBENCH_V23_QA_PASSED part=$Part $qa"
        return
    }
    Open-Guide
    Assert-Check (Is-Visible 'ReviewProgressText') 'Machine guidance is accessible from More without a file'
    Ensure-Workbench
    if ($Part -eq 'Dialogs') { Assert-Guidance; Capture '00-guidance-without-file.png' }
    else { Capture '00-empty-workbench.png' }
    Open-ThroughActivation $drawingPath
    Wait-Until { $button = Find-Id 'GenerateButton'; $null -ne $button -and $button.Current.IsEnabled } 'Actual drawing imported' 45
    Resize-Owned 1440; Go-Parameters
    Assert-Check ((Find-Id 'FileHeading').Current.Name -eq [IO.Path]::GetFileName($drawingPath)) 'The copied actual drawing is open'
    if ($Part -eq 'All') {
    foreach ($group in @(
        @{ id = 'MachineParametersExpander'; label = '机床与工件'; state = 'Collapsed' },
        @{ id = 'ToolParametersExpander'; label = '刀具'; state = 'Collapsed' },
        @{ id = 'CommonParametersExpander'; label = '切削'; state = 'Collapsed' },
        @{ id = 'PlungeParametersExpander'; label = '深度与分层'; state = 'Collapsed' },
        @{ id = 'DirectionParametersExpander'; label = '补偿与方向'; state = 'Collapsed' },
        @{ id = 'MachineOutputExpander'; label = '机床输出与结束动作'; state = 'Collapsed' }
    )) {
        $element = Find-Id $group.id; Assert-Owned $element
        $groupState = $element.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState
        Assert-Check ($groupState.ToString() -eq $group.state) "$($group.label) is $($group.state) by default"
        Assert-Check ($element.Current.Name -match [regex]::Escape($group.label) -or $null -ne (Find-Name $group.label $element)) "$($group.id) exposes the $($group.label) heading"
    }
    $quick = Find-Id 'QuickParametersPanel'; Assert-Owned $quick
    Assert-Check (-not $quick.Current.IsOffscreen) 'Common parameter section is visible by default'
    $skin = Find-Id 'StepDownBox'; Assert-Owned $skin
    Assert-Check (-not $skin.Current.IsOffscreen -and $skin.Current.IsEnabled) 'Leave-skin thickness is the prominent editable parameter'
    Assert-Check (-not (Is-Visible 'StalePlanBadge')) 'An imported drawing with no result has no stale-result badge'
    Capture '01-setup-dark-wide.png'
    Expand-Id 'MachineParametersExpander'
    Expand-Id 'ToolParametersExpander'
    Expand-Id 'CommonParametersExpander'
    Expand-Id 'PlungeParametersExpander'
    Expand-Id 'DirectionParametersExpander'
    foreach ($pair in @(@('ToolNumberBox', 2), @('LengthOffsetBox', 2), @('DiameterBox', 6), @('SpindleSpeedBox', 18000), @('CuttingFeedBox', 10000), @('CuttingDepthBox', -0.1), @('EdgeAllowanceBox', 1), @('SafeZBox', 45), @('PlungeFeedBox', 8000), @('StepDownBox', 0))) {
        Assert-Check ([Math]::Abs((Read-Number $pair[0]) - $pair[1]) -lt 0.000001) "Startup uses fixed default for $($pair[0])"
    }
    Assert-ParameterPanel
    Capture '01c-parameters-expanded-dark-wide.png'

    # Leave-skin input commits on focus exit and is normalized to one decimal.
    Set-Number 'StepDownBox' 0.15 0.2
    Assert-Check ([Math]::Abs((Read-Number 'StepDownBox') - 0.2) -lt 0.000001) 'Leave-skin input rounds to one decimal on focus exit'
    Set-Number 'StepDownBox' 0.3
    Assert-Check ([Math]::Abs((Read-Number 'StepDownBox') - 0.3) -lt 0.000001) 'Leave-skin input displays a clean decimal after editing'

    # Preserve the actual invalid input while other parameter controls refresh.
    Set-Number 'LengthOffsetBox' 2.5
    Wait-Until { (Toggle-State 'FollowToolCheck') -eq [Windows.Automation.ToggleState]::Off -and -not (Find-Id 'GenerateButton').Current.IsEnabled } 'Non-integer H disables following and generation'
    Assert-Check ((Read-Number 'LengthOffsetBox') -eq 2.5 -and (Find-Id 'ValidationSummary').Current.Name -match '请输入整数') 'Invalid H remains visible with an integer validation message'
    Set-Number 'LengthOffsetBox' 3.5
    Invoke-OverflowCommand '撤销'
    Wait-Until { (Read-Number 'LengthOffsetBox') -eq 2.5 -and -not (Find-Id 'GenerateButton').Current.IsEnabled } 'Undo restores the actual invalid H value with its error'
    Invoke-OverflowCommand '重做'
    Wait-Until { (Read-Number 'LengthOffsetBox') -eq 3.5 -and -not (Find-Id 'GenerateButton').Current.IsEnabled } 'Redo restores the newer invalid H value with its error'
    Invoke-OverflowCommand '撤销'
    Wait-Until { (Read-Number 'LengthOffsetBox') -eq 2.5 } 'Restore invalid H before following test'
    Invoke-Id 'FollowToolCheck'
    Wait-Until { (Toggle-State 'FollowToolCheck') -eq [Windows.Automation.ToggleState]::On -and (Read-Number 'LengthOffsetBox') -eq 2 -and (Find-Id 'GenerateButton').Current.IsEnabled } 'Following T replaces invalid H even when the accepted configuration is unchanged'
    Set-Number 'LengthOffsetBox' 2.5
    Wait-Until { (Toggle-State 'FollowToolCheck') -eq [Windows.Automation.ToggleState]::Off -and -not (Find-Id 'GenerateButton').Current.IsEnabled } 'Editing H disables following again'
    Set-Number 'ToolNumberBox' 3
    Assert-Check ((Read-Number 'LengthOffsetBox') -eq 2.5 -and -not (Find-Id 'GenerateButton').Current.IsEnabled -and (Find-Id 'ValidationSummary').Current.Name -match '请输入整数') 'Changing T preserves the invalid H text and cannot re-enable generation'
    Capture '01b-invalid-h-preserved.png'
    Set-Number 'LengthOffsetBox' 2
    Wait-Until { (Find-Id 'GenerateButton').Current.IsEnabled } 'Correcting H to an integer re-enables generation'
    Invoke-Id 'FollowToolCheck'
    Wait-Until { (Toggle-State 'FollowToolCheck') -eq [Windows.Automation.ToggleState]::On -and (Read-Number 'LengthOffsetBox') -eq 3 } 'H following can be restored after correcting invalid input'

    Set-Number 'ToolNumberBox' 4
    Wait-Until { (Read-Number 'LengthOffsetBox') -eq 4 } 'H follows T when explicitly linked'
    Invoke-Id 'FollowToolCheck'
    Wait-Until { (Toggle-State 'FollowToolCheck') -eq [Windows.Automation.ToggleState]::Off } 'Disable H following T'
    Set-Number 'LengthOffsetBox' 7; Set-Number 'ToolNumberBox' 5
    Assert-Check ((Read-Number 'ToolNumberBox') -eq 5 -and (Read-Number 'LengthOffsetBox') -eq 7) 'T and H retain independent values when following is off'

    Invoke-Id 'SaveButton'
    Wait-Until { (Test-Path -LiteralPath $taskPath -PathType Leaf) -and (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'Task saved only in the isolated output directory'
    $saved = Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    Assert-Check ($saved.config.tool.toolNumber -eq 5 -and $saved.config.tool.lengthOffset -eq 7 -and [Math]::Abs([double]$saved.config.machining.leaveSkin - 0.3) -lt 0.000001) 'Saved task records independent T/H and committed leave-skin values'
    Test-ContourSelectionAndBatch
    Invoke-Id 'GenerateButton'
    Wait-Until { (Is-Visible 'WorkbenchContextBar') -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Generation stays on the workbench with playback available' 45
    Assert-Check (-not (Is-Visible 'StalePlanBadge')) 'Successful generation clears the stale-result badge'
    Assert-Check (@(Confirmations).Count -eq 0) 'Generated results have no manual confirmation controls'
    Assert-Check (-not (Find-Id 'ResultsTitle').Current.IsOffscreen) 'Warnings automatically open machining diagnostics on the workbench'
    Assert-Check (-not (Find-Name '收起参数面板').Current.IsOffscreen) 'Generated results and machining parameters remain visible together'
    Select-ResultTab 'NC 代码'
    Assert-Check ((Find-Id 'CopyNcButton').Current.IsEnabled) 'A current valid result can be copied before visiting guidance'
    Capture '02-results-dark-wide.png'
    Assert-Check ((Find-Id 'ExportButton').Current.IsEnabled -and -not (Find-Id 'ExportButton').Current.IsOffscreen) 'Valid generated output is immediately saveable from the main toolbar'
    Assert-Check (-not (Test-Path -LiteralPath $ncDirectory)) 'Generation does not automatically write NC'
    Select-ResultTab '加工摘要'
    Assert-Check ((Find-Id 'OutputSummaryText').Current.Name -match 'T5 / H7') 'Output summary shows the actual T and H'
    Assert-Check ((Find-Id 'OutputPathText').Current.Name -match [regex]::Escape($ncDirectory)) 'Result summary displays the configured NC destination'
    Capture '03-output-ready-without-guidance.png'
    Invoke-Id 'ExportButton'
    Wait-Until { Test-Path -LiteralPath $ncPath -PathType Leaf } 'NC is saved into the isolated QA directory before any guidance visit'
    $ncText = Get-Content -LiteralPath $ncPath -Raw
    Assert-Check ($ncText -match '(?m)^T5(?: M06)?\s*$' -and $ncText -match '(?m)^G43 H7(?:\s+Z)?' -and ($ncText -match '(?m)^G21 G17 G90 G94\s*$' -or $ncText -match '(?m)^G54 G49 G90\s*$') -and $ncText -match '(?m)^G01 X' -and $ncText -match '(?m)^M30\s*$') 'Saved NC contains independent T5/H7, coordinate modes, cutting moves, and program end'
    Assert-Check ($null -eq (Native-Picker)) 'Direct NC save does not submit a native picker'
    $savedNcHash = (Get-FileHash -LiteralPath $ncPath -Algorithm SHA256).Hash
    Capture '04-output-saved.png'

    Assert-Guidance
    $cleanTitle = (Find-Id 'DocumentTitle').Current.Name
    Assert-Check ($cleanTitle -notmatch '未保存') 'Reading guidance does not dirty a saved task'
    Ensure-Workbench
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $cleanTitle) 'Returning from guidance preserves task saved state'
    Open-Settings
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $cleanTitle) 'Opening settings preserves task saved state'
    Ensure-Workbench
    Open-Guide
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $cleanTitle) 'Reopening guidance preserves task saved state'
    Ensure-Workbench
    Assert-Check ((Find-Id 'ExportButton').Current.IsEnabled) 'Page navigation preserves output eligibility'
    Invoke-Name '收起结果区'
    Assert-Check ($null -eq (Find-Id 'ResultsTitle') -or (Find-Id 'ResultsTitle').Current.IsOffscreen) 'Collapsing results hides the bottom result panel'
    Assert-Check (-not (Find-Name '收起参数面板').Current.IsOffscreen -and (Find-Id 'ExportButton').Current.IsEnabled) 'Collapsing results preserves parameters and output eligibility'
    Invoke-Id 'OpenResultsButton'
    Assert-Check (-not (Find-Id 'ResultsTitle').Current.IsOffscreen -and -not (Find-Name '收起参数面板').Current.IsOffscreen) 'Expanding results keeps parameters visible'
    Test-ThemeSaveFailure '浅色'
    Set-Theme '浅色' 'Light'
    Wait-Until { $errorText = Find-Id 'SettingsError'; $null -eq $errorText -or $errorText.Current.IsOffscreen -or [string]::IsNullOrWhiteSpace($errorText.Current.Name) } 'A successful theme change clears the previous save error'
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -eq $cleanTitle) 'Global theme changes do not dirty the saved task'
    Ensure-Workbench
    Assert-Check ((Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled) 'Theme changes retain the generated program and playback'
    Set-Theme '深色' 'Dark'
    Ensure-Workbench

    # A new valid result is immediately usable; machining edits still invalidate it.
    Go-Parameters; Invoke-Id 'GenerateButton'
    Wait-Until { (Is-Visible 'WorkbenchContextBar') -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Regeneration completes on the workbench' 45
    Assert-Check ((Find-Id 'ExportButton').Current.IsEnabled) 'Regeneration restores output without acknowledgement'
    Go-Parameters; Set-Number 'CuttingFeedBox' 9000
    Wait-Until { -not (Find-Id 'PlaybackButton').Current.IsEnabled -and (Is-Visible 'StalePlanBadge') } 'Parameter edit clears playback and immediately marks the old plan stale'
    Assert-Check ((Find-Id 'StalePlanBadge').Current.Name -eq '上次结果 · 待更新') 'Stale badge explicitly identifies the displayed previous result'
    Assert-Check ((Find-Id 'DocumentTitle').Current.Name -match '未保存') 'A machining edit marks the task unsaved'
    Assert-Check (-not (Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'NextActionText').Current.Name -match '重新生成') 'Editing invalidates output and explains the next action'
    Capture '05-output-invalidated.png'
    Go-Parameters; Invoke-Id 'GenerateButton'
    Wait-Until { (Find-Id 'PlaybackButton').Current.IsEnabled -and -not (Is-Visible 'StalePlanBadge') } 'Regeneration restores the plan and clears its stale badge' 45
    Set-Number 'LengthOffsetBox' 2.5
    Wait-Until { -not (Find-Id 'GenerateButton').Current.IsEnabled -and (Is-Visible 'StalePlanBadge') } 'An invalid numeric edit immediately marks the previous plan stale'
    Assert-Check (-not (Find-Id 'ExportButton').Current.IsEnabled -and -not (Find-Id 'PlaybackButton').Current.IsEnabled) 'Invalid numeric input cannot retain export or playback eligibility'
    Capture '05b-invalid-number-stale-result.png'
    Set-Number 'LengthOffsetBox' 7
    # The accepted 10,000 mm allowance collapses the finite fixture contours.
    # This exercises planner failure after a previous valid result, rather than
    # merely testing field validation or cancellation before planning starts.
    Invoke-Id 'SaveButton'
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'Save the current viewport before a planner failure'
    $beforeFailedView = (Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json).view
    Capture '05c-before-planner-failure.png'
    Set-Number 'EdgeAllowanceBox' 10000
    Assert-Check ((Find-Id 'GenerateButton').Current.IsEnabled) 'The planner-failure fixture uses numerically valid parameters'
    Invoke-Id 'GenerateButton'
    Wait-Until { (Find-Id 'GenerateButton').Current.IsEnabled -and (Find-Id 'ResultSummary').Current.Name -eq '生成失败' } 'Valid parameters produce the intended geometric planning failure' 30
    Assert-Check ((Is-Visible 'StalePlanBadge') -and -not (Find-Id 'ExportButton').Current.IsEnabled -and -not (Find-Id 'PlaybackButton').Current.IsEnabled) 'Planning failure keeps the previous-result badge and disables NC export and playback'
    Capture '05c-planner-failure-stale-result.png'
    Invoke-Id 'SaveButton'
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'Save isolated failure parameters for viewport comparison'
    $afterFailedView = (Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json).view
    Assert-Check ($afterFailedView.centerX -eq $beforeFailedView.centerX -and $afterFailedView.centerY -eq $beforeFailedView.centerY -and $afterFailedView.scale -eq $beforeFailedView.scale -and $afterFailedView.hasViewport -eq $beforeFailedView.hasViewport) 'Planning failure preserves the persisted viewport; actual canvas scale requires paired screenshot review'
    Set-Number 'EdgeAllowanceBox' 1
    Invoke-Id 'GenerateButton'
    Wait-Until { (Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled -and -not (Is-Visible 'StalePlanBadge') } 'Correcting the geometric failure restores a current usable plan' 30
    Invoke-Id 'SaveButton'
    Wait-Until { (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'Restore the valid isolated task after the failure scenario'
    }
    else {
        Write-Output 'PART Dialogs: full workflow, direct NC save, and NC open checks are intentionally skipped.'
        Set-Number 'CuttingFeedBox' 9000
    }

    Go-Parameters
    $beforeDialogFeed = Read-Number 'CuttingFeedBox'
    Assert-ParameterPanel
    foreach ($entry in @(@('SettingsImport', '06-settings-import-picker.png'), @('SettingsExport', '07-settings-export-picker.png'))) {
        Ensure-Settings
        Wait-Until { $null -ne (Find-Id 'SettingsTheme') } 'Settings page opens'
        foreach ($id in @('SettingsImport', 'SettingsExport', 'SettingsReset')) {
            Assert-Check ($null -ne (Find-Id $id)) "Settings includes parameter action $id"
        }
        Invoke-Id $entry[0]
        Wait-Until { $null -ne (Native-Picker) } "Owned configuration picker opens: $($entry[0])"
        Capture $entry[1] ([IntPtr](Native-Picker).Current.NativeWindowHandle)
        Cancel-NativePicker
        Wait-Until { (Is-Visible 'SettingsBackButton') -and (Find-Id $entry[0]).Current.IsEnabled } 'Cancelled parameter picker retains the settings page and re-enables actions'
        Go-Parameters
        Assert-Check ((Read-Number 'CuttingFeedBox') -eq $beforeDialogFeed) "Cancelling $($entry[0]) preserves current parameters"
    }
    Invoke-Id 'GenerateButton'
    Wait-Until { (Is-Visible 'WorkbenchContextBar') -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Edited program regenerated' 45
    Open-Settings
    Invoke-Id 'SettingsReset'
    Wait-Until { (Is-Visible 'SettingsBackButton') -and (Find-Id 'SettingsReset').Current.IsEnabled } 'Restoring defaults retains the settings page'
    Go-Parameters
    Assert-Check (-not (Find-Id 'PlaybackButton').Current.IsEnabled -and -not (Find-Id 'ExportButton').Current.IsEnabled) 'Restoring defaults invalidates existing playback and output'
    Assert-Check ((Read-Number 'CuttingFeedBox') -eq 10000 -and (Read-Number 'ToolNumberBox') -eq 2 -and (Read-Number 'LengthOffsetBox') -eq 2) 'Settings restores fixed default machining values'
    Invoke-OverflowCommand '撤销'
    Wait-Until { (Read-Number 'CuttingFeedBox') -eq $beforeDialogFeed } 'Default restoration can be undone'
    Invoke-Id 'GenerateButton'
    Wait-Until { (Is-Visible 'WorkbenchContextBar') -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Undone parameters regenerate successfully' 45
    Capture '08-results-dark-wide.png'
    Assert-Guidance
    Capture '09-guidance-dark-wide.png'
    Resize-Owned 1366 768
    Assert-Check ((Is-Visible 'GuideBackButton') -and -not (Find-Id 'ReviewProgressText').Current.IsOffscreen) '1366 by 768 DIP guidance keeps Back and explanation accessible'
    Capture '10-guidance-dark-1366x768.png'
    Go-Parameters; Capture '10b-setup-dark-1366x768.png'
    Assert-Guidance
    Resize-Owned 820 900
    Assert-Check ((Is-Visible 'GuideBackButton') -and -not (Find-Id 'ReviewProgressText').Current.IsOffscreen) 'Narrow guidance keeps Back and explanation accessible'
    Capture '11-guidance-dark-narrow.png'
    Ensure-Workbench; Invoke-Id 'ResultsPanelButton'
    Assert-Check (-not (Find-Id 'ResultsTitle').Current.IsOffscreen) 'Narrow workbench can display machining results'
    Capture '11b-results-dark-narrow.png'
    Invoke-Id 'PreviewPanelButton'
    Assert-Check (-not (Find-Id 'PlaybackButton').Current.IsOffscreen) 'Narrow workbench switches from results back to the preview and playback'
    Capture '11c-preview-dark-narrow.png'
    Resize-Owned 1000 900; Go-Parameters
    Assert-Check (-not (Find-Id 'ToolNumberBox').Current.IsOffscreen -and -not (Find-Id 'PlaybackButton').Current.IsOffscreen) 'Medium workbench shows the preview beside the active parameter panel'
    Capture '11d-parameters-dark-medium.png'
    Resize-Owned 1440
    Set-Theme '浅色' 'Light'
    Go-Parameters; Capture '12-setup-light-wide.png'
    Assert-Guidance; Capture '13-guidance-light-wide.png'
    Resize-Owned 820 900; Capture '14-guidance-light-narrow.png'
    Assert-Check ((Is-Visible 'GuideBackButton') -and -not (Find-Id 'ReviewProgressText').Current.IsOffscreen) 'Light narrow guidance keeps Back and explanation accessible'
    Ensure-Workbench; Open-Settings; Capture '14b-settings-light-narrow.png'
    Resize-Owned 1440; Capture '14c-settings-light-wide.png'

    if ($Part -eq 'All') {
        Resize-Owned 1440; Go-Parameters
        Invoke-Id 'SaveButton'
        Wait-Until { (Find-Id 'DocumentTitle').Current.Name -notmatch '未保存' } 'Current task saved before entering NC view'
        $ncOpenPath = Join-Path $qa '已生成程序.NC'
        Copy-Item -LiteralPath $ncPath -Destination $ncOpenPath
        Open-ThroughActivation $ncOpenPath
        Wait-Until { (Find-Id 'FileHeading').Current.Name -eq '已生成程序.NC' -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Saved NC reopens through uppercase file activation with playback ready' 45
        Assert-NcSourceSelected 'Opening the generated NC file'
        Assert-NcReadingLayout 1440
        $initialReadingWidth = (Find-Id 'ResultTabs').Current.BoundingRectangle.Width
        $initialCadWidths = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).lastView
        foreach ($id in @('GenerateButton', 'SaveButton', 'ExportButton', 'ExportAsButton', 'ParametersPanelButton', 'ContoursPanelButton', 'ParameterPanel', 'ContourList')) {
            $element = Find-Id $id
            Assert-Check ($null -eq $element -or $element.Current.IsOffscreen) "Read-only NC view hides $id"
        }
        Ensure-Workbench; Open-Settings
        foreach ($id in @('SettingsImport', 'SettingsExport', 'SettingsReset')) {
            Assert-Check (-not (Find-Id $id).Current.IsEnabled) "External NC disables settings machining action $id"
        }
        Ensure-Workbench
        Select-ResultTab '程序参数'
        Assert-Check ($null -ne (Find-Id 'NcParameterList')) 'External NC exposes parsed program parameters'
        $parameterTab = Find-Name '程序参数'; Assert-Owned $parameterTab
        Open-Settings; Ensure-Workbench
        Assert-Check ((Find-Name '程序参数').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'Refreshing external NC parameters during page navigation preserves the selected reading tab'
        Capture '15-nc-parameters-light-wide.png'
        Select-ResultTab 'NC 代码'
        Assert-Check ((Find-Id 'CopyNcButton').Current.IsEnabled) 'External NC source is available to copy'
        $progress = Find-Id 'PlaybackProgress'; Assert-Owned $progress
        $progress.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(50)
        Wait-Until { (Find-Id 'PlaybackPositionText').Current.Name -match 'X .*Y .*Z ' } 'External NC progress exposes XYZ position'
        Capture '16-nc-code-light-wide.png'
        $positionBeforeLayout = (Find-Id 'PlaybackPositionText').Current.Name
        Invoke-Id 'ExpandResultsButton'
        Wait-Until { (Is-Visible 'CopyNcButton') -and -not (Is-Visible 'PlaybackButton') } 'External NC source expands to the large reading view'
        Capture '16b-nc-large-source.png'
        Invoke-Id 'ExpandResultsButton'
        Wait-Until { (Is-Visible 'PlaybackButton') -and (Is-Visible 'CopyNcButton') } 'External NC large view returns to side-by-side reading'
        Assert-Check ((Find-Id 'PlaybackPositionText').Current.Name -eq $positionBeforeLayout) 'Returning from the large NC source view preserves playback coordinates'
        foreach ($width in @(899, 900, 1199, 1200, 899, 900, 1440)) { Assert-NcReadingLayout $width }
        Assert-Check ([Math]::Abs((Find-Id 'ResultTabs').Current.BoundingRectangle.Width - $initialReadingWidth) -le 2) 'NC reading width returns to its prior value after compact layouts'
        $afterReadingView = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).lastView
        Assert-Check ($afterReadingView.leftWidth -eq $initialCadWidths.leftWidth -and $afterReadingView.rightWidth -eq $initialCadWidths.rightWidth) 'NC responsive reading does not overwrite CAD sidebar widths'
        Resize-Owned 820 900
        Invoke-Id 'ResultsPanelButton'
        Wait-Until { Is-Visible 'CopyNcButton' } 'Narrow external NC source remains available'
        Capture '16c-nc-source-light-narrow.png'
        Invoke-Id 'PreviewPanelButton'
        Assert-Check (-not (Find-Id 'PlaybackButton').Current.IsOffscreen -and (Find-Id 'PlaybackButton').Current.IsEnabled) 'External NC playback remains accessible in a narrow window'
        Capture '17-nc-light-narrow.png'
        Resize-Owned 1440

        Test-NcPreview

        $unsupportedNc = Join-Path $qa '不支持的固定循环.nc'
        [IO.File]::WriteAllText($unsupportedNc, "O9002`r`nG21 G17 G90 G94`r`nG0 X0 Y0 Z5`r`nG81 X10 Y10 Z-2 R1 F100`r`nM30`r`n")
        Open-ThroughActivation $unsupportedNc
        Wait-Until { (Find-Id 'FileHeading').Current.Name -eq '不支持的固定循环.nc' } 'Unsupported NC remains open for source inspection' 45
        $unsupportedPlayback = Find-Id 'PlaybackButton'
        Assert-Check ($null -eq $unsupportedPlayback -or -not $unsupportedPlayback.Current.IsEnabled) 'Unsupported motion leaves external NC playback unavailable'
        Assert-NcSourceSelected 'Opening an unsupported NC program'
        Assert-Check (-not (Is-Visible 'PlaybackButton')) 'Unsupported NC initially uses the source large view'
        Assert-Check ((Find-Id 'CopyNcButton').Current.IsEnabled) 'Unsupported NC retains its copyable source'
        Capture '18-nc-unsupported-source.png'
        Select-ResultTab '解析提示'
        Capture '18b-nc-unsupported-diagnostics.png'
        # The G81 error itself has no source index. The parser does retain the
        # line-3 first-position notice produced before G81, which can be located.
        $diagnostics = Find-Id 'DiagnosticList'; Assert-Owned $diagnostics
        foreach ($expander in @($diagnostics.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)))) {
            Assert-Owned $expander
            $expander.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        }
        Wait-Until { $sourceLocation = Find-Name '定位源码行'; $null -ne $sourceLocation -and $sourceLocation.Current.IsEnabled } 'The indexed first-position notice exposes source location'
        Invoke-Name '定位源码行'
        Wait-Until { (Find-Id 'NcCodeTab').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } 'Following the parser notice selects the source tab'
        $locatedRows = @((Find-Id 'NcList').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())
        Assert-Check ($locatedRows.Count -eq 1) 'Locating a parser notice selects exactly one source row'
        Assert-Check ($null -ne (Find-Name '3' $locatedRows[0]) -and $null -ne (Find-Name 'G0 X0 Y0 Z5' $locatedRows[0])) 'The first-position notice locates its actual third source line'
        Capture '18c-nc-diagnostic-source-location.png'

        Open-ThroughActivation $drawingPath
        Wait-Until { (Find-Id 'FileHeading').Current.Name -eq [IO.Path]::GetFileName($drawingPath) -and (Find-Id 'GenerateButton').Current.IsEnabled } 'CAD can reopen after external NC viewing' 45
        Go-Parameters
        Assert-Check ((Is-Visible 'ContourList') -and (Is-Visible 'PlaybackButton') -and (Is-Visible 'ToolParametersExpander')) 'Returning from unsupported NC restores the three-column CAD layout'
        Assert-Check ((Read-Number 'CuttingFeedBox') -eq $beforeDialogFeed) 'Viewing NC preserves the current machining configuration'
        Assert-Check (-not (Find-Id 'PlaybackButton').Current.IsEnabled) 'Returning to CAD does not reuse external NC playback'
    }
    $finalSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Assert-Check ($finalSettings.taskDirectory -eq $taskDirectory -and $finalSettings.ncDirectory -eq $ncDirectory) 'Every output directory remains within the isolated QA area'
    Assert-Check ((Get-FileHash -LiteralPath $sourceDrawing -Algorithm SHA256).Hash -eq $sourceHash) 'Original drawing is unchanged'
    if ($Part -eq 'All') {
        Assert-Check ((Test-Path -LiteralPath $ncPath -PathType Leaf) -and (Get-FileHash -LiteralPath $ncPath -Algorithm SHA256).Hash -eq $savedNcHash) 'Regeneration, edits, cancelled dialogs, and NC viewing preserve the previously saved NC file'
    }
    else { Assert-Check (-not (Test-Path -LiteralPath $ncDirectory)) 'Dialogs-only testing does not write an NC program' }
    Read-Ui | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qa 'final-ui.json') -Encoding utf8
    Write-Report 'passed'
    Write-Output "WORKBENCH_V23_QA_PASSED part=$Part $qa"
}
catch {
    $failure = $_.Exception.Message
    if ($script:window -and $script:ownedProcess -and -not $script:ownedProcess.HasExited) {
        try { Capture 'failure.png'; Read-Ui | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qa 'failure-ui.json') -Encoding utf8 } catch { }
    }
    Write-Report 'failed' $failure
    throw
}
finally {
    if ($script:ownedProcess -and -not $script:ownedProcess.HasExited) {
        # Exact process handle only: never enumerate or terminate unrelated processes.
        $script:ownedProcess.Kill(); $script:ownedProcess.WaitForExit()
    }
    if ($script:ownedProcess) { $script:ownedProcess.Dispose() }
}
