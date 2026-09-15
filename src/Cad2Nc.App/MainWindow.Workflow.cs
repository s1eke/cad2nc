using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    // Pages and result visibility are transient presentation state, never machining edits.
    private enum AppPage { Workbench, Guide, Settings }
    private AppPage _page = AppPage.Workbench;
    private string? _inspectionPanelOverride;
    private bool _showResults;
    private bool _fitSceneAfterLayout, _sceneFitQueued;
    private string _guideKey = "";
    private NcDocument? _shownNcDocument;
    private NcDocument? _sceneNcDocument;
    private NcDocument? _parameterNcDocument;

    private void GuideMenu_Click(object sender, RoutedEventArgs e) => NavigateToPage(AppPage.Guide);
    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => NavigateToPage(AppPage.Settings);
    private void GoWorkbench_Click(object sender, RoutedEventArgs e) => NavigateToPage(AppPage.Workbench);
    private void NavigateToPage(AppPage page)
    {
        _page = page;
        ApplyView(); Refresh();
    }
    private void ShowPreview_Click(object sender, RoutedEventArgs e)
    {
        _narrowPanel = null;
        _resultsExpanded = false;
        NavigateToPage(AppPage.Workbench);
    }
    private void ShowParameters_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsNcDocument) return;
        _inspectionPanelOverride = null;
        if (Root.ActualWidth < 900) _narrowPanel = "right";
        else _session.CurrentView = _session.CurrentView with
        {
            ActiveInspector = "Parameters",
            RightPanelOpen = true
        };
        NavigateToPage(AppPage.Workbench);
    }
    private void ShowContours_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsNcDocument) return;
        _inspectionPanelOverride = null;
        if (Root.ActualWidth < 900) _narrowPanel = "left";
        else _session.CurrentView = _session.CurrentView with
        {
            ActiveInspector = "Contours",
            LeftPanelOpen = true
        };
        NavigateToPage(AppPage.Workbench);
    }
    private void ShowResults_Click(object sender, RoutedEventArgs e)
    {
        _showResults = true;
        if (ReferenceEquals(sender, DiagnosticBadgeButton)) SelectResultRoute(WorkbenchResultRoute.Diagnostics);
        else if (!_session.IsNcDocument) SelectResultRoute(WorkbenchResultRoute.Summary);
        if (Root.ActualWidth < 900) _narrowPanel = "results";
        NavigateToPage(AppPage.Workbench);
    }

    private void SelectResultRoute(WorkbenchResultRoute route) => ResultTabs.SelectedItem = route switch
    {
        WorkbenchResultRoute.Diagnostics => DiagnosticTab,
        WorkbenchResultRoute.Code => NcCodeTab,
        WorkbenchResultRoute.ProgramParameters when NcParametersTab.Visibility == Visibility.Visible => NcParametersTab,
        _ => SummaryTab
    };
    private void CloseResults_Click(object sender, RoutedEventArgs e)
    {
        _showResults = false;
        _resultsExpanded = false;
        if (_narrowPanel == "results") _narrowPanel = null;
        ApplyView();
    }
    private void ResultsResize_DragDelta(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (Root.ActualWidth < 900 || WorkArea.ActualHeight <= 0) return;
        _session.CurrentView = _session.CurrentView with
        {
            ResultHeightRatio = Math.Clamp(_session.CurrentView.ResultHeightRatio - e.VerticalChange / Math.Max(1, WorkArea.ActualHeight - ResultResize.ActualHeight), .22, .55)
        };
    }
    private void WorkArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_numberBoxes is not null) ApplyView();
    }

    private void QueueDefaultSceneFit()
    {
        if (!_fitSceneAfterLayout || _sceneFitQueued || _page != AppPage.Workbench || CanvasPanel.Visibility != Visibility.Visible) return;
        _sceneFitQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _sceneFitQueued = false;
            if (!_fitSceneAfterLayout || _page != AppPage.Workbench || CanvasPanel.Visibility != Visibility.Visible) return;
            // Fitting an initial/default scene to the result layout is presentation, not a task edit.
            var wasSyncing = _syncing;
            _syncing = true;
            try
            {
                Preview.UpdateLayout();
                if (Preview.ActualWidth < 10 || Preview.ActualHeight < 10) return;
                _fitSceneAfterLayout = false;
                if (_session.Document is not null && _session.CurrentView.HasViewport) return;
                Preview.FitToContent();
            }
            finally { _syncing = wasSyncing; }
        });
    }

    private void RefreshWorkflow()
    {
        var external = _session.IsNcDocument;
        var workbench = _page == AppPage.Workbench;
        WorkbenchContextBar.Visibility = WorkArea.Visibility = workbench ? Visibility.Visible : Visibility.Collapsed;
        GuidePage.Visibility = _page == AppPage.Guide ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        EmptyCanvas.Visibility = _session.Document is null && !external ? Visibility.Visible : Visibility.Collapsed;
        OutputSummaryText.Visibility = OutputPathText.Visibility = OutputPathLabel.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        var document = _session.Document;
        var multiBoard = document is not null && ToolpathPlanner.FindSheetCandidates(document).Count > 1;
        var boardSummary = multiBoard ? $" · {ToolpathPlanner.FindSheetCandidates(document!).Count} 块母板" : "";
        OutputSummaryText.Text = document is null ? "尚未打开 CAD 图纸" : $"{ConfigurationDefaults.Name(_session.Config.Machine.Profile)}\n加工 {_session.Contours.Count(c => c.IsIncluded)} 个轮廓{boardSummary} · T{_session.Config.Tool.ToolNumber} / H{_session.Config.Tool.LengthOffset} · 刀径 {_session.Config.Tool.Diameter:0.###} mm\n板面 Z{_session.Config.SurfaceZ:0.###} → 最终 Z{_session.Config.Machining.CuttingDepth:0.###} mm · 安全 Z{_session.Config.Machining.SafeZ:0.###} mm\n{LeaveSkinLabel(_session.Config)}";
        OutputPathText.Text = _session.Document is null ? _session.Store.NcDirectory
            : multiBoard ? _session.Store.NcDirectory : SavePaths.NcPath(_session.SourcePath, _session.Store.NcDirectory);
        ExportButton.IsEnabled = _session.CanExport && !_fileWriteOpen;
        ExportAsButton.IsEnabled = ExportButton.IsEnabled;
        ExportButton.Visibility = workbench && !external ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Label = multiBoard ? "导出多个 NC" : "导出 NC";
        ToolTipService.SetToolTip(ExportButton, ExportExplanation());
        ParametersPanelButton.Visibility = ContoursPanelButton.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        ResultsPanelButton.Content = external ? "程序详情" : "结果";
        ResultsTitle.Text = external ? "NC 文件查看" : "加工结果";
        NcParametersTab.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticTab.Header = external ? "解析提示" : "诊断与提示";
        ResultRangeText.Text = external ? $"{_session.NcLines.Count} 行 · 只读"
            : _session.Plan is { } plan ? $"{(_session.HasStalePlan ? "已过期 · " : "")}刀心范围 {plan.Bounds.Width:0.###} × {plan.Bounds.Height:0.###} mm"
            : _session.SelectedBatchProgram is { } selectedBatch
                ? $"母板 {selectedBatch.SheetContourId} · {_session.NcLines.Count} 行"
                : "生成后显示加工范围与原点";
        BatchBoardPanel.Visibility = _session.IsBatchMode ? Visibility.Visible : Visibility.Collapsed;
        BatchBoardList.ItemsSource = _session.BatchResult?.Programs;
        ClearBatchBoardButton.IsEnabled = _session.SelectedBatchProgram is not null;
        RefreshGuide(); RefreshNcParameters(); RefreshConfigurationSummary(); RefreshSettings();
    }

    private string ExportExplanation()
    {
        if (_session.IsBusy) return "正在处理，请稍候。";
        if (_session.Document is null) return "打开 CAD 图纸并生成刀路后，可导出 NC。";
        if (_session.ValidationErrors.Count > 0) return "加工参数有错误，请在参数栏修正。";
        if (_session.HasStalePlan) return "加工内容已修改，请重新生成刀路。";
        if (_session.IsBatchMode && _session.SelectedBatchProgram is { } selectedBatch)
            return $"当前预览母板 {selectedBatch.SheetContourId}，可播放、复制该板 NC；导出操作仍会输出全部母板。";
        if (_session.Plan is null || _session.NcLines.Count == 0)
            return _session.IsBatchMode ? "已生成多母板结果，请选择一块母板预览 NC。" : "尚未生成有效刀路，请点击“生成／更新刀路”。";
        if (_session.IsBatchMode)
            return _session.CanExport ? $"已生成 {_session.BatchResult!.Programs.Length} 个母板程序，将按母板分别导出。" : "当前批量结果有错误，请在加工结果中查看原因。";
        return _session.CanExport ? "当前 NC 已生成，可以直接导出。" : "当前结果有错误，请在加工结果中查看原因。";
    }

    private void RefreshGuide()
    {
        var key = $"{_session.Config}|{_session.Workpiece}|{_session.Document?.Id}|{_session.Plan?.Revision}|{_session.HasStalePlan}|{_session.IsBusy}|{_session.CurrentView.Theme}";
        if (key == _guideKey) return;
        _guideKey = key; GuideCards.Children.Clear();
        var c = _session.Config;
        var current = _session.Document is not null;
        Add("刀号与刀长补偿", "刀号 T 用于选择机床刀具，H 用于调用刀长补偿表；两者可以不同。请对照实际装刀和补偿表设置。“H 跟随刀号”只帮助输入，不读取机床。", current ? $"T{c.Tool.ToolNumber} / H{c.Tool.LengthOffset}" : null, "ToolNumber");
        Add("刀具直径与封边吃料", "刀径应与实际刀具一致，用于计算刀心路线。封边吃料让外侧零件成品边内缩、内孔成品边外扩，再计算刀具半径补偿。刀路始终由软件预先补偿；宝元 LNC 现场格式不输出 G40，通用 Fanuc 旧版保留 G40，任何模式都不能再叠加 G41/G42。", current ? $"刀径 {c.Tool.Diameter:0.###} mm · 封边吃料 {c.Machining.EdgeAllowance:0.###} mm" : null, "Diameter");
        Add("转速、进给与方向", "主轴转速、切削进给和下刀进给应结合刀具、材料与机床能力设置。主轴旋转方向和轮廓走刀方向分别设置；软件数值校验不代表工艺适合具体机床。", current ? $"主轴 {c.Machining.SpindleSpeed} r/min · 切削 {c.Machining.CuttingFeed:0.###} mm/min · 下刀 {c.Machining.PlungeFeed:0.###} mm/min" : null, "SpindleSpeed");
        Add("机床配置与工件", "宝元配置以板底／垫板上表面为 Z0，默认板厚 17 mm；通用 Fanuc 旧版以板面为 Z0。切换机床会应用内置参数，可撤销。板厚、Z 基准、原点、刀具和机床输出均可单独调整。启动默认值在设置中保存，每份任务保留自己的参数与母板原点。", current ? ConfigurationDefaults.Name(c.Machine.Profile) + $" · 板厚 {c.Machining.BoardThickness:0.###} mm" : null, "Profile");
        Add("深度与留皮", "目标 Z 是绝对工件坐标。留皮厚度大于 0 时，先完成所有路线的留皮层，再完成最终层；留皮为 0 时是否分层由「分层加工」决定。板底 Z0、板厚 17 mm 时，目标 Z−0.10 表示切穿后再下切 0.1 mm；留皮 0.5 mm 时留皮层位于 Z0.5，最终层位于 Z−0.1。", current ? $"板面 Z{c.SurfaceZ:0.###} → 目标 Z{c.Machining.CuttingDepth:0.###} mm · {LeaveSkinLabel(c)}" : null, "CuttingDepth");
        Add("斜向下刀", "斜坡沿补偿后的闭合刀心路线逐渐下降；跨过顶点继续，短轮廓可绕行多圈。到达目标 Z 后再完整走一圈，使入口段也加工到底。分层时从上一层深度开始下一段斜坡。斜坡长度与进给可以调整，也可选择垂直下刀。", current ? c.Machining.EntryMode == EntryMode.Ramp ? $"斜坡 {c.Machining.RampLength:0.###} mm · {c.Machining.RampFeed:0.###} mm/min" : "垂直下刀" : null, "EntryMode");
        Add("安全高度与装夹", "安全高度是相对工件 Z0 的抬刀位置，应高于空移途中工件、压板和夹具。软件不知道现场装夹高度，也不检测夹具碰撞。", current ? $"安全高度 Z{c.Machining.SafeZ:0.###} mm" : null, "SafeZ");
        var origin = current && _session.Plan is { } p && !_session.HasStalePlan ? $"G54 对应 CAD ({p.Origin.X:0.###}, {p.Origin.Y:0.###}) mm" : null;
        Add("工件原点与图纸单位", "宝元默认使用指定母板左下角作为 G54 X0/Y0，也可填写 CAD 原点坐标；这两种原点不随加工轮廓勾选变化。旧版模式使用所选刀心路线整体包围框左下角。母板默认不参与切割，补偿刀路可以出现负 X/Y。图纸单位未声明时按毫米读取并提示；外部 NC 按自身程序坐标显示。", origin ?? CurrentOriginDescription(), "OriginMode");
        Add("控制器与机床适配", "机床输出可配置单独 T 刀号或 T + M06、程序外框、结束停车位置和 M17。停车位置使用工件坐标，先抬 Z 再移 XY，期间保留刀长补偿。宝元 M17 沿用现场样例，未推断具体功能。预览展示编程轨迹和停车运动，未模拟 M17、换刀内部动作、机床偏置表或材料切除。", null, "ToolChange");
        void Add(string title, string details, string? value, string? field)
        {
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (value is not null) stack.Children.Add(new TextBlock { Text = "当前任务：" + value, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            stack.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            if (current && field is not null)
            {
                var button = new Button { Content = field == "Contours" ? "查看加工轮廓" : "查看相关参数", IsEnabled = !_session.IsBusy };
                button.Click += (_, _) => LocateGuideField(field);
                stack.Children.Add(button);
            }
            GuideCards.Children.Add(new Border { Child = stack, Padding = new Thickness(18), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        }
    }

    private void LocateGuideField(string field)
    {
        var side = field == "Contours" ? "left" : "right";
        _inspectionPanelOverride = side;
        _narrowPanel = side;
        NavigateToPage(AppPage.Workbench);
        if (field == "Contours") { ContourList.Focus(FocusState.Programmatic); return; }
        FocusParameter(field);
    }

    private static string LeaveSkinLabel(ConversionConfig config)
    {
        var valid = config.GetValidationErrors().Count == 0;
        if (!valid) return "加工遍数待修正";
        var passes = NcGenerator.DepthPasses(config).Count;
        return config.Machining.LeaveSkin > 0
            ? $"留皮 {config.Machining.LeaveSkin:0.###} mm · {passes} 遍"
            : passes > 1 ? $"不留皮 · 分层 {passes} 遍" : "不留皮 · 1 遍";
    }

    private sealed record NcParameterRow(string Name, string DisplayValue);
    private void RefreshNcParameters()
    {
        var document = _session.NcDocument;
        if (ReferenceEquals(document, _parameterNcDocument)) return;
        _parameterNcDocument = document;
        if (document is null) { NcParameterList.ItemsSource = null; ResultTabs.SelectedItem = DiagnosticTab; return; }
        var rows = document.Parameters.Select(p => new NcParameterRow(p.Name, $"{p.Value} · 第 {p.LineIndex + 1} 行")).ToList();
        if (document.Program?.Bounds is { } bounds)
        {
            rows.Add(new("X 范围", $"{bounds.MinX:0.###} ～ {bounds.MaxX:0.###} mm"));
            rows.Add(new("Y 范围", $"{bounds.MinY:0.###} ～ {bounds.MaxY:0.###} mm"));
            rows.Add(new("Z 范围", bounds.MinZ is { } minZ && bounds.MaxZ is { } maxZ ? $"{minZ:0.###} ～ {maxZ:0.###} mm（已知位置）" : "未指定"));
        }
        rows.Add(new("刀具直径／材料／留量／实际安全高度", "未提供；不能由程序坐标可靠还原"));
        rows.Add(new("坐标说明", "显示程序编写的工件坐标，不模拟机床偏置表或刀长表"));
        NcParameterList.ItemsSource = rows;

    }
}
