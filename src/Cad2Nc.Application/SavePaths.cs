namespace Cad2Nc.Application;

/// <summary>Builds save destinations without creating directories or changing files.</summary>
public static class SavePaths
{
    public const string TaskExtension = ".cad2nc.json";
    public const string NcExtension = ".nc";

    public static string TaskFileName(string sourcePath) => SourceName(sourcePath) + TaskExtension;
    public static string NcFileName(string sourcePath) => SourceName(sourcePath) + NcExtension;

    public static string TaskPath(string sourcePath, string directory, string? currentTaskPath = null) =>
        currentTaskPath is null
            ? Path.Combine(RequiredDirectory(directory), TaskFileName(sourcePath))
            : NormalizeTaskPath(currentTaskPath);

    public static string NcPath(string sourcePath, string directory) =>
        Path.Combine(RequiredDirectory(directory), NcFileName(sourcePath));

    public static string NormalizeTaskPath(string path)
    {
        path = FilePath(path);
        if (path.EndsWith(TaskExtension, StringComparison.OrdinalIgnoreCase)) return path;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^5];
        if (path.EndsWith(".cad2nc", StringComparison.OrdinalIgnoreCase)) path = path[..^7];
        RequireFileName(path);
        return path + TaskExtension;
    }

    public static string NormalizeNcPath(string path)
    {
        path = FilePath(path);
        return path.EndsWith(NcExtension, StringComparison.OrdinalIgnoreCase) ? path : path + NcExtension;
    }

    internal static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("保存目录须为完整路径。", nameof(directory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private static string RequiredDirectory(string directory) =>
        NormalizeDirectory(directory) ?? throw new ArgumentException("请选择保存目录。", nameof(directory));

    private static string SourceName(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("图纸文件名不能为空。", nameof(sourcePath));
        return name;
    }

    private static string FilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RequireFileName(path);
        return Path.GetFullPath(path);
    }

    private static void RequireFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(Path.GetFileName(path))) throw new ArgumentException("保存文件名不能为空。", nameof(path));
    }
}
