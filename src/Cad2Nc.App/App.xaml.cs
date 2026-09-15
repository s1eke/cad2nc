using Microsoft.UI.Xaml;

namespace Cad2Nc.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        Program.AttachWindow(_window);
        _window.Activate();
    }
}
