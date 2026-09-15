using Cad2Nc.Application;
using Cad2Nc.App.Controls;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private sealed record SheetChoice(string? Id, string Label);
    private CadDocument? _sheetChoiceDocument;
    private WorkpieceSetup? _sceneWorkpiece;
    private OriginMode? _sceneOriginMode;

    private void SyncMachineParameters()
    {
        var c = _session.Config;
        void SetNumber(string key, double value)
        {
            if (!_invalidNumberFields.Contains(key)) _numberBoxes[key].Value = value;
        }
        MachineProfileBox.SelectedIndex = c.Machine.Profile == MachineProfile.LncRouter ? 0 : 1;
        MachineConfigurationText.Text = c == _configurationBaseline ? _configurationName : "当前任务已自定义 · 切换机床会应用该机床内置参数";
        ZReferenceBox.SelectedIndex = c.Machining.ZReference == ZReference.WorkpieceBottom ? 0 : 1;
        OriginModeBox.SelectedIndex = c.OriginMode switch { OriginMode.SheetLowerLeft => 0, OriginMode.Manual => 1, _ => 2 };
        EntryModeBox.SelectedIndex = c.Machining.EntryMode == EntryMode.Ramp ? 0 : 1;
        ToolChangeBox.SelectedIndex = c.Machine.ToolChange == ToolChangeMode.ToolOnly ? 0 : 1;
        ProgramEnvelopeCheck.IsChecked = c.Machine.UseProgramEnvelope;
        ParkAtEndCheck.IsChecked = c.Machine.ParkAtEnd;
        EmitM17Check.IsChecked = c.Machine.EmitM17;
        SetNumber("BoardThickness", c.Machining.BoardThickness);
        SetNumber("RampLength", c.Machining.RampLength); SetNumber("RampFeed", c.Machining.RampFeed);
        SetNumber("ParkX", c.Machine.ParkX); SetNumber("ParkY", c.Machine.ParkY); SetNumber("ParkZ", c.Machine.ParkZ);
        SetNumber("ManualOriginX", _session.Workpiece.ManualOrigin?.X ?? double.NaN);
        SetNumber("ManualOriginY", _session.Workpiece.ManualOrigin?.Y ?? double.NaN);
        RampParametersPanel.Visibility = c.Machining.EntryMode == EntryMode.Ramp ? Visibility.Visible : Visibility.Collapsed;
        ParkingParametersPanel.Visibility = c.Machine.ParkAtEnd ? Visibility.Visible : Visibility.Collapsed;
        PlungeFeedBox.Header = c.Machining.EntryMode == EntryMode.Ramp ? "移至板面进给" : "垂直下刀进给";
        var sheetCount = _session.Document is { } currentDocument
            ? ToolpathPlanner.FindSheetCandidates(currentDocument).Count : 0;
        var multiBoard = sheetCount > 1;
        SheetContourBox.Visibility = c.OriginMode == OriginMode.SheetLowerLeft && !multiBoard ? Visibility.Visible : Visibility.Collapsed;
        MultiBoardModeText.Visibility = multiBoard ? Visibility.Visible : Visibility.Collapsed;
        if (multiBoard)
            MultiBoardModeText.Text = $"检测到 {sheetCount} 块母板；每个 NC 对应一块母板，导出时使用各自左下角 G54。";
        ManualOriginPanel.Visibility = c.OriginMode == OriginMode.Manual && !multiBoard ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(_sheetChoiceDocument, _session.Document))
        {
            _sheetChoiceDocument = _session.Document;
            var choices = new List<SheetChoice> { new(null, "自动识别唯一母板") };
            if (_session.Document is { } document)
                choices.AddRange(document.Contours.Where(ToolpathPlanner.IsSheetRectangle).OrderByDescending(x => x.Area)
                    .Select(x => new SheetChoice(x.Id, $"{x.Id} · {x.Bounds.Width:0.###} × {x.Bounds.Height:0.###} mm · {x.Layer}")));
            SheetContourBox.ItemsSource = choices;
        }
        var selected = SheetContourBox.Items.OfType<SheetChoice>().FirstOrDefault(x => x.Id == _session.Workpiece.SheetContourId);
        SheetContourBox.SelectedItem = selected ?? SheetContourBox.Items.OfType<SheetChoice>().FirstOrDefault();
        SheetContourBox.IsEnabled = _session.Document is not null;
        WorkpieceOriginText.Text = CurrentOriginDescription();
        DepthReferenceText.Text = c.Machining.ZReference == ZReference.WorkpieceBottom
            ? $"板底 Z0 · 板面 Z{c.SurfaceZ:0.###}；目标 Z 是绝对工件坐标。"
            : "板面 Z0；目标 Z 是绝对工件坐标，向下为负。";
        var parametersEnabled = !ConfigurationActionBlocked && !_session.IsNcDocument;
        StepDownBox.IsEnabled = parametersEnabled;
        foreach (var control in ParameterPanel.Children.OfType<Control>())
            control.IsEnabled = parametersEnabled;
        OriginModeBox.IsEnabled = !multiBoard && !ConfigurationActionBlocked && !_session.IsNcDocument;
        SheetContourBox.IsEnabled = !multiBoard && _session.Document is not null && !ConfigurationActionBlocked && !_session.IsNcDocument;
    }

    private void MachineProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ConfigurationActionBlocked || MachineProfileBox.SelectedIndex < 0) return;
        var profile = MachineProfileBox.SelectedIndex == 0 ? MachineProfile.LncRouter : MachineProfile.LegacyFanuc;
        if (profile == _session.Config.Machine.Profile) return;
        var config = ConfigurationDefaults.ForProfile(profile);
        ApplyNamedConfiguration(new ParameterPreset(ConfigurationDefaults.Name(profile), config));
        ShowNotice("机床内置参数已应用", "当前任务需重新生成刀路；可使用撤销恢复之前的参数。", InfoBarSeverity.Success);
    }

    private void MachineOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ConfigurationActionBlocked) return;
        var c = _session.Config;
        if (ReferenceEquals(sender, ZReferenceBox))
            c = c with { Machining = c.Machining with { ZReference = ZReferenceBox.SelectedIndex == 0 ? ZReference.WorkpieceBottom : ZReference.WorkpieceTop } };
        else if (ReferenceEquals(sender, OriginModeBox))
            c = c with { OriginMode = OriginModeBox.SelectedIndex switch { 0 => OriginMode.SheetLowerLeft, 1 => OriginMode.Manual, _ => OriginMode.ToolpathMinimum } };
        else if (ReferenceEquals(sender, EntryModeBox))
            c = c with { Machining = c.Machining with { EntryMode = EntryModeBox.SelectedIndex == 0 ? EntryMode.Ramp : EntryMode.Vertical } };
        else if (ReferenceEquals(sender, ToolChangeBox))
            c = c with { Machine = c.Machine with { ToolChange = ToolChangeBox.SelectedIndex == 0 ? ToolChangeMode.ToolOnly : ToolChangeMode.ToolWithM06 } };
        _session.UpdateConfig(c);
    }

    private void MachineFlag_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || ConfigurationActionBlocked) return;
        var c = _session.Config;
        if (ReferenceEquals(sender, ProgramEnvelopeCheck)) c = c with { Machine = c.Machine with { UseProgramEnvelope = ProgramEnvelopeCheck.IsChecked == true } };
        else if (ReferenceEquals(sender, ParkAtEndCheck)) c = c with { Machine = c.Machine with { ParkAtEnd = ParkAtEndCheck.IsChecked == true } };
        else if (ReferenceEquals(sender, EmitM17Check)) c = c with { Machine = c.Machine with { EmitM17 = EmitM17Check.IsChecked == true } };
        _session.UpdateConfig(c);
    }

    private void SheetContour_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ConfigurationActionBlocked || SheetContourBox.SelectedItem is not SheetChoice choice) return;
        _session.UpdateWorkpiece(_session.Workpiece with { SheetContourId = choice.Id });
    }

    private Point2? CurrentFixedOrigin()
    {
        if (_session.Document is { } multiDocument && ToolpathPlanner.FindSheetCandidates(multiDocument).Count > 1)
            return null;
        if (_session.Config.OriginMode == OriginMode.Manual)
            return _session.Workpiece.ManualOrigin is { } point && double.IsFinite(point.X) && double.IsFinite(point.Y) ? point : null;
        if (_session.Config.OriginMode != OriginMode.SheetLowerLeft || _session.Document is not { } document) return null;
        try
        {
            var sheet = ToolpathPlanner.ResolveSheetContour(document, _session.Workpiece);
            return new(sheet.Bounds.MinX, sheet.Bounds.MinY);
        }
        catch (ToolpathGenerationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private string CurrentOriginDescription()
    {
        if (_session.Document is { } multiDocument && ToolpathPlanner.FindSheetCandidates(multiDocument).Count > 1)
            return "多母板模式：预览合并显示；导出按母板拆分，每个 NC 使用对应母板左下角 G54。";
        if (_session.Config.OriginMode == OriginMode.ToolpathMinimum) return "旧版原点随所选刀心路线整体包围框变化。";
        if (CurrentFixedOrigin() is { } point)
        {
            var sheetDescription = "";
            if (_session.Config.OriginMode == OriginMode.SheetLowerLeft && _session.Document is { } document)
            {
                var sheet = ToolpathPlanner.ResolveSheetContour(document, _session.Workpiece);
                sheetDescription = $"母板 {sheet.Id} · {sheet.Bounds.Width:0.###} × {sheet.Bounds.Height:0.###} mm；";
            }
            return sheetDescription + $"G54 对应 CAD ({point.X:0.###}, {point.Y:0.###}) mm；原点不随加工勾选变化。";
        }
        return _session.Config.OriginMode == OriginMode.Manual
            ? "请输入本图的 CAD 原点 X、Y。原点随任务保存。"
            : _session.Document is null ? "打开 CAD 后自动识别唯一明确的矩形母板。"
            : "未找到匹配母板，请选择矩形轮廓或改用手动原点。";
    }
}
