#Requires -Version 7.0
<#
.SYNOPSIS
HISTORICAL v2.5 tool: not a v2.6.4 acceptance suite. See docs/DEVELOPMENT.md.
Checks v2.5 playback, source expansion, and stale-result state using one isolated process.
.NOTES
An explicit -Drawing DWG/DXF is required and expected to contain three included contours and one stock
contour. UIA actions do not verify physical mouse hit testing. Captured stale-result
opacity, path geometry, and line highlighting require separate visual review.
#>
param([string]$Executable = 'dist/CAD2NC/CAD2NC.exe', [string]$Drawing)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$exe = [IO.Path]::GetFullPath($Executable, $workspace)
if ([string]::IsNullOrWhiteSpace($Drawing)) { throw 'Supply -Drawing <DWG/DXF> with 3 included contours and 1 stock contour. This is a historical v2.5 test.' }
$sourceDrawing = [IO.Path]::GetFullPath($Drawing, $workspace)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable does not exist: $exe" }
if (-not (Test-Path -LiteralPath $sourceDrawing -PathType Leaf)) { throw "Drawing does not exist: $sourceDrawing" }
if (Get-Process CAD2NC -ErrorAction SilentlyContinue) { throw 'An existing CAD2NC window is open; it will not be interrupted.' }
$qa = Join-Path $workspace ('build/qa-playback-canvas-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $qa 'profile'
New-Item -ItemType Directory -Force -Path $profile | Out-Null
$settings = @{ schemaVersion = 1; lastConfig = @{ tool = @{}; machining = @{} }; lastView = @{}; theme = 'Dark'; presets = @(); recentTasks = @(); taskDirectory = (Join-Path $qa '任务'); ncDirectory = (Join-Path $qa 'NC') }
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
$drawingPath = Join-Path $qa ('异型板图' + [IO.Path]::GetExtension($sourceDrawing))
Copy-Item -LiteralPath $sourceDrawing -Destination $drawingPath
$checks = [Collections.Generic.List[string]]::new()
$captures = [Collections.Generic.List[string]]::new()
$measurements = [Collections.Generic.List[object]]::new()
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PlaybackQaNative {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd,int x,int y,int width,int height,bool repaint);
}
'@
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'; $start.WorkingDirectory = $qa
$start.Environment['CAD2NC_SETTINGS_DIRECTORY'] = $profile
[void]$start.Environment.Remove('DOTNET_ROOT'); [void]$start.Environment.Remove('DOTNET_ROOT_X64')
$start.ArgumentList.Add($drawingPath)
$process = [Diagnostics.Process]::Start($start)
function Expect([bool]$condition, [string]$message) { if (-not $condition) { throw $message }; $checks.Add($message); Write-Output "PASS $message" }
function Until([scriptblock]$condition, [string]$message, [int]$seconds=25) {
 $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
 while ([DateTime]::UtcNow -lt $deadline) { if ($process.HasExited) { throw "GUI exited $($process.ExitCode)" }; if (& $condition) { return }; Start-Sleep -Milliseconds 150 }
 throw "Timed out: $message"
}
function FindId([string]$id) { $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) }
function FindName([string]$name) { $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)) }
function IsVisible([string]$id) { $element = FindId $id; return $null -ne $element -and -not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty }
function ResizeOwned([int]$widthDip, [int]$heightDip = 940) {
 $rect = [PlaybackQaNative+RECT]::new(); $client = [PlaybackQaNative+RECT]::new()
 $handle = $process.MainWindowHandle
 $scale = [PlaybackQaNative]::GetDpiForWindow($handle) / 96.0
 if ($scale -le 0 -or -not [PlaybackQaNative]::GetWindowRect($handle, [ref]$rect) -or -not [PlaybackQaNative]::GetClientRect($handle, [ref]$client)) { throw 'Could not read owned window DPI or bounds.' }
 $width = [int][Math]::Ceiling($widthDip * $scale) + ($rect.Right - $rect.Left) - ($client.Right - $client.Left)
 $height = [int][Math]::Ceiling($heightDip * $scale) + ($rect.Bottom - $rect.Top) - ($client.Bottom - $client.Top)
 if (-not [PlaybackQaNative]::MoveWindow($handle, $rect.Left, $rect.Top, $width, $height, $true)) { throw 'Could not resize owned window.' }
 Start-Sleep -Milliseconds 450
 if (-not [PlaybackQaNative]::GetClientRect($handle, [ref]$client)) { throw 'Could not verify owned client bounds.' }
 $actualWidth = ($client.Right - $client.Left) / $scale
 Expect ($actualWidth -ge $widthDip - .01 -and $actualWidth -lt $widthDip + 1) "$widthDip client DIP is measured at $([Math]::Round($actualWidth, 3)) DIP"
 $measurements.Add(@{ widthDip = $widthDip; actualWidthDip = $actualWidth; heightDip = $heightDip; actualHeightDip = ($client.Bottom - $client.Top) / $scale; dpi = $scale * 96 })
}
function InvokeId([string]$id) {
 Until { $null -ne (FindId $id) -and (FindId $id).Current.IsEnabled } "Button $id ready after popup transition"
 $element = FindId $id
 if ($element.Current.ProcessId -ne $process.Id) { throw 'Refusing to invoke another process UI' }
 $invoke = $null
 if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke(); return }
 $toggle = $null
 if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) { $toggle.Toggle(); return }
 throw "Control has no invoke or toggle pattern: $id"
}
function SetNumber([string]$id, [double]$value) {
 $element = FindId $id
 if ($null -eq $element -or $element.Current.ProcessId -ne $process.Id) { throw "Missing owned parameter $id" }
 $candidates = @($element) + @($element.FindAll([Windows.Automation.TreeScope]::Descendants,
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
 foreach ($candidate in $candidates) {
  $pattern = $null
  if ($candidate.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($value); return }
  if ($candidate.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
   $pattern.SetValue($value.ToString([Globalization.CultureInfo]::CurrentCulture))
   (FindId 'ToolTypeBox').SetFocus()
   Start-Sleep -Milliseconds 250
   return
  }
}
throw "Parameter $id has no writable value pattern"
}
function PlaybackProgressElement {
 $element = FindId 'PlaybackProgress'
 if ($null -eq $element) { $element = FindName 'NC 播放进度' }
 $element
}
function Progress {
 $element = PlaybackProgressElement
 if ($null -eq $element) { return -1 }
 $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
}
function Seek([double]$value) {
 Until { $null -ne (PlaybackProgressElement) } 'Playback progress available'
 (PlaybackProgressElement).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($value)
}
function Capture([string]$name) {
 Start-Sleep -Milliseconds 350
 $rect = [PlaybackQaNative+RECT]::new(); [void][PlaybackQaNative]::GetWindowRect($process.MainWindowHandle,[ref]$rect)
 $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics = [Drawing.Graphics]::FromImage($bitmap); $dc=$graphics.GetHdc()
 try { [void][PlaybackQaNative]::PrintWindow($process.MainWindowHandle,$dc,2) }
 finally { $graphics.ReleaseHdc($dc) }
 try { $bitmap.Save((Join-Path $qa $name)); $captures.Add($name) }
 finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function WriteReport([string]$status, [string]$failure = '') {
 [ordered]@{
  status = $status; failure = $failure; executable = $exe; drawing = $sourceDrawing
  qaDirectory = $qa; processId = $process.Id; checks = $checks.ToArray(); screenshots = $captures.ToArray()
  measurements = $measurements.ToArray(); sizeBasis = 'Client DIP'; physicalMouseHitTestingVerified = $false
  visualReview = 'Required for stale opacity, source highlight, paths, and marker geometry'
 } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $qa 'report.json') -Encoding utf8
}
try {
 Until { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'Window'
 $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 Until { $null -ne (FindId 'GenerateButton') -and (FindId 'GenerateButton').Current.IsEnabled } 'DWG loaded'
 ResizeOwned 1440
 $contextBar = FindId 'WorkbenchContextBar'
 $generateButton = FindId 'GenerateButton'
 Expect ($null -ne $contextBar -and -not $contextBar.Current.IsOffscreen -and $null -ne $generateButton -and -not $generateButton.Current.IsOffscreen) 'Opening a drawing starts in the canvas workbench'
 Expect (-not (FindId 'PlaybackButton').Current.IsEnabled) 'Playback disabled before generation'
 Expect (-not (IsVisible 'StalePlanBadge')) 'No stale badge is shown before any plan exists'
 $closeParameters = FindName '收起参数面板'
 if ($null -eq $closeParameters -or $closeParameters.Current.IsOffscreen) { InvokeId 'ParametersPanelButton' }
 # With the fixed 3 mm step-down default, a -7 mm depth creates three layers.
 SetNumber 'CuttingDepthBox' -7
 InvokeId 'GenerateButton'
 Until { (FindId 'PlaybackButton').Current.IsEnabled } 'Generated playback ready'
 Expect (-not (IsVisible 'StalePlanBadge')) 'A current plan has no stale badge'
 Expect (-not (FindName '收起参数面板').Current.IsOffscreen -and -not (FindId 'ResultsTitle').Current.IsOffscreen) 'Generation shows results while keeping parameters visible'
 Expect ((Progress) -eq 0 -and (FindId 'PlaybackButton').Current.Name -eq '播放 NC') 'Generated program waits for manual play'
 $positionBounds = (FindId 'PlaybackPositionText').Current.BoundingRectangle
 $playbackStatusBounds = (FindId 'PlaybackStatusText').Current.BoundingRectangle
 Expect ($positionBounds.Top -gt $playbackStatusBounds.Bottom) 'XYZ position is presented in the bottom status bar'
 Expect ((FindId 'SelectionSummary').Current.Name -eq '3 / 4 个轮廓') 'Real DWG has three included contours'
 Capture 'ready.png'
 InvokeId 'PlaybackButton'
 Until { (Progress) -gt .05 } 'Playback advances'
 Expect ((FindId 'PlaybackButton').Current.Name -eq '暂停 NC') 'Playing state is visible'
 InvokeId 'PlaybackButton'
 Start-Sleep -Milliseconds 100
 $paused = Progress
 Start-Sleep -Milliseconds 450
 Expect ((Progress) -eq $paused) 'Pause freezes progress'
 InvokeId 'PlaybackButton'
 Seek 30
 Until { (FindId 'PlaybackButton').Current.Name -eq '播放 NC' } 'Seek pauses playback'
 Expect ([Math]::Abs((Progress)-30) -lt .01) 'Seek updates exact progress'
 $position30 = (FindId 'PlaybackPositionText').Current.Name
 $ncTab = FindId 'NcCodeTab'
 $ncTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Seek 31
 Capture 'playing-path-1.png'
 Seek 65
 Expect ((FindId 'PlaybackPositionText').Current.Name -ne $position30) 'Seek changes XYZ position'
 $beforeExpandedPosition = (FindId 'PlaybackPositionText').Current.Name
 $beforeExpandedProgress = Progress
 InvokeId 'ExpandResultsButton'
 Until { -not (IsVisible 'PlaybackButton') -and (IsVisible 'CopyNcButton') } 'Source opens in the central large view'
 Capture 'source-expanded.png'
 InvokeId 'ExpandResultsButton'
 Until { (IsVisible 'PlaybackButton') -and (IsVisible 'CopyNcButton') } 'Returning from source restores preview and code'
 Expect ((Progress) -eq $beforeExpandedProgress -and (FindId 'PlaybackPositionText').Current.Name -eq $beforeExpandedPosition) 'Large source round trip preserves exact progress and XYZ'
 Capture 'playing-path-2.png'
 Seek 100
 Until { (FindId 'PlaybackStatusText').Current.Name -match '轮廓 3.*第 3/3 层' } 'Last contour and depth layer'
 Expect ((FindId 'PlaybackPositionText').Current.Name -match 'Z 5.000 mm') 'Program finishes at safe Z'
 Capture 'completed.png'
 InvokeId 'PlaybackButton'
 Until { (Progress) -gt 0 -and (Progress) -lt 10 } 'Replay restarts from beginning'
 InvokeId 'PlaybackButton'
 InvokeId 'PlaybackResetButton'
 Expect ((Progress) -eq 0) 'Reset rewinds playback'
 $speed = FindId 'PlaybackSpeed'
 $speed.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Start-Sleep -Milliseconds 200
 $ten = FindName '10×'
 $ten.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 InvokeId 'PlaybackButton'
 Start-Sleep -Milliseconds 600
 InvokeId 'PlaybackButton'
 Expect ((Progress) -gt .3) '10x speed advances faster'
 Seek 45
 ResizeOwned 820 900
 $preview = FindId 'PreviewPanelButton'
 if ($null -ne $preview -and -not $preview.Current.IsOffscreen) { InvokeId 'PreviewPanelButton' }
 Expect ((FindId 'PlaybackButton').Current.IsEnabled -and -not (FindId 'PlaybackButton').Current.IsOffscreen) 'Playback remains accessible in narrow window'
 Capture 'narrow.png'
 $includeCondition = [System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'参与加工'),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::CheckBox))
 # Restore width so the contour list is available, then invalidate the plan by editing it.
 ResizeOwned 1440
 $order = FindId 'ContourOrderToggle'
 Expect ($null -ne $order -and $order.Current.IsEnabled) 'A valid plan exposes machining-order viewing'
 InvokeId 'ContourOrderToggle'
 Expect ($order.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On) 'Machining order can be inspected for a valid plan'
 Expect ((FindId 'PlaybackButton').Current.IsEnabled -and (FindId 'ExportButton').Current.IsEnabled) 'Viewing machining order preserves playback and output'
 $included=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$includeCondition)
 $included.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
 Until { -not (FindId 'PlaybackButton').Current.IsEnabled -and (IsVisible 'StalePlanBadge') } 'Changing machining inclusion clears playback and marks the displayed result stale'
 Expect ((FindId 'StalePlanBadge').Current.Name -eq '上次结果 · 待更新') 'Old geometry carries an explicit stale-result label'
 Expect ((FindId 'ContourOrderToggle').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'Invalidating a plan returns machining-order viewing to all contours'
 Expect ((Progress) -eq 0 -and (FindId 'PlaybackPositionText').Current.Name -eq '位置 —') 'Old program marker and progress are cleared'
 Capture 'invalidated.png'
 InvokeId 'GenerateButton'
 Until { (FindId 'PlaybackButton').Current.IsEnabled -and -not (IsVisible 'StalePlanBadge') } 'Successful regeneration restores playback and removes the stale badge'
 WriteReport 'passed'
 Write-Output "PLAYBACK_QA_PASSED $qa"
}
catch {
 $failure = $_.Exception.Message
 try { Capture 'failure.png' } catch { }
 WriteReport 'failed' $failure
 throw
}
finally {
 if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
 $process.Dispose()
}
