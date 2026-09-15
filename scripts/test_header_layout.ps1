#Requires -Version 7.0
<#
.SYNOPSIS
Checks the compact CAD2NC shell and responsive workbench in one owned process.
.DESCRIPTION
Captures 1440, 1366, 1000 and 820 client DIP layouts in light and dark themes. The checks use
UI Automation and exact owned HWND operations. They verify the fixed primary
command row, overflow access to Guidance and Settings, Back to workbench, the
wide three-panel layout, the medium single-inspector layout, and the narrow
mutually exclusive navigation, exact 899/900/1199/1200 DIP boundaries, and central
results expansion. UIA invocation does not establish physical mouse hit testing;
screenshots still require visual review.
#>
[CmdletBinding()]
param(
    [string]$Executable = 'dist/CAD2NC/CAD2NC.exe',
    [string]$Drawing = 'tests/fixtures/clean_mm.dxf'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$exe = [IO.Path]::GetFullPath($Executable, $workspace)
$sourceDrawing = [IO.Path]::GetFullPath($Drawing, $workspace)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable does not exist: $exe" }
if (-not (Test-Path -LiteralPath $sourceDrawing -PathType Leaf)) { throw "Drawing does not exist: $sourceDrawing" }

function Assert-NoExistingTarget {
    foreach ($process in @(Get-Process CAD2NC -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { }
        if ($path -and [IO.Path]::GetFullPath($path).Equals($exe, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The target executable is already running (PID $($process.Id)); it will not be interrupted."
        }
    }
}

Assert-NoExistingTarget
$qa = [IO.Path]::GetFullPath((Join-Path $workspace ('build/qa-header-layout-' + [Guid]::NewGuid().ToString('N'))))
if (-not $qa.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'QA directory escaped the workspace.' }
$settingsDirectory = Join-Path $qa 'profile'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$drawingPath = Join-Path $qa ('clean-success' + [IO.Path]::GetExtension($sourceDrawing))
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceDrawing -Destination $drawingPath
@{
    schemaVersion = 1; lastConfig = @{}; lastView = @{}; theme = 'Light'; presets = @(); recentTasks = @()
    taskDirectory = (Join-Path $qa 'tasks'); ncDirectory = (Join-Path $qa 'NC')
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding utf8

$script:ownedProcess = $null
$script:window = $null
$script:checks = [Collections.Generic.List[string]]::new()
$script:captures = [Collections.Generic.List[string]]::new()
$script:measurements = [Collections.Generic.List[object]]::new()

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
if (-not ('HeaderLayoutQaNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HeaderLayoutQaNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@
}

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks.Add($Message)
    Write-Output "PASS $Message"
}
function Wait-Until([scriptblock]$Condition, [string]$Message) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:ownedProcess -and $script:ownedProcess.HasExited) { throw "Owned CAD2NC exited ($($script:ownedProcess.ExitCode)); no other process will be used." }
        try { if (& $Condition) { return } } catch [Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Message"
}
function Assert-Owned($Element) {
    if ($null -eq $Element) { throw 'Missing UI control.' }
    if ($Element.Current.ProcessId -ne $script:ownedProcess.Id) { throw 'Refusing to operate another process UI.' }
}
function Find-Property($Property, [string]$Value) {
    $condition = [Windows.Automation.PropertyCondition]::new($Property, $Value)
    $found = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $found) {
        $ownedCondition = [Windows.Automation.AndCondition]::new($condition,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ownedProcess.Id))
        $found = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $ownedCondition)
    }
    return $found
}
function Find-Id([string]$Id) { Find-Property ([Windows.Automation.AutomationElement]::AutomationIdProperty) $Id }
function Find-Name([string]$Name) { Find-Property ([Windows.Automation.AutomationElement]::NameProperty) $Name }
function Is-Visible([string]$Id) {
    $element = Find-Id $Id
    return $null -ne $element -and -not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty
}
function Invoke-Element($Element) {
    Assert-Owned $Element
    if (-not $Element.Current.IsEnabled) { throw "Control is disabled: $($Element.Current.AutomationId) $($Element.Current.Name)" }
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
function Invoke-OverflowId([string]$Id) {
    $item = Find-Id $Id
    if ($null -eq $item -or $item.Current.IsOffscreen) {
        Invoke-Id 'MoreButton'
        Wait-Until { $entry = Find-Id $Id; $null -ne $entry -and $entry.Current.IsEnabled -and -not $entry.Current.IsOffscreen } "Overflow item $Id appears"
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
function Return-Workbench([string]$BackId) {
    Invoke-Id $BackId
    Wait-Until { Is-Visible 'WorkbenchContextBar' } 'Workbench returns'
}
function Toggle-State([string]$Id) {
    $element = Find-Id $Id; Assert-Owned $element
    return $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
}
function Owned-Handle {
    $script:ownedProcess.Refresh()
    $handle = $script:ownedProcess.MainWindowHandle
    [uint32]$owner = 0
    [void][HeaderLayoutQaNative]::GetWindowThreadProcessId($handle, [ref]$owner)
    if ($owner -ne $script:ownedProcess.Id) { throw 'Refusing to operate another process window.' }
    return $handle
}
function Scale {
    $dpi = [HeaderLayoutQaNative]::GetDpiForWindow((Owned-Handle))
    if ($dpi -le 0) { throw 'Could not read the owned window DPI.' }
    return $dpi / 96.0
}
function Resize-Owned([int]$WidthDip, [int]$HeightDip = 900) {
    $handle = Owned-Handle; $scale = Scale
    $rect = [HeaderLayoutQaNative+RECT]::new()
    if (-not [HeaderLayoutQaNative]::GetWindowRect($handle, [ref]$rect)) { throw 'Could not read owned window bounds.' }
    $client = [HeaderLayoutQaNative+RECT]::new()
    if (-not [HeaderLayoutQaNative]::GetClientRect($handle, [ref]$client)) { throw 'Could not read owned client bounds.' }
    # Responsive breakpoints use Root.ActualWidth, so compensate for non-client
    # borders instead of treating the outer HWND width as the content width.
    $borderWidth = ($rect.Right - $rect.Left) - ($client.Right - $client.Left)
    $borderHeight = ($rect.Bottom - $rect.Top) - ($client.Bottom - $client.Top)
    $targetWidth = [int][Math]::Ceiling($WidthDip * $scale) + $borderWidth
    $targetHeight = [int][Math]::Ceiling($HeightDip * $scale) + $borderHeight
    if (-not [HeaderLayoutQaNative]::MoveWindow($handle, $rect.Left, $rect.Top, $targetWidth, $targetHeight, $true)) { throw 'Could not resize owned window.' }
    Start-Sleep -Milliseconds 500
    if (-not [HeaderLayoutQaNative]::GetClientRect($handle, [ref]$client)) { throw 'Could not verify owned client bounds.' }
    $actualWidth = ($client.Right - $client.Left) / $scale
    Check ($actualWidth -ge $WidthDip - 0.01 -and $actualWidth -lt $WidthDip + 1) "$WidthDip DIP client width is measured at $([Math]::Round($actualWidth, 3)) DIP"
    $script:measurements.Add(@{ kind = 'client-size'; requestedWidthDip = $WidthDip; actualWidthDip = $actualWidth; requestedHeightDip = $HeightDip; actualHeightDip = ($client.Bottom - $client.Top) / $scale; dpi = $scale * 96 })
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 350
    $handle = Owned-Handle
    $rect = [HeaderLayoutQaNative+RECT]::new()
    if (-not [HeaderLayoutQaNative]::GetWindowRect($handle, [ref]$rect)) { throw 'Could not read owned window bounds.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $printed = [HeaderLayoutQaNative]::PrintWindow($handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $printed) { throw 'PrintWindow did not capture the owned window.' }
        $bitmap.Save((Join-Path $qa $Name)); $script:captures.Add($Name)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Bounds([string]$Id) {
    $element = Find-Id $Id; Assert-Owned $element
    if ($element.Current.IsOffscreen -or $element.Current.BoundingRectangle.IsEmpty) { throw "Control has no visible bounds: $Id" }
    return $element.Current.BoundingRectangle
}
function Check-CommandBar([int]$WidthDip, [string]$Theme) {
    $scale = Scale
    $reference = Bounds 'OpenCadButton'
    $controls = [ordered]@{}
    foreach ($id in @('OpenCadButton', 'SaveButton', 'GenerateButton', 'ExportButton')) {
        $rect = Bounds $id
        Check ([Math]::Abs($rect.Height - $reference.Height) / $scale -le 2) "$WidthDip DIP $Theme $id matches the command height"
        Check ([Math]::Abs(($rect.Top + $rect.Height / 2) - ($reference.Top + $reference.Height / 2)) / $scale -le 2) "$WidthDip DIP $Theme $id shares the command centerline"
        $controls[$id] = @{ x = $rect.X; y = $rect.Y; width = $rect.Width; height = $rect.Height }
    }
    Check ((Find-Id 'ExportButton').Current.Name -eq '导出 NC') "$WidthDip DIP $Theme labels the output command 导出 NC"
    $context = Bounds 'WorkbenchContextBar'
    Check ($context.Height / $scale -le 40) "$WidthDip DIP $Theme keeps the status row compact"
    Check (($context.Bottom - $reference.Top) / $scale -le 94) "$WidthDip DIP $Theme combines file commands and status into a compact shell"
    Check ($null -eq (Find-Id 'PageNavigation')) "$WidthDip DIP $Theme removes the old page navigation"
    foreach ($oldId in @('WorkbenchTab', 'GuideTab', 'SettingsTab')) { Check ($null -eq (Find-Id $oldId)) "$WidthDip DIP $Theme removes $oldId" }
    $script:measurements.Add(@{ kind = 'commands'; widthDip = $WidthDip; theme = $Theme; dpi = $scale * 96; controls = $controls })
}
function Check-WideWorkbench([int]$WidthDip, [string]$Theme) {
    Check (Is-Visible 'ContourList') "$WidthDip DIP $Theme shows the contour inspector"
    Check (Is-Visible 'PlaybackButton') "$WidthDip DIP $Theme shows the canvas and playback"
    Check (Is-Visible 'DiameterBox') "$WidthDip DIP $Theme shows the parameter inspector"
    Check (-not (Is-Visible 'CompactNavigation')) "$WidthDip DIP $Theme hides compact navigation"
    $left = Bounds 'ContourList'; $right = Bounds 'DiameterBox'; $scale = Scale
    $canvasSpan = ($right.Left - $left.Right) / $scale
    Check ($canvasSpan -ge 500) "$WidthDip DIP $Theme preserves at least 500 DIP between the inspectors (measured interior gap $([Math]::Round($canvasSpan, 1)) DIP)"
}
function Check-CentralResults {
    Resize-Owned 1440 940
    Check (Is-Visible 'ResultStrip') 'Collapsed CAD results keep their central tab strip available'
    $strip = Bounds 'ResultStrip'
    $beforeLeft = Bounds 'ContourList'
    $beforeRight = Bounds 'ToolParametersExpander'
    $beforeLeftPanel = Bounds 'LeftPanel'
    $beforeRightPanel = Bounds 'RightPanel'
    Invoke-Id 'OpenResultsButton'
    Wait-Until { Is-Visible 'ResultsTitle' } 'Central results open'
    $afterLeft = Bounds 'ContourList'; $afterRight = Bounds 'ToolParametersExpander'; $scale = Scale
    $afterLeftPanel = Bounds 'LeftPanel'; $afterRightPanel = Bounds 'RightPanel'
    Check ([Math]::Abs($afterLeftPanel.Height - $beforeLeftPanel.Height) / $scale -le 2 -and [Math]::Abs($afterLeftPanel.Top - $beforeLeftPanel.Top) / $scale -le 2) 'Opening central results preserves the entire left panel height and position'
    Check ([Math]::Abs($afterRightPanel.Height - $beforeRightPanel.Height) / $scale -le 2 -and [Math]::Abs($afterRightPanel.Top - $beforeRightPanel.Top) / $scale -le 2) 'Opening central results preserves the entire right panel height and position'
    Check ([Math]::Abs($afterLeft.Height - $beforeLeft.Height) / $scale -le 2 -and [Math]::Abs($afterLeft.Top - $beforeLeft.Top) / $scale -le 2) 'Opening central results preserves the contour list height and position'
    Check ([Math]::Abs($afterRight.Top - $beforeRight.Top) / $scale -le 2 -and [Math]::Abs($afterRight.Height - $beforeRight.Height) / $scale -le 2) 'Opening central results preserves the tool group bounds'
    Check ($strip.Left -ge $afterLeft.Right - 2 * $scale -and $strip.Right -le $afterRight.Left + 2 * $scale) 'The result strip stays between the CAD inspectors'
    $tabs = Bounds 'ResultTabs'
    Check ($tabs.Left -ge $afterLeft.Right - 2 * $scale -and $tabs.Right -le $afterRight.Left + 2 * $scale) 'Expanded result tabs stay between the CAD inspectors'
    $tab = Find-Id 'NcCodeTab'; Assert-Owned $tab
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Id 'ExpandResultsButton'
    Wait-Until { -not (Is-Visible 'PlaybackButton') -and (Is-Visible 'CopyNcButton') } 'Source expands into the central large view'
    Check ((Is-Visible 'ContourList') -and (Is-Visible 'ToolParametersExpander')) 'Large source view preserves both CAD inspectors'
    $largeRightPanel = Bounds 'RightPanel'; $largeLeftPanel = Bounds 'LeftPanel'
    Check ([Math]::Abs($largeRightPanel.Height - $beforeRightPanel.Height) / $scale -le 2 -and [Math]::Abs($largeLeftPanel.Height - $beforeLeftPanel.Height) / $scale -le 2) 'The large source view keeps both entire CAD inspectors at their original full height'
    Capture '1440-light-central-source-expanded.png'
    Invoke-Id 'ExpandResultsButton'
    Wait-Until { (Is-Visible 'PlaybackButton') -and (Is-Visible 'ResultsTitle') } 'Returning from large source restores the preview and results'
    Check ((Find-Id 'ExportButton').Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled) 'Result layout changes preserve valid output and playback'
    Invoke-Element (Find-Name '收起结果区')
    Wait-Until { -not (Is-Visible 'ResultsTitle') -and (Is-Visible 'ResultStrip') } 'Collapsing results leaves the result strip available'
}
function Check-MediumWorkbench([int]$WidthDip, [string]$Theme) {
    Check (Is-Visible 'PlaybackButton') "$WidthDip DIP $Theme keeps the canvas visible"
    Check (-not (Is-Visible 'CompactNavigation')) "$WidthDip DIP $Theme hides compact navigation"
    $visibleInspectors = @(@('ContourList', 'DiameterBox') | Where-Object { Is-Visible $_ })
    Check ($visibleInspectors.Count -eq 1) "$WidthDip DIP $Theme shows exactly one inspector beside the canvas"
    if (Is-Visible 'ContourList') { Invoke-Id 'ShowRightButton'; $expected = 'DiameterBox'; $hidden = 'ContourList' }
    else { Invoke-Id 'ShowLeftButton'; $expected = 'ContourList'; $hidden = 'DiameterBox' }
    Wait-Until { (Is-Visible $expected) -and -not (Is-Visible $hidden) } 'Medium inspector switches'
    Check ((Is-Visible 'PlaybackButton') -and (Is-Visible $expected) -and -not (Is-Visible $hidden)) "$WidthDip DIP $Theme switches inspectors without hiding the canvas"
}
function Check-NarrowWorkbench([int]$WidthDip, [string]$Theme) {
    Check (Is-Visible 'CompactNavigation') "$WidthDip DIP $Theme shows compact navigation"
    $buttons = @('PreviewPanelButton', 'ContoursPanelButton', 'ParametersPanelButton', 'ResultsPanelButton')
    foreach ($button in $buttons) { Check ($null -ne (Find-Id $button)) "$WidthDip DIP $Theme exposes $button" }
    foreach ($scenario in @(
        @{ button = 'PreviewPanelButton'; probe = 'PlaybackButton' },
        @{ button = 'ContoursPanelButton'; probe = 'ContourList' },
        @{ button = 'ParametersPanelButton'; probe = 'DiameterBox' },
        @{ button = 'ResultsPanelButton'; probe = 'ResultsTitle' }
    )) {
        if ((Toggle-State $scenario.button) -ne [Windows.Automation.ToggleState]::On) { Invoke-Id $scenario.button }
        Wait-Until { Is-Visible $scenario.probe } "$($scenario.button) view appears"
        $selected = @($buttons | Where-Object { (Toggle-State $_) -eq [Windows.Automation.ToggleState]::On })
        Check ($selected.Count -eq 1 -and $selected[0] -eq $scenario.button) "$WidthDip DIP $Theme $($scenario.button) is the only selected compact view"
        $otherProbes = @('PlaybackButton', 'ContourList', 'DiameterBox', 'ResultsTitle') | Where-Object { $_ -ne $scenario.probe }
        Check (@($otherProbes | Where-Object { Is-Visible $_ }).Count -eq 0) "$WidthDip DIP $Theme $($scenario.button) hides the other work views"
    }
    Invoke-Id 'PreviewPanelButton'
}
function Check-ShortContourDetails {
    Resize-Owned 800 600
    Invoke-Id 'ContoursPanelButton'
    Wait-Until { Is-Visible 'ContourList' } 'Short window opens the contour view'
    $list = Find-Id 'ContourList'; Assert-Owned $list
    $items = @($list.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -eq 0) { throw 'The short-window fixture has no contour to select.' }
    Assert-Owned $items[0]
    $items[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Until { Is-Visible 'SelectedOutsideButton' } 'Short window exposes selected-contour details'
    $scale = Scale
    Check ((Bounds 'ContourList').Height / $scale -ge 64) 'Selecting a contour at 800 by 600 client DIP retains at least 64 DIP of list height'
    $details = Find-Id 'ContourDetailScroll'; Assert-Owned $details
    $scroll = $details.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    Check ($scroll.Current.VerticallyScrollable) 'Short-window contour details have an independent vertical scrollbar'
    $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, 100)
    Wait-Until { $locate = Find-Name '定位选中轮廓'; $null -ne $locate -and -not $locate.Current.IsOffscreen } 'Scrolling selected details exposes the locate action'
    Check (Is-Visible 'VisibleContoursButton') 'Short-window scrolling keeps batch operations accessible'
    Capture '800x600-light-selected-contour-scroll.png'
    foreach ($selected in @($list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())) {
        Assert-Owned $selected
        $selected.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).RemoveFromSelection()
    }
    Resize-Owned 820 900
    Invoke-Id 'PreviewPanelButton'
}
function Check-SettingsCenter([int]$WidthDip, [string]$Theme) {
    $scale = Scale
    $viewport = $script:window.Current.BoundingRectangle
    $rect = Bounds 'SettingsLayout'
    $left = ($rect.Left - $viewport.Left) / $scale
    $right = ($viewport.Right - $rect.Right) / $scale
    Write-Output "MEASURE $WidthDip DIP $Theme settings left=$([Math]::Round($left,1)) right=$([Math]::Round($right,1)) width=$([Math]::Round($rect.Width / $scale,1))"
    Check ([Math]::Abs($left - $right) -le 12) "$WidthDip DIP $Theme settings have balanced margins"
    Check ($rect.Width / $scale -le 960.5) "$WidthDip DIP $Theme settings stay within 960 DIP"
    Check ($left -ge 0 -and $right -ge 0) "$WidthDip DIP $Theme settings fit the window"
    $script:measurements.Add(@{ kind = 'settings'; widthDip = $WidthDip; theme = $Theme; dpi = $scale * 96; leftDip = $left; rightDip = $right; contentWidthDip = $rect.Width / $scale })
}
function Set-Theme([string]$Label, [string]$StoredValue) {
    Open-Settings
    $theme = Find-Id 'SettingsTheme'; Assert-Owned $theme
    $theme.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Wait-Until { $null -ne (Find-Name $Label) } "Theme option $Label appears"
    $option = Find-Name $Label; Assert-Owned $option
    $selection = $null
    while ($null -ne $option -and -not $option.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        $option = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($option); Assert-Owned $option
    }
    $selection.Select()
    Wait-Until { (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).theme -eq $StoredValue } "Theme $StoredValue is saved only to the QA profile"
    Check (Is-Visible 'SettingsBackButton') "Changing to $StoredValue keeps settings open"
    Return-Workbench 'SettingsBackButton'
}
function Write-Report([string]$Status, [string]$Failure = '') {
    [ordered]@{
        status = $Status; failure = $Failure; executable = $exe; qaDirectory = $qa
        processId = if ($script:ownedProcess) { $script:ownedProcess.Id } else { $null }
        checks = $script:checks.ToArray(); measurements = $script:measurements.ToArray(); screenshots = $script:captures.ToArray()
        isolation = 'Owned process with CAD2NC_SETTINGS_DIRECTORY under the workspace; no file pickers or installation'
        visualReview = 'Required for typography, Fluent styling, status badges, and focus treatment'
        physicalMouseHitTestingVerified = $false
        sizeBasis = 'Client DIP: outer window dimensions include measured non-client borders'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa 'report.json') -Encoding utf8
}

try {
    Assert-NoExistingTarget
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'; $start.WorkingDirectory = $qa
    $start.Environment['CAD2NC_SETTINGS_DIRECTORY'] = $settingsDirectory
    [void]$start.Environment.Remove('DOTNET_ROOT'); [void]$start.Environment.Remove('DOTNET_ROOT_X64')
    $start.ArgumentList.Add($drawingPath)
    $script:ownedProcess = [Diagnostics.Process]::Start($start)
    Wait-Until { $script:ownedProcess.Refresh(); $script:ownedProcess.MainWindowHandle -ne 0 } 'Owned CAD2NC starts'
    $script:window = [Windows.Automation.AutomationElement]::FromHandle($script:ownedProcess.MainWindowHandle)
    Wait-Until { Is-Visible 'WorkbenchContextBar' } 'Compact workbench is ready'
    Wait-Until { $button = Find-Id 'GenerateButton'; $null -ne $button -and $button.Current.IsEnabled } 'Clean fixture drawing is ready'

    Resize-Owned 1440 940
    Invoke-Id 'GenerateButton'
    Wait-Until { $button = Find-Id 'ExportButton'; $null -ne $button -and $button.Current.IsEnabled -and (Find-Id 'PlaybackButton').Current.IsEnabled } 'Clean fixture generation succeeds'
    Check (-not (Is-Visible 'ResultsTitle')) '1440 DIP clean success keeps the results drawer collapsed'
    Check ((Find-Id 'GenerateButton').Current.Name -eq '重新生成') 'Successful generation changes the generation command to 重新生成'
    Check ((Find-Id 'OpenResultsButton').Current.IsEnabled) 'Clean success exposes 查看结果 without opening it'
    Check ((Find-Id 'PrintLabelButton').Current.IsEnabled) 'Fresh valid toolpaths enable motherboard label printing'
    Check (-not (Is-Visible 'StalePlanBadge')) 'A freshly generated plan has no stale badge'
    Capture '1440-light-clean-success-collapsed.png'
    Check-CentralResults
    Resize-Owned 1366 768
    Check (-not (Is-Visible 'ResultsTitle')) '1366 by 768 DIP clean success keeps the results drawer collapsed'
    Check-WideWorkbench 1366 'Light'
    Capture '1366-light-clean-success-collapsed.png'

    foreach ($scenario in @(
        @{ width = 1440; theme = 'Dark'; label = '深色' },
        @{ width = 1440; theme = 'Light'; label = '浅色' },
        @{ width = 1366; theme = 'Light'; label = '浅色' },
        @{ width = 1366; theme = 'Dark'; label = '深色' },
        @{ width = 1000; theme = 'Dark'; label = '深色' },
        @{ width = 1000; theme = 'Light'; label = '浅色' },
        @{ width = 820; theme = 'Dark'; label = '深色' },
        @{ width = 820; theme = 'Light'; label = '浅色' }
    )) {
        Resize-Owned $scenario.width
        if ((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).theme -ne $scenario.theme) { Set-Theme $scenario.label $scenario.theme }
        Check-CommandBar $scenario.width $scenario.theme
        if ($scenario.width -ge 1200) { Check-WideWorkbench $scenario.width $scenario.theme }
        elseif ($scenario.width -ge 900) { Check-MediumWorkbench $scenario.width $scenario.theme }
        else { Check-NarrowWorkbench $scenario.width $scenario.theme }
        Capture "$($scenario.width)-$($scenario.theme.ToLowerInvariant())-workbench.png"

        Open-Settings
        Check-SettingsCenter $scenario.width $scenario.theme
        Capture "$($scenario.width)-$($scenario.theme.ToLowerInvariant())-settings.png"
        Return-Workbench 'SettingsBackButton'
    }

    foreach ($width in @(899, 900, 1199, 1200, 1199, 900, 899, 1200)) {
        Resize-Owned $width
        if ($width -ge 1200) { Check-WideWorkbench $width 'Light' }
        elseif ($width -ge 900) { Check-MediumWorkbench $width 'Light' }
        else { Check-NarrowWorkbench $width 'Light' }
        Check ((Find-Id 'ExportButton').Current.IsEnabled) "$width DIP round trip preserves output eligibility"
    }
    Capture '1200-light-boundary-return.png'
    Check-ShortContourDetails

    Open-Guide
    Check (Is-Visible 'ReviewProgressText') 'Guidance remains reachable through More without opening a file'
    Check ($null -eq (Find-Id 'GuideTab')) 'Guidance no longer exposes a permanent page tab'
    Capture '820-light-guide.png'
    Return-Workbench 'GuideBackButton'
    Check (Is-Visible 'WorkbenchContextBar') 'Back to workbench restores the task context bar'
    Write-Report 'passed'
    Write-Output "HEADER_LAYOUT_QA_PASSED $qa"
}
catch {
    $failure = $_.Exception.Message
    if ($script:ownedProcess -and -not $script:ownedProcess.HasExited -and $script:window) {
        try { Capture 'failure.png' } catch { }
    }
    Write-Report 'failed' $failure
    throw
}
finally {
    if ($script:ownedProcess -and -not $script:ownedProcess.HasExited) { $script:ownedProcess.Kill(); $script:ownedProcess.WaitForExit() }
    if ($script:ownedProcess) { $script:ownedProcess.Dispose() }
}
