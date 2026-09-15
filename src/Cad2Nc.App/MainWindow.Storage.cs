using Cad2Nc.Application;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private bool _fileWriteOpen;

    private async Task<bool> SaveFileAsync(bool nc, bool forcePicker)
    {
        if (_fileWriteOpen || _dialogOpen || _pickerOpen || _session.Document is null || _session.IsBusy ||
            (_openGate.IsBusy && !_confirmingDiscard) || (_closeDialogOpen && !_confirmingDiscard)) return false;
        if (nc && !_session.CanExport) return false;
        _fileWriteOpen = true;
        QueueRefresh();
        var document = _session.Document;
        try
        {
            var directory = nc ? _session.Store.NcDirectory : _session.Store.TaskDirectory;
            if (nc && _session.IsBatchMode)
            {
                if (forcePicker)
                {
                    var pickedDirectory = await PickExportDirectoryAsync(directory);
                    if (pickedDirectory is null) return false;
                    directory = pickedDirectory;
                }
                var batch = _session.BatchResult!;
                var existing = batch.Programs.Select(program => Path.Combine(directory, program.SuggestedFileName))
                    .Where(File.Exists).ToArray();
                if (existing.Length > 0)
                {
                    var decision = await ShowDialogAsync("批量 NC 文件已存在",
                        string.Join(Environment.NewLine, existing), "覆盖全部", "取消");
                    if (decision != ContentDialogResult.Primary) return false;
                }
                if (!ReferenceEquals(document, _session.Document)) return false;
                var paths = await _session.ExportBatchNcAsync(directory);
                ShowNotice("批量 NC 已导出", string.Join(Environment.NewLine, paths), InfoBarSeverity.Success);
                var openBatch = new Button { Content = "打开文件夹" };
                openBatch.Click += async (_, _) => await OpenDirectoryAsync(directory);
                Notice.ActionButton = openBatch;
                return true;
            }
            var path = nc ? SavePaths.NcPath(_session.SourcePath, directory) : SavePaths.TaskPath(_session.SourcePath, directory, _session.TaskPath);
            var existingTask = !nc && !forcePicker && _session.TaskPath is not null &&
                path.Equals(Path.GetFullPath(_session.TaskPath), StringComparison.OrdinalIgnoreCase);
            while (true)
            {
                if (forcePicker)
                {
                    var picked = await PickSaveAsync(nc, directory, Path.GetFileName(path));
                    if (picked is null) return false;
                    path = nc ? SavePaths.NormalizeNcPath(picked) : SavePaths.NormalizeTaskPath(picked);
                    existingTask = false;
                }
                if (File.Exists(path) && !existingTask)
                {
                    var decision = await ShowDialogAsync("文件已存在", path, "覆盖", "取消", "另存为…");
                    if (decision == ContentDialogResult.Secondary) { forcePicker = true; continue; }
                    if (decision != ContentDialogResult.Primary) return false;
                }
                // An awaited picker must never cause a different document to be written.
                if (!ReferenceEquals(document, _session.Document)) return false;
                if (nc) await _session.ExportNcAsync(path);
                else await _session.SaveTaskAsync(path);
                ShowNotice(nc ? "NC 已导出" : "任务已保存", path, InfoBarSeverity.Success);
                var open = new Button { Content = "打开文件夹" };
                open.Click += async (_, _) => await OpenDirectoryAsync(Path.GetDirectoryName(path)!);
                Notice.ActionButton = open;
                return true;
            }
        }
        catch (Exception error)
        {
            ShowNotice(nc ? "导出失败" : "保存失败", error.Message, InfoBarSeverity.Error);
            var retry = new Button { Content = "更换保存位置…" };
            retry.Click += async (_, _) => await SaveFileAsync(nc, true);
            Notice.ActionButton = retry;
            return false;
        }
        finally { _fileWriteOpen = false; QueueRefresh(); }
    }

    private async Task<string?> PickSaveAsync(bool nc, string directory, string suggested)
    {
        if (_pickerOpen) return null;
        _pickerOpen = true;
        try
        {
            // FileTypeChoices does not accept compound suffixes. Keep the full task
            // suffix in the name and confirm overwrite only after path normalization.
            try { Directory.CreateDirectory(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            var extension = nc ? ".nc" : ".json";
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = suggested,
                DefaultFileExtension = extension,
                ShowOverwritePrompt = false
            };
            if (Directory.Exists(directory)) picker.SuggestedFolder = directory;
            picker.FileTypeChoices.Add(nc ? "NC 程序" : "加工任务 (.cad2nc.json)", new[] { extension });
            return (await picker.PickSaveFileAsync())?.Path;
        }
        finally { _pickerOpen = false; }
    }

    private async Task<string?> PickExportDirectoryAsync(string current)
    {
        if (_pickerOpen) return null;
        _pickerOpen = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            if (Directory.Exists(current)) picker.SuggestedFolder = current;
            return (await picker.PickSingleFolderAsync())?.Path;
        }
        finally { _pickerOpen = false; }
    }

    private async Task OpenDirectoryAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (!await Launcher.LaunchFolderPathAsync(directory)) throw new IOException("无法打开文件夹：" + directory);
        }
        catch (Exception error) { ShowNotice("无法打开文件夹", error.Message, InfoBarSeverity.Error); }
    }

}
