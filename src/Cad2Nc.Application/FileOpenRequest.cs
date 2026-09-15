using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cad2Nc.Application;

public enum FileOpenKind { Cad, Task, Nc }

public sealed record FileOpenRequest(string? Path, FileOpenKind Kind = FileOpenKind.Cad, string? Error = null)
{
    public FileOpenRequest(string? Path, bool IsTask, string? Error = null)
        : this(Path, IsTask ? FileOpenKind.Task : FileOpenKind.Cad, Error) { }
    public bool IsTask => Kind == FileOpenKind.Task;
    public bool IsNc => Kind == FileOpenKind.Nc;
    public bool IsValid => Error is null;
    public bool IsActivationOnly => Path is null && Error is null;

    public static FileOpenRequest FromPaths(IReadOnlyList<string> paths, bool allowTask = false)
    {
        if (paths.Count == 0) return new(Path: null);
        if (paths.Count != 1) return new(null, Error: "一次只能打开一个文件，请只选择一份图纸、加工任务或 NC 程序。");
        try
        {
            var path = System.IO.Path.GetFullPath(paths[0]);
            var extension = System.IO.Path.GetExtension(path);
            var isCad = extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase);
            var isTask = allowTask && extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
            var isNc = extension.Equals(".nc", StringComparison.OrdinalIgnoreCase);
            if (!isCad && !isTask && !isNc) return new(null, Error: allowTask ? "请选择 DWG、DXF、NC 或加工任务 JSON 文件。" : "仅支持拖入 DWG、DXF 或 NC 文件。");
            if (!File.Exists(path)) return new(null, Error: "文件不存在、无法访问，或拖入的是文件夹。请重新选择文件。");
            return new(path, isTask ? FileOpenKind.Task : isNc ? FileOpenKind.Nc : FileOpenKind.Cad);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return new(null, Error: "无法读取文件路径，请重新选择文件。");
        }
    }
}

public enum OpenRequestAdmission { Start, Pending, Duplicate, Rejected }

/// <summary>Reserves the entire confirmation/picker/import flow, including early activations.</summary>
public sealed class FileOpenGate
{
    private readonly object sync = new();
    private bool ready, busy, earlyRequestRejected;
    private FileOpenRequest? pending;

    public bool IsBusy { get { lock (sync) return busy; } }

    public OpenRequestAdmission Admit(FileOpenRequest request)
    {
        if (!request.IsValid || request.IsActivationOnly) throw new ArgumentException("A valid file request is required.", nameof(request));
        lock (sync)
        {
            if (!ready)
            {
                if (pending is null) { pending = request; return OpenRequestAdmission.Pending; }
                if (string.Equals(pending.Path, request.Path, StringComparison.OrdinalIgnoreCase)) return OpenRequestAdmission.Duplicate;
                earlyRequestRejected = true;
                return OpenRequestAdmission.Rejected;
            }
            if (busy) return OpenRequestAdmission.Rejected;
            busy = true;
            return OpenRequestAdmission.Start;
        }
    }

    public FileOpenRequest? MarkReady(out bool rejected)
    {
        lock (sync)
        {
            ready = true;
            rejected = earlyRequestRejected;
            earlyRequestRejected = false;
            var request = pending;
            pending = null;
            if (request is not null) busy = true;
            return request;
        }
    }

    public bool TryBegin()
    {
        lock (sync)
        {
            if (!ready || busy) return false;
            busy = true;
            return true;
        }
    }

    public void Complete() { lock (sync) busy = false; }
}

public static class WindowsLaunchArguments
{
    // AppLifecycle's unpackaged Launch payload includes argv[0], unlike Main(args).
    [SupportedOSPlatform("windows")]
    public static string[] Parse(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
                result[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? "";
            return result;
        }
        finally { _ = LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
