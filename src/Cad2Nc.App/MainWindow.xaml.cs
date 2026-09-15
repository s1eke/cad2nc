using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Cad2Nc.Application;
using Cad2Nc.App.Controls;
using Cad2Nc.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization.NumberFormatting;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Cad2Nc.App;

public sealed partial class MainWindow : Window
{
    // Optional isolated profile for portable deployments and GUI regression checks.
    // Normal launches continue to use the Windows per-user settings location.
    private readonly WorkbenchSession _session = new(CreateUserStore());
    private static UserStore CreateUserStore()
    {
        var directory = Environment.GetEnvironmentVariable("CAD2NC_SETTINGS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return new();
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("CAD2NC_SETTINGS_DIRECTORY 须为完整路径。");
        return new(directory);
    }
    private readonly FileOpenGate _openGate = new();
    private readonly ObservableCollection<ContourRow> _filteredContours = [];
    private readonly ObservableCollection<DiagnosticGroup> _diagnosticGroups = [];
    private string _diagnosticKey = "";
    private bool _syncing = true, _refreshQueued, _closingApproved, _closeDialogOpen, _dialogOpen;
    private int _layoutBand = -1;
    private string? _narrowPanel;
    private CadDocument? _shownDocument;
    private ToolpathPlan? _scenePlan;
    private BatchNcGenerationResult? _sceneBatchResult;
    private NcPlaybackProgram? _sceneGeneratedPlayback;
    private CadDocument? _sceneDocument;
    private string _sceneSelection = "";
    private ConversionConfig? _shownConfig;
    private bool _rootLoaded, _pickerOpen, _confirmingDiscard;
    private IReadOnlyList<string>? _shownNc;
    private string? _presentedValidationField;
    private ScenePoint _displayOrigin;
    private readonly Dictionary<string, NumberBox> _numberBoxes;
    private static readonly Dictionary<string, string> FieldNames = new()
    {
        ["ToolNumber"] = "刀号", ["Diameter"] = "刀径", ["SpindleSpeed"] = "主轴转速",
        ["CuttingFeed"] = "切削进给", ["CuttingDepth"] = "加工深度", ["EdgeAllowance"] = "封边吃料",
        ["SafeZ"] = "安全高度", ["PlungeFeed"] = "下刀进给", ["LeaveSkin"] = "留皮厚度", ["MaxStepDown"] = "单层下切", ["ToolType"] = "刀具名称", ["LengthOffset"] = "刀长补偿 H",
        ["BoardThickness"] = "板厚", ["ZReference"] = "Z 零点", ["OriginMode"] = "XY 原点", ["Profile"] = "机床配置",
        ["SheetContourId"] = "母板", ["ManualOrigin"] = "手动原点", ["ManualOriginX"] = "原点 X", ["ManualOriginY"] = "原点 Y",
        ["EntryMode"] = "下刀方式", ["RampLength"] = "斜坡长度", ["RampFeed"] = "斜坡进给", ["PrioritizePartStability"] = "优先保持零件稳定", ["ToolChange"] = "换刀输出",
        ["ParkX"] = "停车 X", ["ParkY"] = "停车 Y", ["ParkZ"] = "停车 Z"
    };

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "CAD2NC.ico"));
        InitializeTitleBar();
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var initialWidth = (int)Math.Min(1440 * dpi, workArea.Width - 24 * dpi);
        var initialHeight = (int)Math.Min(940 * dpi, workArea.Height - 24 * dpi);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(480, initialWidth), Math.Max(360, initialHeight)));
        AppWindow.Move(new Windows.Graphics.PointInt32(workArea.X + (workArea.Width - initialWidth) / 2, workArea.Y + (workArea.Height - initialHeight) / 2));
        _numberBoxes = new()
        {
            ["ToolNumber"] = ToolNumberBox, ["LengthOffset"] = LengthOffsetBox, ["Diameter"] = DiameterBox, ["SpindleSpeed"] = SpindleSpeedBox,
            ["CuttingFeed"] = CuttingFeedBox, ["CuttingDepth"] = CuttingDepthBox, ["EdgeAllowance"] = EdgeAllowanceBox,
            ["SafeZ"] = SafeZBox, ["PlungeFeed"] = PlungeFeedBox, ["LeaveSkin"] = StepDownBox,
            ["BoardThickness"] = BoardThicknessBox, ["RampLength"] = RampLengthBox, ["RampFeed"] = RampFeedBox,
            ["ParkX"] = ParkXBox, ["ParkY"] = ParkYBox, ["ParkZ"] = ParkZBox,
            ["ManualOriginX"] = ManualOriginXBox, ["ManualOriginY"] = ManualOriginYBox
        };
        StepDownBox.NumberFormatter = new DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = 1,
            IsGrouped = false,
            IsDecimalPointAlwaysDisplayed = true,
            NumberRounder = new IncrementNumberRounder
            {
                Increment = 0.1,
                RoundingAlgorithm = RoundingAlgorithm.RoundHalfAwayFromZero
            }
        };
        StepDownBox.LostFocus += Parameter_LostFocus;
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), handledEventsToo: true);
        SetConfigurationSource(ConfigurationDefaults.Name(_session.Config.Machine.Profile) + " · 启动参数", _session.Config);
        ContourList.ItemsSource = _filteredContours;
        DiagnosticList.ItemsSource = _diagnosticGroups;
        ResultTabs.SelectedItem = DiagnosticTab;
        LayerFilter.Items.Add("全部图层"); LayerFilter.SelectedIndex = 0;
        _session.PropertyChanged += Session_Changed;
        _session.Contours.CollectionChanged += (_, _) => QueueRefresh();
        Preview.ContourPicked += Canvas_Picked;
        Preview.CoordinateChanged += (_, point) =>
        {
            if (_session.Playback.Frame is null)
                PlaybackPositionText.Text = $"X {point.X - _displayOrigin.X:0.000}  Y {point.Y - _displayOrigin.Y:0.000}  mm";
        };
        Preview.ViewportChanged += (_, view) =>
        {
            if (!_syncing && _session.Document is not null)
                _session.CurrentView = _session.CurrentView with { CenterX = view.CenterX, CenterY = view.CenterY, Scale = view.Scale, HasViewport = true };
        };
        Root.Loaded += async (_, _) =>
        {
            if (_rootLoaded) return;
            _rootLoaded = true;
            Refresh();
            var pending = _openGate.MarkReady(out var rejected);
            if (pending is not null) await OpenReservedAsync(pending);
            if (rejected) ShowOpenInProgress();
        };
        Root.SizeChanged += (_, _) => { if (_numberBoxes is not null) ApplyView(); };
        AppWindow.Closing += Window_Closing;
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) DropHighlight.Visibility = Visibility.Collapsed; };
        InitializePlayback();
        Closed += (_, _) => { StopPlaybackTimer(); _labelPrinter.Dispose(); _session.Dispose(); try { _session.Store.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { } Preview.ReleaseResources(); };
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, async () => await OpenCadAsync());
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, async () => await SaveTaskAsync(false));
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, () => { if (!ConfigurationActionBlocked && _session.CanUndo) { _shownConfig = null; _session.Undo(); } return Task.CompletedTask; });
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, () => { if (!ConfigurationActionBlocked && _session.CanRedo) { _shownConfig = null; _session.Redo(); } return Task.CompletedTask; });
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, GenerateAsync);
        SyncParameters(); ApplyView(); _syncing = false; Refresh();
    }

    internal void HandleActivation(FileOpenRequest request)
    {
        if (_closingApproved) return;
        if (_rootLoaded)
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            Activate();
            _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        if (!request.IsActivationOnly) _ = RequestOpenAsync(request);
    }
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, args) => { args.Handled = true; await action(); };
        Root.KeyboardAccelerators.Add(accelerator);
    }
    private void Session_Changed(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private void Refresh()
    {
        if (_numberBoxes is null) return;
        _syncing = true;
        try
        {
            var documentChanged = !ReferenceEquals(_shownDocument, _session.Document);
            var ncChanged = !ReferenceEquals(_shownNcDocument, _session.NcDocument);
            if (ncChanged)
            {
                _shownNcDocument = _session.NcDocument;
                if (_session.IsNcDocument) { _page = AppPage.Workbench; _showResults = true; _resultsExpanded = !_session.CanPlay; _inspectionPanelOverride = null; _narrowPanel = "results"; _fitSceneAfterLayout = true; SelectResultRoute(WorkbenchResultRoute.Code); }
            }
            if (documentChanged)
            {
                _shownDocument = _session.Document;
                if (_session.Document is not null) { _page = AppPage.Workbench; _showResults = false; _resultsExpanded = false; _showToolpathOrder = false; _inspectionPanelOverride = null; _narrowPanel = null; _fitSceneAfterLayout = !_session.CurrentView.HasViewport; }
                LayerFilter.Items.Clear(); LayerFilter.Items.Add("全部图层");
                foreach (var layer in _session.Contours.Select(c => c.Layer).Distinct().Order()) LayerFilter.Items.Add(layer);
                LayerFilter.SelectedIndex = 0; ContourSearch.Text = "";
                foreach (var row in _session.Contours) row.PropertyChanged += Row_Changed;
                Refilter();
            }
            if (_filteredContours.Count == 0 && _session.Contours.Count > 0 && string.IsNullOrEmpty(ContourSearch.Text)) Refilter();
            UpdateDiagnostics();
            var ui = BuildWorkbenchUiState();
            StatusText.Text = BottomStatus(_session.Status);
            FileHeading.Text = _session.Document is null && !_session.IsNcDocument ? "准备新的加工任务" : _session.FileName;
            ToolTipService.SetToolTip(FileHeading, _session.SourcePath);
            DocumentTitle.Text = _session.IsNcDocument ? _session.FileName + " · 只读 NC" : (_session.TaskPath is null ? (_session.Document is null ? "未命名任务" : _session.FileName) : Path.GetFileNameWithoutExtension(_session.TaskPath)) + (_session.IsDirty ? " · 未保存" : "");
            ToolTipService.SetToolTip(DocumentTitle, _session.TaskPath ?? _session.SourcePath);
            Title = $"{_session.FileName} — CAD2NC";
            SelectionSummary.Text = $"{_session.Contours.Count(c => c.IsIncluded)} / {_session.Contours.Count} 个轮廓";
            SelectionHeader.Visibility = _session.IsNcDocument ? Visibility.Collapsed : Visibility.Visible;
            ResultSummary.Text = ui.ResultText;
            NextActionText.Text = ui.NextActionText;
            DiagnosticBadgeButton.Content = ui.DiagnosticBadgeText;
            DiagnosticBadgeButton.Visibility = ui.HasDiagnostics ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiagnosticBadgeButton,
                ui.HasDiagnostics ? $"查看诊断，{ui.DiagnosticBadgeText}" : "没有诊断信息");
            OpenResultsButton.Content = _session.IsNcDocument ? "程序详情" : "查看结果";
            OpenResultsButton.Visibility = _session.Document is not null || _session.IsNcDocument ? Visibility.Visible : Visibility.Collapsed;
            OpenResultsButton.IsEnabled = _session.IsNcDocument || _session.Plan is not null || _session.IsBatchMode || _diagnosticGroups.Count > 0;
            GenerateButton.Label = ui.GenerateLabel;
            ApplyPrimaryCommand(ui.PrimaryCommand);
            var workbenchVisibility = _page == AppPage.Workbench ? Visibility.Visible : Visibility.Collapsed;
            var editingVisibility = _page == AppPage.Workbench && !_session.IsNcDocument ? Visibility.Visible : Visibility.Collapsed;
            OpenCadButton.Visibility = workbenchVisibility;
            GenerateButton.Visibility = SaveButton.Visibility = SaveAsButton.Visibility = ExportAsButton.Visibility = BatchExportButton.Visibility = UndoButton.Visibility = RedoButton.Visibility = PrintLabelButton.Visibility = editingVisibility;
            ExportButton.Visibility = editingVisibility;
            GenerateButton.IsEnabled = _session.CanGenerate;
            ExportButton.IsEnabled = _session.CanExport && !_fileWriteOpen;
            ExportAsButton.IsEnabled = ExportButton.IsEnabled;
            BatchExportButton.IsEnabled = _session.Document is not null && !_session.IsBusy &&
                ToolpathPlanner.FindSheetCandidates(_session.Document).Count > 1 && _session.ValidationErrors.Count == 0;
            PrintLabelButton.IsEnabled = CanPrintLabels;
            CopyNcButton.IsEnabled = _session.CanCopy;
            SaveButton.IsEnabled = _session.CanSaveTask && !_fileWriteOpen;
            UndoButton.IsEnabled = _session.CanUndo; RedoButton.IsEnabled = _session.CanRedo;
            CancelButton.Visibility = _page == AppPage.Workbench && _session.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            BusyRing.IsActive = _session.IsBusy;
            SyncParameters(); ApplyView();
            if (!ReferenceEquals(_shownNc, _session.NcLines)) { _shownNc = _session.NcLines; SetNcRows(_shownNc); }
            BuildScene(documentChanged || ncChanged);
            RefreshPlaybackProgram();
            UpdatePlanSummary();
            UpdateContourOrderLabels();
            RefreshWorkflow();
            RefreshWorkbenchPresentation();
            if (documentChanged && _session.Document is not null && _session.CurrentView.HasViewport)
                Preview.RestoreViewport(new CanvasViewport(_session.CurrentView.CenterX, _session.CurrentView.CenterY, _session.CurrentView.Scale));
        }
        finally { _syncing = false; }
    }

    private static string BottomStatus(string status)
    {
        string[] transientPrefixes = ["任务已保存", "已导出", "已复制", "已取消", "正在取消", "打开失败", "生成失败", "文件未", "任务未"];
        return transientPrefixes.Any(prefix => status.StartsWith(prefix, StringComparison.Ordinal)) ? status : "就绪";
    }

    private WorkbenchUiState BuildWorkbenchUiState()
    {
        var occurrences = _diagnosticGroups.Sum(group => group.OccurrenceCount);
        var highest = _diagnosticGroups.Count == 0
            ? (DiagnosticSeverity?)null
            : _diagnosticGroups.Max(group => group.Severity);
        return WorkbenchUiState.Create(_session, _diagnosticGroups.Count, occurrences, highest);
    }

    private void ApplyPrimaryCommand(WorkbenchPrimaryCommand primary)
    {
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        GenerateButton.Style = (Style)resources[primary == WorkbenchPrimaryCommand.Generate
            ? "PrimaryCommandBarButtonStyle" : "SecondaryCommandBarButtonStyle"];
        ExportButton.Style = (Style)resources[primary == WorkbenchPrimaryCommand.Export
            ? "PrimaryCommandBarButtonStyle" : "SecondaryCommandBarButtonStyle"];
    }

    private void UpdateContourOrderLabels()
    {
        var orders = _session.CanExport && _session.Plan is { } plan
            ? plan.Toolpaths.Select((path, index) => (path.ContourId, Order: index + 1))
                .GroupBy(item => item.ContourId)
                .ToDictionary(group => group.Key, group => group.Min(item => item.Order), StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        var layerCount = _session.CanExport && _session.Plan is { } exportedPlan
            ? NcGenerator.DepthPasses(exportedPlan.Config).Count
            : 0;
        foreach (var row in _session.Contours)
            row.ToolpathOrderText = orders.TryGetValue(row.Id, out var order)
                ? $"顺序 {order} · {layerCount} 遍"
                : "";
    }

    private void Row_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContourRow.IsIncluded) or nameof(ContourRow.Side)) QueueRefresh();
    }

    private void UpdateDiagnostics()
    {
        var key = string.Join("\n", _session.Diagnostics.Select(d => $"{d.Code}|{d.Severity}|{d.ContourId}|{d.Count}|{d.Message}"))
            + "|NC|" + _session.NcDocument?.SourcePath + _session.NcDocument?.PlaybackError + string.Join("\n", _session.NcDocument?.Notices.Select(n => $"{n.LineIndex}|{n.Message}") ?? []);
        if (key == _diagnosticKey) return;
        _diagnosticKey = key; _diagnosticGroups.Clear();
        foreach (var group in _session.Diagnostics.GroupBy(d => new { d.Code, d.Severity }).OrderByDescending(g => g.Key.Severity))
        {
            var count = group.Sum(d => d.Count);
            var summary = group.Key.Code switch
            {
                "reader.unsupported" => $"CAD 兼容性诊断 · {count} 项，请核对图纸完整性",
                "reader.notice" => $"CAD 读取提示 · {count} 项",
                "reader.reference" => $"CAD 非图形对象与引用提示 · {count} 项",
                _ => count > 1 ? $"{group.First().Message}（共 {count} 项）" : group.First().Message
            };
            var messages = string.Join("\n\n", group.Select(d => d.Message).Distinct());
            var details = count > 1 ? $"原始出现 {count} 次\n\n{messages}" : messages;
            _diagnosticGroups.Add(new(summary, details, group.Key.Severity,
                group.Where(d => d.ContourId is not null).Select(d => d.ContourId!).Distinct().ToArray(), count));
        }
        if (_session.NcDocument is { } ncDocument)
        {
            foreach (var group in ncDocument.Notices.GroupBy(notice => notice.Message))
            {
                var count = group.Count();
                var lines = group.Where(notice => notice.LineIndex is not null).Select(notice => notice.LineIndex!.Value + 1).Distinct().Order().ToArray();
                var first = group.First();
                var summary = count == 1 && first.LineIndex is { } line
                    ? $"第 {line + 1} 行：{first.Message}"
                    : count > 1 ? $"{first.Message}（共 {count} 项）" : first.Message;
                var location = lines.Length == 0 ? "" : $"\n\n出现行：{string.Join("、", lines)}";
                var details = count > 1 ? $"原始出现 {count} 次{location}\n\n{first.Message}" : first.Message;
                _diagnosticGroups.Add(new(summary, details, DiagnosticSeverity.Information, [], count, lines.Select(line => line - 1).ToArray()));
            }
            if (ncDocument.PlaybackError is { } error)
                _diagnosticGroups.Add(new("轨迹无法预览", error, DiagnosticSeverity.Warning, [], 1));
        }
    }
    public sealed record DiagnosticGroup(string Summary, string Details, DiagnosticSeverity Severity, string[] ContourIds, int OccurrenceCount, int[]? SourceLineIndices = null)
    {
        public Visibility LocateVisibility => ContourIds.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SourceLocateVisibility => SourceLineIndices is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LocateDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiagnosticGroup diagnostic) return;
        ContourSearch.Text = ""; LayerFilter.SelectedIndex = 0; Refilter();
        ContourList.SelectedItems.Clear();
        foreach (var row in _filteredContours.Where(c => diagnostic.ContourIds.Contains(c.Id))) ContourList.SelectedItems.Add(row);
        FocusSelectionOnCanvas();
    }

    private void BuildScene(bool fit)
    {
        Preview.IsPlanStale = _session.HasStalePlan && !_session.IsNcDocument;
        if (_session.NcDocument is { } external)
        {
            if (ReferenceEquals(external, _sceneNcDocument)) return;
            _sceneNcDocument = external; _sceneDocument = null; _scenePlan = null; _sceneBatchResult = null; _sceneGeneratedPlayback = null;
            _displayOrigin = new ScenePoint(0, 0); Preview.Origin = _displayOrigin;
            Preview.OriginLabel = external.Program?.WorkCoordinateSystem ?? "NC";
            var ncPaths = new List<ScenePath>();
            var points = new List<ScenePoint>();
            ScenePathKind? kind = null;
            void Flush()
            {
                if (points.Count > 0 && kind is { } pathKind) ncPaths.Add(new("NC", points.ToArray(), pathKind));
                points.Clear();
            }
            if (external.Program is { } program)
                foreach (var motion in program.Motions)
                {
                    var nextKind = motion.Kind == NcMotionKind.Rapid ? ScenePathKind.Rapid : ScenePathKind.Toolpath;
                    var first = new ScenePoint(motion.Start.X, motion.Start.Y);
                    if (nextKind != kind || points.Count >= 8192 || points.Count > 0 && points[^1] != first) Flush();
                    kind = nextKind;
                    foreach (var point in motion.GetPreviewPoints())
                    {
                        var xy = new ScenePoint(point.X, point.Y);
                        if (points.Count == 0 || points[^1] != xy) points.Add(xy);
                    }
                }
            Flush();
            if (ncPaths.Count == 0 && external.Program?.InitialPosition is { } initial)
                ncPaths.Add(new("NC", [new ScenePoint(initial.X, initial.Y)], ScenePathKind.Rapid));
            Preview.EmptyTitle = external.PlaybackError is null ? "未找到可绘制的运动" : "此程序暂不支持轨迹预览";
            Preview.EmptySubtitle = "可在程序详情中查看原文、参数与解析提示";
            Preview.SetFocused([]); Preview.SetScene(ncPaths, true); return;
        }
        _sceneNcDocument = null;
        Preview.EmptyTitle = "打开文件，开始工作";
        Preview.EmptySubtitle = "打开 DWG / DXF 生成刀路，或打开 NC 查看轨迹";
        var document = _session.Document;
        if (document is null) { _sceneDocument = null; _scenePlan = null; _sceneBatchResult = null; _sceneGeneratedPlayback = null; Preview.SetScene([], false); return; }
        var plan = _session.Plan;
        var batchResult = _session.BatchResult;
        // The stale scene keeps the previous program's real rapid moves along with its old rings.
        var generatedProgram = _session.Playback.Program ??
            (!_session.IsBatchMode && ReferenceEquals(_scenePlan, plan) ? _sceneGeneratedPlayback : null);
        var selection = string.Join(";", _session.Contours.Where(c => c.IsIncluded).Select(c => c.Id));
        if (!fit && ReferenceEquals(_sceneDocument, document) && ReferenceEquals(_scenePlan, plan) && ReferenceEquals(_sceneBatchResult, batchResult) && selection == _sceneSelection
            && ReferenceEquals(_sceneGeneratedPlayback, generatedProgram) && _sceneWorkpiece == _session.Workpiece && _sceneOriginMode == _session.Config.OriginMode) return;
        _sceneDocument = document; _scenePlan = plan; _sceneBatchResult = batchResult; _sceneSelection = selection;
        _sceneGeneratedPlayback = generatedProgram;
        _sceneWorkpiece = _session.Workpiece; _sceneOriginMode = _session.Config.OriginMode;
        var paths = new List<ScenePath>();
        var rows = _session.Contours.ToDictionary(c => c.Id);
        var batchSheetIds = batchResult?.Succeeded == true
            ? batchResult.Programs.Select(program => program.SheetContourId).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in document.Contours)
            paths.Add(new ScenePath(c.Id, c.Points.Select(p => new ScenePoint(p.X, p.Y)).ToArray(),
                c.Id == _session.Workpiece.SheetContourId || c.IsOuterCandidate ? ScenePathKind.Stock : ScenePathKind.Source,
                rows.GetValueOrDefault(c.Id)?.IsIncluded ?? false, HitTestInterior: batchSheetIds.Contains(c.Id)));
        if (batchResult?.Succeeded == true && !batchResult.Programs.IsDefaultOrEmpty)
        {
            _displayOrigin = new ScenePoint(0, 0);
            foreach (var program in batchResult.Programs)
            {
                foreach (var profile in program.Plan.FinishedProfiles)
                    paths.Add(new ScenePath(profile.ContourId, profile.Points.Select(p => new ScenePoint(p.X, p.Y)).ToArray(), ScenePathKind.Finished));
                var order = 0;
                foreach (var toolpath in program.Plan.Toolpaths)
                {
                    var points = toolpath.Points.Select(p => new ScenePoint(p.X + program.Plan.Origin.X, p.Y + program.Plan.Origin.Y)).ToArray();
                    if (points.Length < 2) continue;
                    paths.Add(new ScenePath($"{program.SheetContourId}:{toolpath.ContourId}", points, ScenePathKind.Toolpath, true, ++order));
                }
                var seen = new HashSet<(ScenePoint Start, ScenePoint End)>();
                foreach (var motion in program.Playback.Motions.Where(m => m.Kind == NcMotionKind.Rapid))
                {
                    var start = new ScenePoint(motion.Start.X + program.Plan.Origin.X, motion.Start.Y + program.Plan.Origin.Y);
                    var end = new ScenePoint(motion.End.X + program.Plan.Origin.X, motion.End.Y + program.Plan.Origin.Y);
                    if (start != end && seen.Add((start, end)))
                        paths.Add(new ScenePath($"{program.SheetContourId}:NC RAPID", [start, end], ScenePathKind.Rapid));
                }
            }
            Preview.OriginLabel = "多母板 · 各板 G54";
        }
        else if (plan is not null && plan.Request.Document.Id == document.Id)
        {
            _displayOrigin = new ScenePoint(plan.Origin.X, plan.Origin.Y);
            foreach (var profile in plan.FinishedProfiles)
                paths.Add(new ScenePath(profile.ContourId, profile.Points.Select(p => new ScenePoint(p.X, p.Y)).ToArray(), ScenePathKind.Finished));
            var order = 0;
            foreach (var path in plan.Toolpaths)
            {
                var points = path.Points.Select(p => new ScenePoint(p.X + plan.Origin.X, p.Y + plan.Origin.Y)).ToArray();
                if (points.Length < 2) continue;
                paths.Add(new ScenePath(path.ContourId, points, ScenePathKind.Toolpath, true, ++order));
            }
            if (generatedProgram is not null)
            {
                var seen = new HashSet<(ScenePoint Start, ScenePoint End)>();
                foreach (var motion in generatedProgram.Motions.Where(m => m.Kind == NcMotionKind.Rapid))
                {
                    var start = new ScenePoint(motion.Start.X + plan.Origin.X, motion.Start.Y + plan.Origin.Y);
                    var end = new ScenePoint(motion.End.X + plan.Origin.X, motion.End.Y + plan.Origin.Y);
                    if (start != end && seen.Add((start, end)))
                        paths.Add(new ScenePath("NC RAPID", [start, end], ScenePathKind.Rapid));
                }
            }
            Preview.OriginLabel = "G54";
        }
        else
        {
            var origin = CurrentFixedOrigin();
            _displayOrigin = new ScenePoint(origin?.X ?? 0, origin?.Y ?? 0);
            Preview.OriginLabel = origin is null ? "CAD" : "G54";
        }
        Preview.Origin = _displayOrigin;
        Preview.SetScene(paths, fit && !_session.CurrentView.HasViewport);
        SetPreviewFocus();
    }

    private void UpdatePlanSummary()
    {
        if (_session.NcDocument is { } ncDocument)
        {
            OriginSummary.Text = ncDocument.Program is { } p ? $"{p.WorkCoordinateSystem} · 程序坐标（mm）" : "NC 原文与参数";
            PlanSummary.Text = ncDocument.Program is { } program
                ? $"{ncDocument.Lines.Count} 行 · {program.Motions.Length} 个运动块 · 移动时间约 {program.DurationSeconds / 60:0.##} min"
                : "无法生成完整轨迹；原文和可识别参数仍可查看。";
            CutOrderSummary.Text = "";
            return;
        }
        if (_session.IsBatchMode && _session.BatchResult is { } batch)
        {
            OriginSummary.Text = $"多母板 · {batch.Programs.Length} 个独立 G54";
            PlanSummary.Text = string.Join("\n", batch.Programs.Select((program, index) =>
                $"{index + 1}. {program.SheetContourId} · {program.Plan.Toolpaths.Length} 条路线 · {program.SuggestedFileName}"));
            CutOrderSummary.Text = _session.SelectedBatchProgram is { } selected
                ? $"当前预览母板 {selected.SheetContourId} · 可播放、复制该板 NC；导出时仍按母板分别生成独立 NC。"
                : "全图静态预览 · 点击母板任意位置或列表项即可加载该板 NC 并播放。";
            return;
        }
        var plan = _session.Plan;
        if (plan is null)
        {
            OriginSummary.Text = CurrentOriginDescription();
            PlanSummary.Text = _session.HasStalePlan ? "设置已变化，旧结果不能导出。请重新生成。" : "二维预览 · 刀心预补偿 G40 · 同一组刀路生成 NC";
            CutOrderSummary.Text = "加工顺序：先完成留皮层，再完成最终层";
            return;
        }
        var bounds = plan.Bounds;
        OriginSummary.Text = $"G54 ← CAD ({plan.Origin.X:0.###}, {plan.Origin.Y:0.###}) mm\n刀心范围：{bounds.Width:0.###} × {bounds.Height:0.###} mm";
        var length = plan.Toolpaths.Sum(p => p.Length);
        var depths = NcGenerator.DepthPasses(plan.Config);
        var passes = depths.Count;
        var movement = _session.Playback.Program is { } playback ? $"编程移动约 {playback.DurationSeconds / 60:0.##} min" : $"每圈 {length:0.##} mm";
        var passText = passes > 1
            ? plan.Config.Machining.LeaveSkin > 0 ? $"留皮 {plan.Config.Machining.LeaveSkin:0.###} mm · {passes} 遍" : $"分层 {passes} 遍"
            : "单次最终深度";
        PlanSummary.Text = (_session.HasStalePlan ? "上次结果已过期，请重新生成。\n" : "") + $"{plan.Toolpaths.Length} 条路线 · {passText} · {movement}\n板面 Z{plan.Config.SurfaceZ:0.###} → 最终 Z{plan.Config.Machining.CuttingDepth:0.###} mm\n未模拟材料切除、装夹或换刀内部动作" + (plan.Config.Machine.EmitM17 ? "与 M17" : "") + "。";
        var order = plan.Toolpaths.Select((path, index) => $"{index + 1}:{path.ContourId}").ToArray();
        var shownOrder = order.Length <= 16 ? string.Join(" → ", order) : string.Join(" → ", order.Take(16)) + $" → …（共 {order.Length} 条）";
        var depthSummary = passes > 1
            ? plan.Config.Machining.LeaveSkin > 0 ? "先完成全部路线的留皮层，再完成最终层。" : $"按 {passes} 个深度层加工。"
            : "每条路线一次切到最终深度。";
        CutOrderSummary.Text = $"加工顺序：{shownOrder}\n{depthSummary}";
    }

    private void SyncParameters()
    {
        var c = _session.Config;
        foreach (var pair in _numberBoxes)
        {
            if (_session.TryGetRejectedNumber(pair.Key, out var rejected))
            {
                // Restore invalid numeric edits with their error when undoing or redoing.
                // Avoid replacing text that the NumberBox is still parsing in the current edit.
                if (!_invalidNumberFields.Contains(pair.Key) || !pair.Value.Value.Equals(rejected)) pair.Value.Value = rejected;
                _invalidNumberFields.Add(pair.Key);
                if (pair.Key == "LengthOffset") _followTool = false;
            }
            else if (_invalidNumberFields.Remove(pair.Key)) _shownConfig = null;
        }
        void SetNumber(string key, double value) { if (!_invalidNumberFields.Contains(key)) _numberBoxes[key].Value = value; }
        if (_shownConfig != c)
        {
            _shownConfig = c;
            ToolTypeBox.Text = c.Tool.ToolType;
            SetNumber("ToolNumber", c.Tool.ToolNumber); SetNumber("LengthOffset", c.Tool.LengthOffset); SetNumber("Diameter", c.Tool.Diameter);
            if (c.Tool.ToolNumber != c.Tool.LengthOffset) _followTool = false;
            FollowToolCheck.IsChecked = _followTool;
            SpindleDirectionBox.SelectedIndex = c.Tool.SpindleDirection == SpindleDirection.Clockwise ? 0 : 1;
            SetNumber("SpindleSpeed", c.Machining.SpindleSpeed); SetNumber("CuttingFeed", c.Machining.CuttingFeed);
            SetNumber("CuttingDepth", c.Machining.CuttingDepth); SetNumber("EdgeAllowance", c.Machining.EdgeAllowance);
            SetNumber("SafeZ", c.Machining.SafeZ); SetNumber("PlungeFeed", c.Machining.PlungeFeed); SetNumber("LeaveSkin", c.Machining.LeaveSkin);
            CutDirectionBox.SelectedIndex = c.Machining.CutDirection == CutDirection.Counterclockwise ? 0 : 1;
        }
        SyncMachineParameters();
        var errors = _session.ValidationErrors;
        ValidationSummary.Text = string.Join("\n", errors.Select(p => $"{FieldNames.GetValueOrDefault(p.Key, p.Key)}：{p.Value}"));
        ValidationSummary.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in _numberBoxes)
        {
            pair.Value.Description = errors.TryGetValue(pair.Key, out var error) ? error : null;
            ToolTipService.SetToolTip(pair.Value, error);
        }
        PresentFirstValidationError(errors);
    }


    private void Parameter_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || ConfigurationActionBlocked || sender.Tag is not string field) return;
        CommitParameterValue(sender, field, args.NewValue);
    }

    private void Parameter_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing || ConfigurationActionBlocked || sender is not NumberBox box || box.Tag is not string field) return;
        // NumberBox evaluates its text as focus changes. Defer one UI turn so the
        // commit sees the parsed value even when the user leaves by clicking elsewhere.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_syncing && !ConfigurationActionBlocked && box.IsEnabled)
                CommitParameterValue(box, field, box.Value);
        });
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_syncing || ConfigurationActionBlocked || StepDownBox.FocusState == FocusState.Unfocused) return;
        if (IsDescendantOf(e.OriginalSource as DependencyObject, StepDownBox)) return;
        // Blank panels and some headers do not take focus themselves. Commit on
        // the click so leaving the editor has the same effect everywhere.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_syncing && !ConfigurationActionBlocked)
                CommitParameterValue(StepDownBox, "LeaveSkin", StepDownBox.Value);
        });
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void CommitParameterValue(NumberBox sender, string field, double value)
    {
        if (field == "LengthOffset") _followTool = false;
        if (!double.IsFinite(value)) { _invalidNumberFields.Add(field); _session.SetInputError(field, "请输入有效数字", rejectedValue: value); return; }
        if ((field is "ToolNumber" or "LengthOffset" or "SpindleSpeed") && (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue))
        { _invalidNumberFields.Add(field); _session.SetInputError(field, "请输入整数", rejectedValue: value); return; }
        var normalized = NumberInputNormalizer.Normalize(field, value);
        if (!normalized.Equals(value))
        {
            _syncing = true;
            try { sender.Value = normalized; }
            finally { _syncing = false; }
            value = normalized;
        }
        _invalidNumberFields.Remove(field);
        if (field is "ManualOriginX" or "ManualOriginY")
        {
            _session.UpdateManualOriginCoordinate(field, value);
            return;
        }
        _session.SetInputError(field, null);
        var config = _session.Config;
        config = field switch
        {
            "ToolNumber" => config with { Tool = config.Tool with { ToolNumber = (int)value, LengthOffset = _followTool ? (int)value : config.Tool.LengthOffset } },
            "LengthOffset" => config with { Tool = config.Tool with { LengthOffset = (int)value } },
            "Diameter" => config with { Tool = config.Tool with { Diameter = value } },
            "SpindleSpeed" => config with { Machining = config.Machining with { SpindleSpeed = (int)value } },
            "CuttingFeed" => config with { Machining = config.Machining with { CuttingFeed = value } },
            "CuttingDepth" => config with { Machining = config.Machining with { CuttingDepth = value } },
            "EdgeAllowance" => config with { Machining = config.Machining with { EdgeAllowance = value } },
            "SafeZ" => config with { Machining = config.Machining with { SafeZ = value } },
            "PlungeFeed" => config with { Machining = config.Machining with { PlungeFeed = value } },
            "LeaveSkin" => config with { Machining = config.Machining with { LeaveSkin = value } },
            "BoardThickness" => config with { Machining = config.Machining with { BoardThickness = value } },
            "RampLength" => config with { Machining = config.Machining with { RampLength = value } },
            "RampFeed" => config with { Machining = config.Machining with { RampFeed = value } },
            "ParkX" => config with { Machine = config.Machine with { ParkX = value } },
            "ParkY" => config with { Machine = config.Machine with { ParkY = value } },
            "ParkZ" => config with { Machine = config.Machine with { ParkZ = value } },
            _ => config
        };
        _session.UpdateConfig(config);
    }
    private void ToolType_Changed(object sender, TextChangedEventArgs e)
    { if (!_syncing) _session.UpdateConfig(_session.Config with { Tool = _session.Config.Tool with { ToolType = ToolTypeBox.Text } }); }
    private void Direction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _session.UpdateConfig(_session.Config with
        {
            Tool = _session.Config.Tool with { SpindleDirection = SpindleDirectionBox.SelectedIndex == 1 ? SpindleDirection.Counterclockwise : SpindleDirection.Clockwise },
            Machining = _session.Config.Machining with { CutDirection = CutDirectionBox.SelectedIndex == 1 ? CutDirection.Clockwise : CutDirection.Counterclockwise }
        });
    }

    private void Refilter()
    {
        var focused = ContourList.SelectedItems.OfType<ContourRow>().Select(c => c.Id).ToHashSet();
        var query = ContourSearch.Text.Trim();
        var layer = LayerFilter.SelectedIndex <= 0 ? null : LayerFilter.SelectedItem as string;
        var rows = _session.Contours.Where(row => (layer is null || row.Layer == layer) &&
            (query.Length == 0 || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Layer.Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (_showToolpathOrder && _session.CanExport && _session.Plan is { } plan)
        {
            var order = plan.Toolpaths.Select((path, index) => (path.ContourId, index)).GroupBy(x => x.ContourId)
                .ToDictionary(group => group.Key, group => group.Min(x => x.index));
            rows = rows.Where(row => order.ContainsKey(row.Id)).OrderBy(row => order[row.Id]);
        }
        _refiltering = true;
        try
        {
            _filteredContours.Clear();
            foreach (var row in rows) _filteredContours.Add(row);
            foreach (var row in _filteredContours.Where(r => focused.Contains(r.Id))) ContourList.SelectedItems.Add(row);
        }
        finally { _refiltering = false; }
        SetPreviewFocus();
        ContourCount.Text = $"显示 {_filteredContours.Count} / {_session.Contours.Count} · 勾选参与加工";
        ContourAllToggle.IsChecked = !_showToolpathOrder;
        ContourOrderToggle.IsChecked = _showToolpathOrder;
        RefreshContourSelection();
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (!_syncing) Refilter(); }
    private void LayerFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) Refilter(); }
    private void ContourList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Preview is null) return;
        foreach (var item in e.RemovedItems.OfType<ContourRow>()) item.IsFocused = false;
        foreach (var item in e.AddedItems.OfType<ContourRow>()) item.IsFocused = true;
        if (!_refiltering) SetPreviewFocus();
        RefreshContourSelection();
    }
    private void Canvas_Picked(object? sender, ContourPickedEventArgs args)
    {
        if (_session.IsBatchMode && _session.BatchResult?.Programs.Any(program =>
                string.Equals(program.SheetContourId, args.Id, StringComparison.Ordinal)) == true)
        {
            _session.SelectBatchProgram(args.Id);
            _showResults = true;
            SelectResultRoute(WorkbenchResultRoute.Code);
            if (Root.ActualWidth < 900) _narrowPanel = "results";
            ApplyView();
            return;
        }
        var row = _session.Contours.FirstOrDefault(c => c.Id == args.Id); if (row is null) return;
        if (!_filteredContours.Contains(row)) { _showToolpathOrder = false; ContourSearch.Text = ""; LayerFilter.SelectedIndex = 0; Refilter(); }
        if (!args.ExtendSelection) ContourList.SelectedItems.Clear();
        if (args.ExtendSelection && ContourList.SelectedItems.Contains(row)) ContourList.SelectedItems.Remove(row);
        else ContourList.SelectedItems.Add(row);
        ContourList.ScrollIntoView(row);
    }
    private void IncludeVisible_Click(object sender, RoutedEventArgs e) => _session.SetIncluded(_filteredContours.Select(c => c.Id), true);
    private void ExcludeVisible_Click(object sender, RoutedEventArgs e) => _session.SetIncluded(_filteredContours.Select(c => c.Id), false);
    private void IncludeParts_Click(object sender, RoutedEventArgs e)
    {
        _session.SetIncluded(_filteredContours.Where(c => c.IsOuterCandidate).Select(c => c.Id), false);
        _session.SetIncluded(_filteredContours.Where(c => !c.IsOuterCandidate).Select(c => c.Id), true);
    }
    private void SetOutside_Click(object sender, RoutedEventArgs e) => SetSide(CutSide.Outside);
    private void SetInside_Click(object sender, RoutedEventArgs e) => SetSide(CutSide.Inside);
    private void SetSide(CutSide side)
    {
        var ids = ContourList.SelectedItems.OfType<ContourRow>().Select(c => c.Id).ToArray();
        if (ids.Length == 0) { ShowNotice("先选中轮廓", "在列表或画布中选中要修改的轮廓。Ctrl / Shift 可多选。", InfoBarSeverity.Informational); return; }
        _session.SetCutSide(ids, side);
        RefreshContourSelection();
    }


    private void ToggleLeft_Click(object sender, RoutedEventArgs e)
    {
        _inspectionPanelOverride = null;
        var open = LeftPanel.Visibility != Visibility.Visible;
        if (Root.ActualWidth < 900) { _narrowPanel = open ? "left" : null; ApplyView(); return; }
        _session.CurrentView = _session.CurrentView with { LeftPanelOpen = open, ActiveInspector = "Contours" };
    }
    private void ToggleRight_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsNcDocument) return;
        _inspectionPanelOverride = null;
        var open = RightPanel.Visibility != Visibility.Visible;
        if (Root.ActualWidth < 900) { _narrowPanel = open ? "right" : null; ApplyView(); return; }
        _session.CurrentView = _session.CurrentView with { RightPanelOpen = open, ActiveInspector = "Parameters" };
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => Preview.FitToContent();
    private void LocateSelection_Click(object sender, RoutedEventArgs e) => FocusSelectionOnCanvas();
    private void Layers_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || Preview is null) return;
        _session.CurrentView = _session.CurrentView with { ShowSource = SourceToggle.IsChecked, ShowFinished = FinishedToggle.IsChecked, ShowRapid = RapidToggle.IsChecked, ShowToolpaths = ToolpathToggle.IsChecked, ShowDirections = DirectionsToggle.IsChecked };
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { _shownConfig = null; _session.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _shownConfig = null; _session.Redo(); }
    private void RestoreDefaults_Click(object sender, RoutedEventArgs e) => ApplyNamedConfiguration(new(
        ConfigurationDefaults.Name(_session.Config.Machine.Profile), ConfigurationDefaults.ForProfile(_session.Config.Machine.Profile)));
    private void Cancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();

    private async void OpenCad_Click(object sender, RoutedEventArgs e) => await OpenCadAsync();
    private Task OpenCadAsync() => OpenWithPickerAsync(false);
    private async void OpenTask_Click(object sender, RoutedEventArgs e) => await OpenWithPickerAsync(true);
    private async Task OpenWithPickerAsync(bool task)
    {
        if (_fileWriteOpen || _dialogOpen || _pickerOpen || _closeDialogOpen || _closingApproved || !_openGate.TryBegin())
        { ShowOpenInProgress(); return; }
        try
        {
            var path = task ? await PickOpenAsync(".json") : await PickOpenAsync(".dwg", ".dxf", ".nc");
            if (path is null) return;
            var request = FileOpenRequest.FromPaths([path], allowTask: task);
            if (!request.IsValid) { ShowNotice("文件未打开", request.Error!, InfoBarSeverity.Warning); return; }
            await OpenConfirmedAsync(request);
        }
        finally { _openGate.Complete(); }
    }

    private Task OpenPathAsync(string path) => RequestOpenAsync(FileOpenRequest.FromPaths([path], allowTask: true));
    private async Task RequestOpenAsync(FileOpenRequest request)
    {
        if (!request.IsValid) { ShowNotice("文件未打开", request.Error!, InfoBarSeverity.Warning); return; }
        if (request.IsActivationOnly) return;
        if (_fileWriteOpen || _dialogOpen || _pickerOpen || _closeDialogOpen || _closingApproved) { ShowOpenInProgress(); return; }
        switch (_openGate.Admit(request))
        {
            case OpenRequestAdmission.Start: await OpenReservedAsync(request); break;
            case OpenRequestAdmission.Rejected: ShowOpenInProgress(); break;
        }
    }

    private async Task OpenReservedAsync(FileOpenRequest request)
    {
        try { await OpenConfirmedAsync(request); }
        finally { _openGate.Complete(); }
    }

    private async Task OpenConfirmedAsync(FileOpenRequest request)
    {
        try
        {
            if (!await ConfirmDiscardAsync() || _closingApproved) return;
            // Recheck after confirmation: a file can disappear while a dialog is open.
            var checkedRequest = FileOpenRequest.FromPaths([request.Path!], allowTask: request.IsTask);
            if (!checkedRequest.IsValid) { ShowNotice("文件未打开", checkedRequest.Error!, InfoBarSeverity.Warning); return; }
            if (request.IsTask) await LoadTaskAsync(request.Path!);
            else if (request.Kind == FileOpenKind.Nc)
            {
                try { Notice.IsOpen = false; await _session.LoadNcAsync(request.Path!); Refresh(); }
                catch (OperationCanceledException) { }
            }
            else await LoadCadAsync(request.Path!);
        }
        catch (Exception error) { ShowNotice("文件未打开", error.Message, InfoBarSeverity.Error); }
    }

    private void ShowOpenInProgress() => ShowNotice("暂未打开新文件", "当前打开流程或对话框尚未结束，请完成后再拖入文件。", InfoBarSeverity.Informational);

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var available = !_openGate.IsBusy && !_fileWriteOpen && !_dialogOpen && !_pickerOpen && !_closeDialogOpen && !_closingApproved;
        e.AcceptedOperation = available && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropHighlight.Visibility = e.AcceptedOperation == DataPackageOperation.Copy ? Visibility.Visible : Visibility.Collapsed;
        e.DragUIOverride.Caption = available ? "松开以打开一份 DWG / DXF / NC" : "当前打开流程尚未结束，请稍后重试";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(Root);
        if (position.X <= 0 || position.Y <= 0 || position.X >= Root.ActualWidth || position.Y >= Root.ActualHeight || ReferenceEquals(e.OriginalSource, Root))
            DropHighlight.Visibility = Visibility.Collapsed;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropHighlight.Visibility = Visibility.Collapsed;
        if (_fileWriteOpen || _dialogOpen || _pickerOpen || _closeDialogOpen || _closingApproved || !_openGate.TryBegin())
        { ShowOpenInProgress(); return; }
        try
        {
            FileOpenRequest request;
            var deferral = e.GetDeferral();
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                    request = new(null, Error: "请拖入一个 DWG、DXF 或 NC 文件。");
                else
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    request = items.Count == 1 && items[0] is StorageFile file
                        ? FileOpenRequest.FromPaths([file.Path])
                        : new(null, Error: "一次只能拖入一个 DWG、DXF 或 NC 文件，不支持文件夹或多个文件。");
                }
            }
            catch (Exception error) { request = new(null, Error: $"无法读取拖入的文件：{error.Message}"); }
            finally { deferral.Complete(); }
            // Complete the shell operation before awaiting any dialog or CAD import.
            if (!request.IsValid)
            {
                ShowNotice("文件未打开", request.Error!, InfoBarSeverity.Warning);
                return;
            }
            await OpenConfirmedAsync(request);
        }
        catch (Exception error) { ShowNotice("文件未打开", error.Message, InfoBarSeverity.Error); }
        finally { _openGate.Complete(); }
    }
    private async Task LoadCadAsync(string path)
    {
        try { Notice.IsOpen = false; await _session.LoadCadAsync(path); Refresh(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice("导入失败", error.Message, InfoBarSeverity.Error); }
    }
    private async Task LoadTaskAsync(string path)
    {
        try { Notice.IsOpen = false; await _session.LoadTaskAsync(path); SetConfigurationSource("任务中的参数", _session.Config); Refresh(); }
        catch (SourceFileMissingException error)
        {
            var result = await ShowDialogAsync("找不到任务源图纸", $"{error.SourcePath}\n\n请选择原 CAD 文件。内容不一致时不会套用旧轮廓选择。", "定位图纸", "取消");
            if (result == ContentDialogResult.Primary)
            {
                var relocated = await PickOpenAsync(".dwg", ".dxf");
                if (relocated is not null)
                    try { await _session.LoadTaskAsync(path, relocated); SetConfigurationSource("任务中的参数", _session.Config); Refresh(); }
                    catch (Exception relocationError) { ShowNotice("任务未打开", relocationError.Message, InfoBarSeverity.Error); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice("任务未打开", error.Message, InfoBarSeverity.Error); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveTaskAsync(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveTaskAsync(true);
    private Task<bool> SaveTaskAsync(bool forcePicker) => SaveFileAsync(false, forcePicker);
    private async void Generate_Click(object sender, RoutedEventArgs e) => await GenerateAsync();
    private async Task GenerateAsync()
    {
        if (_fileWriteOpen || _dialogOpen || _pickerOpen || !_session.CanGenerate) return;
        try { Notice.IsOpen = false; await _session.GenerateAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice("无法生成完整刀路", error.Message, InfoBarSeverity.Error); }
        finally
        {
            // A failed/canceled generation retains the previous result and its viewport.
            _fitSceneAfterLayout = _session.GenerationOutcome == GenerationOutcome.Succeeded
                && _session.Plan is not null && !_session.HasStalePlan && !_session.CurrentView.HasViewport;
            UpdateDiagnostics();
            var ui = BuildWorkbenchUiState();
            _showResults = ui.ResultRoute == WorkbenchResultRoute.Diagnostics;
            if (_showResults)
            {
                SelectResultRoute(ui.ResultRoute);
                if (Root.ActualWidth < 900) _narrowPanel = "results";
            }
            else
            {
                SelectResultRoute(ui.ResultRoute);
                if (_narrowPanel == "results") _narrowPanel = null;
            }
            ApplyView(); RefreshWorkflow();
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs e) => await SaveFileAsync(true, false);
    private async void ExportAs_Click(object sender, RoutedEventArgs e) => await SaveFileAsync(true, true);
    private async void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        if (_fileWriteOpen || _dialogOpen || _pickerOpen || _session.Document is null) return;
        _fileWriteOpen = true; QueueRefresh();
        try
        {
            var batch = await _session.GenerateBatchAsync();
            var directory = _session.Store.NcDirectory;
            var existing = batch.Programs.Select(p => Path.Combine(directory, p.SuggestedFileName)).Where(File.Exists).ToArray();
            if (existing.Length > 0)
            {
                var decision = await ShowDialogAsync("批量 NC 文件已存在",
                    string.Join(Environment.NewLine, existing), "覆盖全部", "取消");
                if (decision != ContentDialogResult.Primary) return;
            }
            var paths = BatchNcGenerator.Write(directory, batch);
            ShowNotice("批量 NC 已导出", string.Join(Environment.NewLine, paths), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice("批量导出失败", error.Message, InfoBarSeverity.Error); }
        finally { _fileWriteOpen = false; QueueRefresh(); }
    }
    private void CopyNc_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.CanCopy) return;
        var data = new DataPackage(); data.SetText(_session.NcCode); Clipboard.SetContent(data);
        ShowNotice("已复制 NC", $"共 {_session.NcLines.Count} 行", InfoBarSeverity.Success);
    }
    private void RecentMenu_Opening(object sender, object e)
    {
        RecentMenu.Items.Clear();
        foreach (var path in _session.Store.RecentTasks)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) => await OpenPathAsync(path);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0) RecentMenu.Items.Add(new MenuFlyoutItem { Text = "暂无最近任务", IsEnabled = false });
    }
    private void LeftResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (LeftPanel.Visibility == Visibility.Visible) _session.CurrentView = _session.CurrentView with { LeftWidth = Math.Clamp(LeftColumn.ActualWidth + e.HorizontalChange, 240, 400) };
    }
    private void RightResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (RightPanel.Visibility == Visibility.Visible) _session.CurrentView = _session.CurrentView with { RightWidth = Math.Clamp(RightColumn.ActualWidth - e.HorizontalChange, 320, 480) };
    }
    private async Task<string?> PickOpenAsync(params string[] extensions)
    {
        if (_pickerOpen) return null;
        _pickerOpen = true;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { ViewMode = PickerViewMode.List };
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync(); return file?.Path;
        }
        catch (Exception error) { ShowNotice("无法打开文件选择器", error.Message, InfoBarSeverity.Error); return null; }
        finally { _pickerOpen = false; }
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.ActionButton = null; Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
        if (_page == AppPage.Settings)
        {
            if (severity == InfoBarSeverity.Error) ReportSettingsError(message);
            else if (severity == InfoBarSeverity.Success) ClearSettingsError();
        }
    }
    private async Task<ContentDialogResult> ShowDialogAsync(string title, string message, string primary, string close, string? secondary = null)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = primary, CloseButtonText = close, SecondaryButtonText = secondary ?? "", DefaultButton = ContentDialogButton.Primary };
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (_confirmingDiscard) return false;
        _confirmingDiscard = true;
        try
        {
            if (_session.IsBusy)
            {
                var result = await ShowDialogAsync("当前任务正在处理", "停止当前导入或计算，再打开另一份文件？", "停止并继续", "返回");
                if (result != ContentDialogResult.Primary) return false;
                _session.Cancel();
                if (_session.IsBusy)
                {
                    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Changed(object? _, PropertyChangedEventArgs e)
                    { if (e.PropertyName == nameof(WorkbenchSession.IsBusy) && !_session.IsBusy) idle.TrySetResult(); }
                    _session.PropertyChanged += Changed;
                    try { if (_session.IsBusy) await idle.Task; }
                    finally { _session.PropertyChanged -= Changed; }
                }
            }
            if (!_session.IsDirty || _session.Document is null) return true;
            var discard = await ShowDialogAsync("保存当前加工任务？", "轮廓加工选择和参数有未保存的修改。", "保存", "取消", "不保存");
            if (discard == ContentDialogResult.Secondary) return true;
            if (discard != ContentDialogResult.Primary || !await SaveTaskAsync(false)) return false;
            if (!_session.IsDirty) return true;
            ShowNotice("仍有未保存的修改", "保存期间加工内容又有变化，已保留当前任务。请再次保存后打开新文件。", InfoBarSeverity.Warning);
            return false;
        }
        finally { _confirmingDiscard = false; }
    }
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingApproved) return;
        args.Cancel = true;
        if (_fileWriteOpen || _pickerOpen || _dialogOpen) return;
        if (_closeDialogOpen) return;
        _closeDialogOpen = true;
        try
        {
            if (await ConfirmDiscardAsync()) { _closingApproved = true; Close(); }
        }
        catch (Exception error) { ShowNotice("窗口尚未关闭", error.Message, InfoBarSeverity.Error); }
        finally { _closeDialogOpen = false; }
    }
}
