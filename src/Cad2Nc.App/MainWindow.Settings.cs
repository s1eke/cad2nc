using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private bool _settingsInitialized, _syncingSettings, _settingsActionPending;
    private ComboBox? _settingsTheme;
    private TextBlock? _settingsTaskDirectory, _settingsNcDirectory, _settingsError, _settingsParameterHint, _settingsStartupSummary;
    private readonly List<Control> _settingsPreferenceControls = [];
    private readonly Dictionary<string, Button> _settingsParameterButtons = [];

    private bool SettingsActionBlocked => ConfigurationActionBlocked || _closingApproved || _settingsActionPending;

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateToPage(AppPage.Settings);

    private void RefreshSettings()
    {
        if (!_settingsInitialized) InitializeSettings();
        _syncingSettings = true;
        try
        {
            _settingsTheme!.SelectedIndex = _session.Store.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            _settingsTaskDirectory!.Text = _session.Store.TaskDirectory;
            _settingsNcDirectory!.Text = _session.Store.NcDirectory;
            foreach (var control in _settingsPreferenceControls) control.IsEnabled = !SettingsActionBlocked;
            var parametersEnabled = !SettingsActionBlocked && !_session.IsNcDocument;
            foreach (var (action, button) in _settingsParameterButtons)
                button.IsEnabled = parametersEnabled && (action is not ("export" or "startup") || !HasReusableConfigurationErrors);
            var startup = _session.Store.StartupConfig;
            _settingsStartupSummary!.Text = $"启动默认：{ConfigurationDefaults.Name(startup.Machine.Profile)}\n板厚 {startup.Machining.BoardThickness:0.###} mm · {(startup.Machining.ZReference == ZReference.WorkpieceBottom ? "板底 Z0" : "板面 Z0")} · 安全 Z{startup.Machining.SafeZ:0.###}\n{LeaveSkinLabel(startup)}";
            _settingsParameterHint!.Text = _session.IsNcDocument
                ? "当前为只读 NC。打开 CAD 后可管理加工参数和启动默认值。"
                : _session.IsBusy ? "处理完成后可管理参数配置。"
                : HasReusableConfigurationErrors ? "当前工艺参数有错误，修正后可导出或保存为启动默认；也可导入或恢复机床内置值。"
                : "导入、恢复或切换机床后需重新生成刀路，可撤销。启动默认不包含本图母板与手动原点，不改变其他任务。";
        }
        finally { _syncingSettings = false; }
    }

    private void InitializeSettings()
    {
        _settingsInitialized = true;
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = "设置", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(SettingsNote("管理应用外观、保存位置与可复用的加工参数。"));
        SettingsContent.Children.Add(heading);
        _settingsError = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["InspectionError"]
        };
        AutomationProperties.SetAutomationId(_settingsError, "SettingsError");
        AutomationProperties.SetLiveSetting(_settingsError, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        SettingsContent.Children.Add(_settingsError);

        var appearance = AddSettingsCard("外观");
        _settingsTheme = new ComboBox
        {
            Header = "应用主题", ItemsSource = new[] { "跟随系统", "浅色", "深色" },
            MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(_settingsTheme, "SettingsTheme");
        AutomationProperties.SetName(_settingsTheme, "应用主题");
        _settingsTheme.SelectionChanged += SettingsTheme_Changed;
        _settingsPreferenceControls.Add(_settingsTheme);
        appearance.Children.Add(_settingsTheme);

        var files = AddSettingsCard("文件保存");
        AddSettingsDirectory(files, "默认任务文件夹", false);
        AddSettingsDirectory(files, "默认 NC 文件夹", true);
        files.Children.Add(SettingsNote("目录更改立即保存，已有文件保留在原位置。"));

        var parameters = AddSettingsCard("参数配置");
        _settingsStartupSummary = SettingsNote("");
        parameters.Children.Add(_settingsStartupSummary);
        _settingsParameterHint = SettingsNote("");
        parameters.Children.Add(_settingsParameterHint);
        var parameterActions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        parameterActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parameterActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parameterActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        parameterActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (label, action, id) in new[]
        {
            ("导入参数", "import", "SettingsImport"),
            ("导出当前参数", "export", "SettingsExport"),
            ("保存当前参数为启动默认", "startup", "SettingsSaveStartup"),
            ("恢复所选机床内置值", "reset", "SettingsReset")
        })
        {
            var button = new Button
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40, Padding = new Thickness(10, 8, 10, 8)
            };
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, label);
            button.Click += async (_, _) => await RunSettingsParameterActionAsync(action);
            Grid.SetColumn(button, _settingsParameterButtons.Count % 2);
            Grid.SetRow(button, _settingsParameterButtons.Count / 2);
            parameterActions.Children.Add(button);
            _settingsParameterButtons.Add(action, button);
        }
        parameters.Children.Add(parameterActions);

        var about = AddSettingsCard("关于 CAD2NC");
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        about.Children.Add(new TextBlock { Text = $"CAD2NC {version} · WinUI 3", FontSize = 14 });
        about.Children.Add(SettingsNote("二维 CAD 轮廓与预补偿 NC 加工工作台"));
        about.Children.Add(SettingsNote("GPLv3 · ACadSharp / NetTopologySuite · 第三方许可随发布包提供。"));
    }

    private StackPanel AddSettingsCard(string title)
    {
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        SettingsContent.Children.Add(new Border
        {
            Child = content, Padding = new Thickness(20), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["InspectionCard"]
        });
        return content;
    }

    private static TextBlock SettingsNote(string text) => new()
    {
        Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["InspectionNeutral"]
    };

    private void AddSettingsDirectory(StackPanel card, string label, bool nc)
    {
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var path = SettingsNote("");
        path.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(path, nc ? "SettingsNcDirectory" : "SettingsTaskDirectory");
        if (nc) _settingsNcDirectory = path; else _settingsTaskDirectory = path;
        group.Children.Add(path);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Add("更改…", "更改" + label, async () => await ChooseSettingsDirectoryAsync(nc));
        Add("打开", "打开" + label, async () => await OpenSettingsDirectoryAsync(nc));
        Add("恢复默认", "恢复" + label, () => { ResetSettingsDirectory(nc); return Task.CompletedTask; });
        group.Children.Add(actions);
        card.Children.Add(group);

        void Add(string text, string name, Func<Task> action)
        {
            var button = new Button { Content = text, MinHeight = 40 };
            AutomationProperties.SetName(button, name);
            button.Click += async (_, _) => await action();
            actions.Children.Add(button);
            _settingsPreferenceControls.Add(button);
        }
    }

    private async Task ChooseSettingsDirectoryAsync(bool nc)
    {
        if (SettingsActionBlocked) return;
        _pickerOpen = true;
        RefreshSettings(); QueueRefresh();
        try
        {
            var current = nc ? _session.Store.NcDirectory : _session.Store.TaskDirectory;
            var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            if (Directory.Exists(current)) picker.SuggestedFolder = current;
            var selected = await picker.PickSingleFolderAsync();
            if (selected is null || _closingApproved) return;
            if (nc) _session.Store.UpdateNcDirectory(selected.Path); else _session.Store.UpdateTaskDirectory(selected.Path);
            ClearSettingsError();
        }
        catch (Exception error) { ReportSettingsError(error.Message); }
        finally { _pickerOpen = false; RefreshSettings(); QueueRefresh(); }
    }

    private async Task OpenSettingsDirectoryAsync(bool nc)
    {
        if (SettingsActionBlocked) return;
        _settingsActionPending = true;
        RefreshSettings();
        try
        {
            var current = nc ? _session.Store.NcDirectory : _session.Store.TaskDirectory;
            Directory.CreateDirectory(current);
            if (!await Launcher.LaunchFolderPathAsync(current)) throw new IOException("无法打开文件夹：" + current);
            ClearSettingsError();
        }
        catch (Exception error) { ReportSettingsError(error.Message); }
        finally { _settingsActionPending = false; RefreshSettings(); }
    }

    private void ResetSettingsDirectory(bool nc)
    {
        if (SettingsActionBlocked) return;
        try
        {
            if (nc) _session.Store.UpdateNcDirectory(null); else _session.Store.UpdateTaskDirectory(null);
            ClearSettingsError();
        }
        catch (Exception error) { ReportSettingsError(error.Message); }
        finally { RefreshSettings(); QueueRefresh(); }
    }

    private void SettingsTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings || _settingsTheme is null) return;
        if (SettingsActionBlocked) { RefreshSettings(); return; }
        var previous = _session.Store.Theme;
        var selected = _settingsTheme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" };
        if (selected == previous) return;
        try
        {
            _session.Store.Theme = selected;
            _session.Store.Save();
            _session.CurrentView = _session.CurrentView with { Theme = selected };
            ClearSettingsError();
        }
        catch (Exception error)
        {
            _session.Store.Theme = previous;
            ReportSettingsError(error.Message);
        }
        finally { RefreshSettings(); QueueRefresh(); }
    }

    private async Task RunSettingsParameterActionAsync(string action)
    {
        if (SettingsActionBlocked || _session.IsNcDocument) return;
        _settingsActionPending = true;
        ClearSettingsError(); RefreshSettings();
        try
        {
            if (action == "import") await ImportConfigAsync();
            else if (action == "export") await ExportConfigAsync();
            else if (action == "startup")
            {
                if (HasReusableConfigurationErrors) throw new InvalidOperationException("请先修正工艺参数，再保存启动默认值。");
                _session.Store.SaveStartupConfig(_session.Config);
                ShowNotice("启动默认参数已保存", "下次启动使用当前工艺参数；已打开及已保存的其他任务保持各自参数。", InfoBarSeverity.Success);
            }
            else if (action == "reset")
            {
                var profile = _session.Config.Machine.Profile;
                ApplyNamedConfiguration(new ParameterPreset(ConfigurationDefaults.Name(profile), ConfigurationDefaults.ForProfile(profile)));
                ShowNotice("已恢复所选机床内置值", "可使用撤销恢复之前的参数；需重新生成刀路。启动默认值保持原设置。", InfoBarSeverity.Success);
            }
        }
        catch (Exception error) { ReportSettingsError(error.Message); }
        finally { _settingsActionPending = false; RefreshSettings(); QueueRefresh(); }
    }

    private void ReportSettingsError(string message)
    {
        if (!_settingsInitialized) InitializeSettings();
        _settingsError!.Text = message;
        _settingsError.Visibility = Visibility.Visible;
        _settingsError.StartBringIntoView();
    }

    private void ClearSettingsError()
    {
        if (_settingsError is null) return;
        _settingsError.Text = "";
        _settingsError.Visibility = Visibility.Collapsed;
    }
}
