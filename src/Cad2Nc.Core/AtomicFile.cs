using System.Text;

namespace Cad2Nc.Core;

/// <summary>Writes a complete file beside the destination before replacing it.</summary>
public static class AtomicFile
{
    public static string WriteText(string path, string text, Encoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, encoding, 4096, leaveOpen: true))
                {
                    writer.Write(text);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch
        {
            // Failure must preserve the old destination; remove only our own temporary file.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
