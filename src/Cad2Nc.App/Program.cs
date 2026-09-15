using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Cad2Nc.Application;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Cad2Nc.App;

internal static class Program
{
    private const string GuiInstanceKey = "CAD2NC.Gui";
    internal const string InstallMutexName = @"Local\CAD2NC.Gui.InstallLock";
    private static readonly object ActivationSync = new();
    private static readonly Queue<FileOpenRequest> PendingActivations = new();
    private static MainWindow? window;
    private static DispatcherQueue? dispatcher;
    private static bool drainQueued;

    [STAThread]
    private static int Main(string[] args)
    {
        // Conversion processes must never register or redirect to the GUI instance.
        if (CommandLine.IsRequested(args)) return CommandLine.RunAsync(args).GetAwaiter().GetResult();

        AppInstance? current = null;
        var ownsKey = false;
        Mutex? installMutex = null;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            current = AppInstance.GetCurrent();
            var activation = current.GetActivatedEventArgs();
            // Subscribe before exposing the key, so an immediate second launch cannot be lost.
            current.Activated += OnActivated;
            QueueActivation(FileOpenRequest.FromPaths(args, allowTask: true));
            var target = AppInstance.FindOrRegisterForKey(GuiInstanceKey);
            ownsKey = target.IsCurrent;
            if (!ownsKey)
            {
                RedirectActivation(activation, target);
                return 0;
            }

            // Inno Setup checks existence of this mutex while this GUI process is alive.
            installMutex = new Mutex(false, InstallMutexName);
            // Match the installed shortcuts so pinned taskbar entries group with this window.
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("CAD2NC.Desktop"));
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            return 0;
        }
        catch (Exception error)
        {
            _ = MessageBoxW(IntPtr.Zero, $"CAD2NC 未能打开。已有窗口可能正在退出或暂时无响应，请稍后重试。\n\n{error.Message}", "CAD2NC", 0x10);
            return 1;
        }
        finally
        {
            lock (ActivationSync) { window = null; dispatcher = null; PendingActivations.Clear(); }
            if (current is not null)
            {
                current.Activated -= OnActivated;
                if (ownsKey) current.UnregisterKey();
            }
            installMutex?.Dispose();
        }
    }

    internal static void AttachWindow(MainWindow mainWindow)
    {
        // Called on the UI thread before Activate, retaining the initial request until Loaded.
        lock (ActivationSync)
        {
            window = mainWindow;
            dispatcher = mainWindow.DispatcherQueue;
            while (PendingActivations.TryDequeue(out var request)) mainWindow.HandleActivation(request);
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        try
        {
            FileOpenRequest request;
            if (args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch)
            {
                var paths = WindowsLaunchArguments.Parse(launch.Arguments);
                // Explorer supplies absolute paths. Do not resolve another process's relative path
                // against this process's different working directory.
                request = paths.Any(path => !Path.IsPathFullyQualified(path))
                    ? new(null, Error: "请拖入图纸，或使用文件的完整路径打开。")
                    : FileOpenRequest.FromPaths(paths, allowTask: true);
            }
            else if (args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs file)
                request = FileOpenRequest.FromPaths(file.Files.Select(item => item.Path).ToArray(), allowTask: true);
            else request = new(Path: null);
            QueueActivation(request);
        }
        catch (Exception error)
        {
            QueueActivation(new(null, Error: $"无法读取打开请求：{error.Message}"));
        }
    }

    private static void QueueActivation(FileOpenRequest request)
    {
        lock (ActivationSync)
        {
            PendingActivations.Enqueue(request);
            if (dispatcher is null || drainQueued) return;
            drainQueued = dispatcher.TryEnqueue(DrainActivations);
        }
    }

    private static void DrainActivations()
    {
        while (true)
        {
            FileOpenRequest request;
            MainWindow? target;
            lock (ActivationSync)
            {
                if (!PendingActivations.TryDequeue(out request!)) { drainQueued = false; return; }
                target = window;
            }
            target?.HandleActivation(request);
        }
    }

    private static void RedirectActivation(AppActivationArguments activation, AppInstance target)
    {
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        ExceptionDispatchInfo? failure = null;
        _ = Task.Run(async () =>
        {
            try { await target.RedirectActivationToAsync(activation); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally
            {
                try { completed.Set(); }
                catch (ObjectDisposedException) { /* The bounded wait expired and this process is exiting. */ }
            }
        });
        // Unlike Task.Wait on the STA, this wait pumps COM calls needed by redirection.
        var result = CoWaitForMultipleObjects(0, 30000, 1, [completed.SafeWaitHandle.DangerousGetHandle()], out _);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        failure?.Throw();
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint milliseconds, uint handleCount, IntPtr[] handles, out uint index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
