using System.Text;
using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private bool _followTool = true;
    private readonly HashSet<string> _invalidNumberFields = new(StringComparer.Ordinal);
    private string _configurationName = "默认配置";
    private ConversionConfig _configurationBaseline = new();
    private void SetConfigurationSource(string name, ConversionConfig config, bool resetFollow = true)
    {
        _configurationName = name; _configurationBaseline = config;
        if (resetFollow) _followTool = config.Tool.ToolNumber == config.Tool.LengthOffset;
        _shownConfig = null;
    }

    private void ApplyNamedConfiguration(ParameterPreset preset)
    {
        _invalidNumberFields.Clear();
        SetConfigurationSource(preset.Name, preset.Config);
        _session.ApplyPreset(preset.Config);
        QueueRefresh();
    }

    private void RefreshConfigurationSummary()
    {
        var c = _session.Config;
        FollowToolCheck.IsChecked = _followTool;
        if (HasReusableConfigurationErrors) { ParameterResultText.Text = "请先修正参数中的错误，再查看加工结果。"; return; }
        var passes = NcGenerator.DepthPasses(c).Count;
        var passText = passes > 1
            ? c.Machining.LeaveSkin > 0 ? $"留皮 {c.Machining.LeaveSkin:0.###} mm · {passes} 遍" : $"分层 {passes} 遍"
            : "单次最终深度";
        ParameterResultText.Text = $"切入 {c.SurfaceZ - c.Machining.CuttingDepth:0.###} mm · {passText} · 安全 Z 高出板面 {c.Machining.SafeZ - c.SurfaceZ:0.###} mm";
    }

    private void FollowTool_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _numberBoxes is null) return;
        _followTool = FollowToolCheck.IsChecked == true;
        if (_followTool)
        {
            _invalidNumberFields.Remove("LengthOffset");
            _shownConfig = null;
            _session.SetInputError("LengthOffset", null);
            _session.UpdateConfig(_session.Config with { Tool = _session.Config.Tool with { LengthOffset = _session.Config.Tool.ToolNumber } });
        }
        QueueRefresh();
    }

    private bool ConfigurationActionBlocked => _fileWriteOpen || _dialogOpen || _pickerOpen || _openGate.IsBusy || _closeDialogOpen || _session.IsBusy;
    private bool HasReusableConfigurationErrors => _session.Config.GetValidationErrors().Count > 0 ||
        _session.ValidationErrors.Keys.Any(key => key is not ("SheetContourId" or "ManualOrigin" or "ManualOriginX" or "ManualOriginY"));

    private static ScrollViewer ConfigurationPreview(ConversionConfig current, ConversionConfig candidate, string introduction)
    {
        var list = new StackPanel { Spacing = 12, MaxWidth = 620 };
        list.Children.Add(new TextBlock { Text = introduction, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        list.Children.Add(new TextBlock
        {
            Text = $"有效加工策略\n当前：{EffectiveMachiningSummary(current)}\n导入：{EffectiveMachiningSummary(candidate)}",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = EffectiveMachiningSummary(current) == EffectiveMachiningSummary(candidate)
                ? Microsoft.UI.Text.FontWeights.Normal
                : Microsoft.UI.Text.FontWeights.SemiBold
        });
        foreach (var row in ParameterConfigurationStorage.Compare(current, candidate))
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{row.Label}{(row.IsChanged ? "（有变化）" : "")}\n{row.CurrentValue}  →  {row.ImportedValue}",
                FontSize = 14, TextWrapping = TextWrapping.Wrap,
                FontWeight = row.IsChanged ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
            });
        }
        return new ScrollViewer { Content = list, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static string EffectiveMachiningSummary(ConversionConfig config)
    {
        var valid = config.GetValidationErrors().Count == 0;
        var passes = valid ? NcGenerator.DepthPasses(config).Count : 0;
        return valid
            ? passes > 1 ? config.Machining.LeaveSkin > 0 ? $"留皮 {config.Machining.LeaveSkin:0.###} mm · {passes} 遍" : $"分层 {passes} 遍"
                : "单次最终深度"
            : "加工遍数待修正";
    }

    private async Task ImportConfigAsync()
    {
        if (ConfigurationActionBlocked) return;
        var path = await PickOpenAsync(".json");
        if (path is null) return;
        try
        {
            var preset = ParameterConfigurationStorage.Read(path);
            var revision = _session.Revision;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "导入参数配置：" + preset.Name,
                Content = ConfigurationPreview(_session.Config, preset.Config, "核对下列参数。左边是当前值，右边是导入值。应用后需要重新生成刀路，可使用撤销恢复。"),
                PrimaryButtonText = "应用到当前任务", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            _dialogOpen = true;
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            finally { _dialogOpen = false; }
            if (result != ContentDialogResult.Primary) return;
            if (_session.Revision != revision) { ShowNotice("参数尚未应用", "当前任务已变化，请重新导入并核对。", InfoBarSeverity.Warning); return; }
            ApplyNamedConfiguration(preset);
            ShowNotice("参数配置已应用", "可使用撤销恢复；需要复用时可在设置中导出参数。", InfoBarSeverity.Success);
        }
        catch (Exception error) { ShowNotice("配置未导入", error.Message, InfoBarSeverity.Error); }
    }

    private async Task ExportConfigAsync()
    {
        if (ConfigurationActionBlocked) return;
        if (HasReusableConfigurationErrors) { ShowNotice("参数仍有错误", "修正参数后再导出配置。", InfoBarSeverity.Warning); return; }
        var config = _session.Config;
        var name = config == _configurationBaseline ? _configurationName : "自定义加工参数";
        var filename = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ParameterConfigurationStorage.Extension;
        _fileWriteOpen = true; QueueRefresh();
        try
        {
            while (true)
            {
                string? selected;
                _pickerOpen = true;
                try
                {
                    var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = filename, DefaultFileExtension = ".json", ShowOverwritePrompt = false };
                    picker.FileTypeChoices.Add("加工参数配置 (.cad2nc.config.json)", new[] { ".json" });
                    selected = (await picker.PickSaveFileAsync())?.Path;
                }
                finally { _pickerOpen = false; }
                if (selected is null) return;
                var path = ParameterConfigurationStorage.NormalizePath(selected);
                if (path.Equals(_session.TaskPath, StringComparison.OrdinalIgnoreCase) || path.Equals(_session.SourcePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("参数配置不能覆盖当前加工任务或源图纸，请选择其它文件名。");
                if (File.Exists(path))
                {
                    var result = await ShowDialogAsync("配置文件已存在", path, "覆盖", "取消", "另存为…");
                    if (result == ContentDialogResult.Secondary) continue;
                    if (result != ContentDialogResult.Primary) return;
                }
                path = ParameterConfigurationStorage.Save(path, name, config);
                ShowNotice("参数配置已导出", path, InfoBarSeverity.Success);
                var open = new Button { Content = "打开文件夹" }; open.Click += async (_, _) => await OpenDirectoryAsync(Path.GetDirectoryName(path)!);
                Notice.ActionButton = open;
                return;
            }
        }
        catch (Exception error) { ShowNotice("配置未导出", error.Message, InfoBarSeverity.Error); }
        finally { _fileWriteOpen = false; QueueRefresh(); }
    }

}
