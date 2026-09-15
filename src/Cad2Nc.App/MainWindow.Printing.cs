using System.Collections.Immutable;
using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private readonly IBoardLabelPrinter _labelPrinter = new WindowsBoardLabelPrinter();
    private bool _printingLabels;

    private bool CanPrintLabels => !_printingLabels && !_fileWriteOpen && !_session.IsBusy &&
        !_session.IsNcDocument && _session.CanExport;

    private async void PrintLabel_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPrintLabels) return;
        var printedAt = DateTimeOffset.UtcNow;
        var all = BuildLabelBatch(printedAt);
        if (all.IsEmpty) { ShowNotice("暂时无法打印标签", "请先生成一份有效刀路。", InfoBarSeverity.Warning); return; }

        var labels = await ChooseLabelScopeAsync(all);
        if (labels is null || labels.IsEmpty) return;
        if (!CanPrintLabels) { ShowNotice("打印已取消", "当前图纸或刀路状态已变化，请重新生成后再试。", InfoBarSeverity.Warning); return; }

        _printingLabels = true;
        QueueRefresh();
        try
        {
            ShowNotice("正在准备标签", $"即将打印 {labels.Labels.Length} 张 60×40 mm 标签，请在系统打印窗口确认纸张规格。", InfoBarSeverity.Informational);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            await _labelPrinter.PrintAsync(hwnd, labels.Labels);
            ShowNotice("打印任务已提交", $"已提交 {labels.Labels.Length} 张母板标签。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice("无法打印标签", error.Message, InfoBarSeverity.Error); }
        finally { _printingLabels = false; QueueRefresh(); }
    }

    private BoardLabelBatch BuildLabelBatch(DateTimeOffset printedAt)
    {
        if (_session.Document is not { } document) return new([]);
        if (_session.IsBatchMode && _session.BatchResult is { } batch)
            return BoardLabelBuilder.BuildBatch(document, batch, null, printedAt, GetOrderName());
        if (_session.Plan is { } plan)
        {
            var ncFile = Path.GetFileName(SavePaths.NcPath(_session.SourcePath, _session.Store.NcDirectory));
            return BoardLabelBuilder.BuildSingle(document, plan, ncFile, printedAt, GetOrderName());
        }
        return new([]);
    }

    private string GetOrderName()
    {
        var taskName = _session.TaskPath is null ? null : Path.GetFileNameWithoutExtension(_session.TaskPath);
        if (taskName?.EndsWith(".cad2nc", StringComparison.OrdinalIgnoreCase) == true)
            taskName = taskName[..^7];
        return string.IsNullOrWhiteSpace(taskName)
            ? Path.GetFileNameWithoutExtension(_session.SourcePath)
            : taskName;
    }

    private async Task<BoardLabelBatch?> ChooseLabelScopeAsync(BoardLabelBatch all)
    {
        if (all.Labels.Length == 1) return all;
        if (_dialogOpen) return null;

        var currentId = _session.SelectedBatchSheetId;
        var allRadio = new RadioButton { Content = $"全部母板（{all.Labels.Length} 张）", IsChecked = true };
        var currentRadio = new RadioButton
        {
            Content = currentId is null ? "当前母板（请先在结果列表中选择）" : $"当前母板（{currentId}）",
            IsEnabled = currentId is not null
        };
        var preview = await BoardLabelPage.CreateAsync(all.Labels[0]);
        preview.HorizontalAlignment = HorizontalAlignment.Left;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "标签为 60×40 mm 横向热敏格式；二维码只包含离线板卡身份信息。请在系统打印窗口确认打印机纸张已设为 60×40 mm。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(allRadio);
        content.Children.Add(currentRadio);
        content.Children.Add(new TextBlock { Text = "预览（第一张）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        content.Children.Add(preview);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "选择打印范围",
            Content = content,
            PrimaryButtonText = "打印",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            if (currentRadio.IsChecked == true && currentId is not null)
                return new(all.Labels.Where(label => string.Equals(label.BoardId, currentId, StringComparison.Ordinal)).ToImmutableArray());
            return all;
        }
        finally { _dialogOpen = false; }
    }
}
