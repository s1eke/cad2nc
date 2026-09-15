using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private bool _resultsExpanded, _showToolpathOrder, _refiltering;
    private double _ncWidthBeforeDrag;

    private void ApplyView()
    {
        var view = _session.CurrentView;
        var width = Root.ActualWidth;
        Root.RequestedTheme = view.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        var external = _session.IsNcDocument;
        var narrow = width is > 0 and < 900;
        var compact = width is >= 900 and < 1200;
        var band = narrow ? 0 : compact ? 1 : 2;
        if (band != _layoutBand)
        {
            _narrowPanel = narrow && external && _showResults ? "results" : null;
            _inspectionPanelOverride = null;
            _layoutBand = band;
        }
        var workbench = _page == AppPage.Workbench;
        // Keep the virtualized list usable when a short window also shows selection details.
        ContourSelectionDetails.MaxHeight = Math.Clamp(WorkArea.ActualHeight - 360, 80, 260);
        UpdateHeaderCommandWidth();
        BrandName.Visibility = width < 580 ? Visibility.Collapsed : Visibility.Visible;
        DocumentTitle.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
        SelectionHeader.Visibility = workbench && !external && width >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        ResultContextHeader.Visibility = workbench && width >= 650 ? Visibility.Visible : Visibility.Collapsed;
        NextActionText.Visibility = workbench && width >= 1400 ? Visibility.Visible : Visibility.Collapsed;
        var activeInspector = _inspectionPanelOverride switch { "left" => "Contours", "right" => "Parameters", _ => view.ActiveInspector };
        var leftVisible = workbench && !external && (narrow ? _narrowPanel == "left"
            : compact ? activeInspector == "Contours" && (view.LeftPanelOpen || _inspectionPanelOverride == "left") : view.LeftPanelOpen || _inspectionPanelOverride == "left");
        var rightVisible = workbench && !external && (narrow ? _narrowPanel == "right"
            : compact ? activeInspector == "Parameters" && (view.RightPanelOpen || _inspectionPanelOverride == "right") : view.RightPanelOpen || _inspectionPanelOverride == "right");
        var resultsVisible = workbench && _showResults && (!narrow || _narrowPanel == "results");
        var resultsOnly = resultsVisible && (_resultsExpanded || narrow);
        var leftWidth = leftVisible ? Math.Clamp(view.LeftWidth, 240, 400) : 0;
        var rightWidth = rightVisible ? Math.Clamp(view.RightWidth, 320, 480) : 0;
        var sidebarBudget = Math.Max(0, width - 32 - 520);
        var minimumLeft = leftVisible ? 240 : 0;
        var minimumRight = rightVisible ? 320 : 0;
        var extra = leftWidth + rightWidth - minimumLeft - minimumRight;
        if (!narrow && leftWidth + rightWidth > sidebarBudget && extra > 0)
        {
            var fraction = Math.Clamp((sidebarBudget - minimumLeft - minimumRight) / extra, 0, 1);
            leftWidth = minimumLeft + (leftWidth - minimumLeft) * fraction;
            rightWidth = minimumRight + (rightWidth - minimumRight) * fraction;
        }
        if (external && resultsVisible && !resultsOnly)
            rightWidth = Math.Min(_session.Store.NcReviewWidth, Math.Max(0, WorkArea.ActualWidth - 376));

        LeftPanel.Visibility = leftVisible ? Visibility.Visible : Visibility.Collapsed;
        RightPanel.Visibility = rightVisible ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility = resultsVisible ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = leftVisible ? narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(leftWidth) : new GridLength(0);
        RightColumn.Width = rightWidth > 0 ? narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(rightWidth) : new GridLength(0);
        CanvasColumn.Width = narrow && (leftVisible || rightVisible) ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CanvasPanel.Visibility = !workbench || resultsOnly || narrow && (leftVisible || rightVisible) ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRowSpan(CanvasPanel, external ? 4 : 1);
        Grid.SetColumn(ReviewPanel, external && !resultsOnly ? 2 : 1);
        Grid.SetRow(ReviewPanel, external || resultsOnly ? 0 : 2);
        Grid.SetRowSpan(ReviewPanel, external || resultsOnly ? 4 : 1);
        var drawer = resultsVisible && !external && !resultsOnly;
        MainViewRow.Height = new GridLength(drawer ? 1 - view.ResultHeightRatio : 1, GridUnitType.Star);
        ResultsRow.Height = drawer ? new GridLength(view.ResultHeightRatio, GridUnitType.Star) : new GridLength(0);
        var stripVisible = workbench && !external && !resultsVisible && !leftVisibleInNarrow() && !rightVisibleInNarrow();
        ResultStrip.Visibility = stripVisible ? Visibility.Visible : Visibility.Collapsed;
        ResultStripRow.Height = new GridLength(stripVisible ? 36 : 0);
        ResultResize.Visibility = drawer ? Visibility.Visible : Visibility.Collapsed;
        LeftResize.Visibility = leftVisible && !narrow ? Visibility.Visible : Visibility.Collapsed;
        RightResize.Visibility = rightVisible && !narrow ? Visibility.Visible : Visibility.Collapsed;
        NcResize.Visibility = external && resultsVisible && !resultsOnly ? Visibility.Visible : Visibility.Collapsed;
        // The resize handle is declared before the result panel but remains above it for input.
        Canvas.SetZIndex(NcResize, 2);
        ExpandResultsButton.Content = _resultsExpanded ? "返回预览" : "大视图";
        ExpandResultsButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        CloseResultsButton.Content = external ? "查看预览" : "收起";
        CompactNavigation.Visibility = workbench && narrow ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanelButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanelButton.IsChecked = narrow && _narrowPanel is null;
        ContoursPanelButton.IsChecked = narrow && _narrowPanel == "left";
        ParametersPanelButton.IsChecked = narrow && _narrowPanel == "right";
        ResultsPanelButton.IsChecked = narrow && resultsVisible;
        ShowLeftButton.Visibility = workbench && !external && !narrow && CanvasPanel.Visibility == Visibility.Visible && !leftVisible ? Visibility.Visible : Visibility.Collapsed;
        ShowRightButton.Visibility = workbench && !external && !narrow && CanvasPanel.Visibility == Visibility.Visible && !rightVisible ? Visibility.Visible : Visibility.Collapsed;
        SourceToggle.Visibility = FinishedToggle.Visibility = SourceLegend.Visibility = FinishedLegend.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        SourceToggle.IsChecked = view.ShowSource; FinishedToggle.IsChecked = view.ShowFinished;
        RapidToggle.IsChecked = view.ShowRapid; ToolpathToggle.IsChecked = view.ShowToolpaths; DirectionsToggle.IsChecked = view.ShowDirections;
        SourceLegend.IsChecked = view.ShowSource; FinishedLegend.IsChecked = view.ShowFinished;
        RapidLegend.IsChecked = view.ShowRapid; ToolpathLegend.IsChecked = view.ShowToolpaths; DirectionsLegend.IsChecked = view.ShowDirections;
        Preview.ShowSource = view.ShowSource; Preview.ShowFinished = view.ShowFinished; Preview.ShowRapid = view.ShowRapid;
        Preview.ShowDirections = view.ShowDirections; Preview.ShowToolpaths = view.ShowToolpaths;
        QueueDefaultSceneFit();
        bool leftVisibleInNarrow() => narrow && leftVisible;
        bool rightVisibleInNarrow() => narrow && rightVisible;
    }

    private void RefreshWorkbenchPresentation()
    {
        var stale = _session.HasStalePlan && !_session.IsNcDocument;
        Preview.IsPlanStale = stale;
        StalePlanBadge.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        CanvasLegend.Visibility = _session.Document is not null || _session.IsNcDocument ? Visibility.Visible : Visibility.Collapsed;
        var returnedFromOrder = _showToolpathOrder && !_session.CanExport;
        if (returnedFromOrder) { _showToolpathOrder = false; Refilter(); }
        ContourOrderToggle.IsEnabled = _session.CanExport;
        ContourOrderToggle.IsChecked = _showToolpathOrder;
        ContourAllToggle.IsChecked = !_showToolpathOrder;
        ToolTipService.SetToolTip(ContourOrderToggle, _session.CanExport ? "查看本次有效刀路的加工顺序" : "生成有效刀路后查看加工顺序");
        if (returnedFromOrder) ContourCount.Text = "刀路待更新 · 已返回全部轮廓";
        DiagnosticShortcut.Content = $"诊断 {_diagnosticGroups.Count}";
        RefreshContourSelection();
        SetPreviewFocus();
        var config = _session.Config;
        if (!HasReusableConfigurationErrors)
        {
            var depths = NcGenerator.DepthPasses(config);
            var layers = depths.Count;
            var list = depths.Count <= 8 ? $"\n层位 Z：{string.Join("、", depths.Select(z => z.ToString("0.###")))}" : "";
            var passText = layers > 1
                ? config.Machining.LeaveSkin > 0 ? $"留皮 {config.Machining.LeaveSkin:0.###} mm · {layers} 遍" : $"分层 {layers} 遍"
                : "单次最终深度";
            DepthSummary.Text = $"切入 {config.SurfaceZ - config.Machining.CuttingDepth:0.###} mm · {passText} · 安全 Z 高出板面 {config.Machining.SafeZ - config.SurfaceZ:0.###} mm" + list;
            SafeDepthLabel.Text = $"安全 Z {config.Machining.SafeZ:0.###} mm";
            SurfaceDepthLabel.Text = $"板面 Z {config.SurfaceZ:0.###} mm";
            TargetDepthLabel.Text = $"目标 Z {config.Machining.CuttingDepth:0.###} mm";
        }
        else
        {
            DepthSummary.Text = "请先修正参数，再计算加工层数";
            SafeDepthLabel.Text = "安全 Z —"; SurfaceDepthLabel.Text = "板面 Z —"; TargetDepthLabel.Text = "目标 Z —";
        }
    }

    private void RefreshContourSelection()
    {
        if (SelectedContourHeading is null || _refiltering) return;
        var selected = ContourList.SelectedItems.OfType<ContourRow>().ToArray();
        ContourSelectionSummary.Text = $"选中 {selected.Length} · 加工 {_session.Contours.Count(c => c.IsIncluded)} / {_session.Contours.Count}";
        IncludeVisibleMenu.Text = $"加工当前显示的 {_filteredContours.Count} 个轮廓";
        ExcludeVisibleMenu.Text = $"不加工当前显示的 {_filteredContours.Count} 个轮廓";
        IncludeVisibleMenu.IsEnabled = ExcludeVisibleMenu.IsEnabled = _filteredContours.Count > 0;
        OutsideSelectedMenu.Text = $"选中 {selected.Length} 项设为外侧零件";
        InsideSelectedMenu.Text = $"选中 {selected.Length} 项设为内侧孔";
        OutsideSelectedMenu.IsEnabled = InsideSelectedMenu.IsEnabled = SelectedOutsideButton.IsEnabled = SelectedInsideButton.IsEnabled = selected.Length > 0;
        ContourSelectionDetails.Visibility = selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedContourHeading.Text = selected.Length == 1 ? $"选中轮廓 · {selected[0].Id}" : $"选中 {selected.Length} 个轮廓";
        var outside = selected.Length > 0 && selected.All(c => c.Side == CutSide.Outside);
        var inside = selected.Length > 0 && selected.All(c => c.Side == CutSide.Inside);
        SelectedOutsideButton.IsChecked = outside; SelectedInsideButton.IsChecked = inside;
        SelectedSideSummary.Text = outside ? "外侧：保留轮廓内的零件" : inside ? "内侧：加工孔的内部" : "混合加工侧 · 选择一侧批量应用";
        SelectedSideDiagram.Visibility = outside || inside ? Visibility.Visible : Visibility.Collapsed;
        if (outside || inside) SelectedSideDiagram.Side = inside ? CutSide.Inside : CutSide.Outside;
    }

    private void ContourMode_Click(object sender, RoutedEventArgs e)
    {
        _showToolpathOrder = ReferenceEquals(sender, ContourOrderToggle) && _session.CanExport;
        ContourOrderToggle.IsChecked = _showToolpathOrder; ContourAllToggle.IsChecked = !_showToolpathOrder;
        Refilter();
    }

    private void FocusSelectionOnCanvas()
    {
        _resultsExpanded = false;
        _narrowPanel = null;
        ApplyView();
        DispatcherQueue.TryEnqueue(() => { Preview.UpdateLayout(); Preview.FitToSelection(); });
    }

    private void Legend_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not ToggleButton { Tag: string layer } button) return;
        var enabled = button.IsChecked == true;
        var view = _session.CurrentView;
        _session.CurrentView = layer switch
        {
            "Source" => view with { ShowSource = enabled }, "Finished" => view with { ShowFinished = enabled },
            "Toolpath" => view with { ShowToolpaths = enabled }, "Rapid" => view with { ShowRapid = enabled },
            "Directions" => view with { ShowDirections = enabled }, _ => view
        };
    }

    private void ResultShortcut_Click(object sender, RoutedEventArgs e)
    {
        _showResults = true;
        SelectResultRoute(((sender as FrameworkElement)?.Tag as string) switch
        {
            "Code" => WorkbenchResultRoute.Code, "Diagnostics" => WorkbenchResultRoute.Diagnostics, _ => WorkbenchResultRoute.Summary
        });
        if (Root.ActualWidth < 900) _narrowPanel = "results";
        ApplyView();
    }

    private void BatchBoard_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BatchNcProgram program) return;
        _session.SelectBatchProgram(program.SheetContourId);
        _showResults = true;
        SelectResultRoute(WorkbenchResultRoute.Code);
        if (Root.ActualWidth < 900) _narrowPanel = "results";
        ApplyView();
        SetPreviewFocus();
    }

    private void ClearBatchBoard_Click(object sender, RoutedEventArgs e)
    {
        _session.SelectBatchProgram(null);
        SetPreviewFocus();
        ApplyView();
    }

    private void SetPreviewFocus()
    {
        var ids = ContourList.SelectedItems.OfType<ContourRow>().Select(row => row.Id);
        if (_session.SelectedBatchProgram is { } selected)
            ids = ids.Concat([selected.SheetContourId]).Concat(
                selected.Plan.Toolpaths.Select(path => $"{selected.SheetContourId}:{path.ContourId}"));
        Preview.SetFocused(ids.Distinct(StringComparer.Ordinal));
    }

    private void ExpandResults_Click(object sender, RoutedEventArgs e)
    {
        _resultsExpanded = !_resultsExpanded;
        _showResults = true;
        ApplyView();
    }

    private void NcResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_session.IsNcDocument || Root.ActualWidth < 900) return;
        if (_ncWidthBeforeDrag == 0)
        {
            _ncWidthBeforeDrag = _session.Store.NcReviewWidth;
            _session.Store.NcReviewWidth = RightColumn.ActualWidth;
        }
        _session.Store.NcReviewWidth -= e.HorizontalChange;
        ApplyView();
    }

    private void NcResize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_ncWidthBeforeDrag == 0) return;
        if (e.Canceled)
        {
            _session.Store.NcReviewWidth = _ncWidthBeforeDrag;
            _ncWidthBeforeDrag = 0;
            ApplyView();
            return;
        }
        try { _session.Store.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _session.Store.NcReviewWidth = _ncWidthBeforeDrag;
            ShowNotice("阅读布局未保存", error.Message, InfoBarSeverity.Warning);
            ApplyView();
        }
        finally { _ncWidthBeforeDrag = 0; }
    }

    private (Expander? Group, FrameworkElement Control)? ParameterTarget(string field)
    {
        if (field == "LeaveSkin") return (null, StepDownBox);
        var group = field switch
        {
            "BoardThickness" or "ZReference" or "Profile" or "OriginMode" or "SheetContourId" or "ManualOrigin" or "ManualOriginX" or "ManualOriginY" => MachineParametersExpander,
            "ToolNumber" or "LengthOffset" or "Diameter" or "ToolType" => ToolParametersExpander,
            "SpindleSpeed" or "CuttingFeed" or "PlungeFeed" => CommonParametersExpander,
            "CuttingDepth" or "SafeZ" or "EntryMode" or "RampLength" or "RampFeed" => PlungeParametersExpander,
            "ToolChange" or "ParkX" or "ParkY" or "ParkZ" => MachineOutputExpander,
            _ => DirectionParametersExpander
        };
        FrameworkElement? control = field switch
        {
            "ToolType" => ToolTypeBox, "SpindleDirection" => SpindleDirectionBox, "CutDirection" => CutDirectionBox,
            "Profile" => MachineProfileBox, "ZReference" => ZReferenceBox, "OriginMode" => OriginModeBox,
            "SheetContourId" => SheetContourBox, "ManualOrigin" => ManualOriginXBox,
            "EntryMode" => EntryModeBox, "ToolChange" => ToolChangeBox,
            _ => _numberBoxes.GetValueOrDefault(field)
        };
        return control is null ? null : (group, control);
    }

    private void FocusParameter(string field)
    {
        if (ParameterTarget(field) is not { } target) return;
        if (target.Group is not null) target.Group.IsExpanded = true;
        if (field is "RampLength" or "RampFeed") RampParametersPanel.Visibility = Visibility.Visible;
        if (field is "ParkX" or "ParkY" or "ParkZ") ParkingParametersPanel.Visibility = Visibility.Visible;
        if (field is "ManualOrigin" or "ManualOriginX" or "ManualOriginY") ManualOriginPanel.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { target.Control.StartBringIntoView(); target.Control.Focus(FocusState.Programmatic); });
    }

    private void PresentFirstValidationError(IReadOnlyDictionary<string, string> errors)
    {
        var field = errors.Keys.FirstOrDefault();
        if (field is null) { _presentedValidationField = null; return; }
        if (field == _presentedValidationField) return;
        _presentedValidationField = field;
        FocusParameter(field);
    }

    private void LocateDiagnosticSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiagnosticGroup { SourceLineIndices.Length: > 0 } diagnostic) return;
        SelectResultRoute(WorkbenchResultRoute.Code);
        var line = diagnostic.SourceLineIndices[0];
        if (line < 0 || line >= NcList.Items.Count) return;
        var row = NcList.Items[line];
        NcList.SelectedItems.Clear(); NcList.SelectedItems.Add(row);
        NcList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        NcList.Focus(FocusState.Programmatic);
    }
}
