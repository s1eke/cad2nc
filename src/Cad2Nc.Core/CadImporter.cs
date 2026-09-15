using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ACadSharp.IO;

namespace Cad2Nc.Core;

/// <summary>Reads DWG and DXF directly with ACadSharp; no converter or Python runtime is required.</summary>
public sealed class CadImporter : ICadImporter
{
    private readonly ImportOptions options;
    public CadImporter(ImportOptions? options = null)
    {
        this.options = options ?? new(); this.options.Validate();
    }

    public Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(path, cancellationToken), cancellationToken);

    public CadImportResult Load(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".dwg" or ".dxf")) throw new CadImportException("Unsupported CAD format; expected .dwg or .dxf.");
        try
        {
            // Keep one read-only handle while hashing and decoding: a replacement cannot silently
            // give the document a fingerprint for different source contents.
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var maximum = extension == ".dwg" ? options.Limits.MaxDwgFileBytes : options.Limits.MaxDxfFileBytes;
            if (stream.Length > maximum) throw new CadImportException($"{extension[1..].ToUpperInvariant()} file is {stream.Length} bytes; maximum is {maximum} bytes.");
            if (stream.Length == 0) throw new CadImportException("CAD file is empty.");
            var hash = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();
            var messages = new Dictionary<(string Code, string Text, DiagnosticSeverity Severity, bool Confirm), int>();
            var diagnosticBytes = 0L; var truncated = 0;
            void OnNotification(object? sender, NotificationEventArgs e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (e.NotificationType == NotificationType.Error)
                    throw new CadImportException("CAD reader reported an error; partial geometry is not accepted: " + e.Message, e.Exception);
                var message = e.Message ?? "Unspecified CAD reader notification.";
                if (diagnosticBytes + Encoding.UTF8.GetByteCount(message) > options.Limits.MaxDiagnosticBytes) { truncated++; return; }
                diagnosticBytes += Encoding.UTF8.GetByteCount(message);
                // These messages describe ordering tables and retained non-graphical objects,
                // not missing model-space entities. All other reader omissions stay reviewable.
                var referenceOnly = message.Contains("in SortEntitiesTable", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Unknown non-graphical", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Unknown non graphical", StringComparison.OrdinalIgnoreCase);
                var code = referenceOnly ? "reader.reference" : "reader.unsupported";
                var severity = referenceOnly ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning;
                var text = Regex.Replace(message, @"(?<=handle )[0-9A-Fa-f]+|(?<=handle: )[0-9A-Fa-f]+|(?<=\|)[0-9A-Fa-f]+", "…", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                var key = (code, text, severity, !referenceOnly);
                messages[key] = messages.GetValueOrDefault(key) + 1;
            }
            ACadSharp.CadDocument drawing;
            if (extension == ".dwg")
            {
                using var reader = new DwgReader(stream, OnNotification);
                reader.Configuration.Failsafe = false;
                reader.Configuration.KeepUnknownEntities = true;
                reader.Configuration.KeepUnknownNonGraphicalObjects = true;
                drawing = reader.Read();
            }
            else
            {
                using var reader = new DxfReader(stream, OnNotification);
                reader.Configuration.Failsafe = false;
                reader.Configuration.KeepUnknownEntities = true;
                reader.Configuration.KeepUnknownNonGraphicalObjects = true;
                drawing = reader.Read();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = messages.Select(p => new Diagnostic(p.Key.Code, p.Key.Text, p.Key.Severity, RequiresConfirmation: p.Key.Confirm, Count: p.Value)).ToList();
            if (truncated > 0) diagnostics.Add(new("reader.truncated", "CAD 读取诊断超过安全长度，部分记录已省略，请对照原图确认。", RequiresConfirmation: true, Count: truncated));
            return new CadGeometryExtractor(options).Extract(drawing, fullPath, hash, diagnostics, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (CadImportException) { throw; }
        catch (Exception ex) { throw new CadImportException($"Could not read CAD file '{Path.GetFileName(fullPath)}': {ex.Message}", ex); }
    }
}
