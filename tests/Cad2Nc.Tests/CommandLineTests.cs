using System.Text;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using Cad2Nc.Application;
using Cad2Nc.Core;
using DwgDocument = ACadSharp.CadDocument;

namespace Cad2Nc.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc CLI 回归 " + Guid.NewGuid().ToString("N"));
    private const string PreviousNc = "PREVIOUS VERIFIED NC\n";
    private const string PreviousSummary = "{\"previous\":true}\n";

    public CommandLineTests() => Directory.CreateDirectory(directory);

    private string FilePath(string name) => Path.Combine(directory, name);

    private string Drawing(UnitsType units = UnitsType.Millimeters, double elevation = 0)
    {
        var drawing = new DwgDocument();
        drawing.Header.InsUnits = units;
        var rectangle = new LwPolyline { IsClosed = true, Elevation = elevation };
        rectangle.Vertices.AddRange([new(10, 20), new(110, 20), new(110, 80), new(10, 80)]);
        drawing.Entities.Add(rectangle);
        var source = FilePath("中文 图纸.dxf");
        using var writer = new DxfWriter(source, drawing, false);
        writer.Write();
        return source;
    }

    private string BatchDrawing()
    {
        var drawing = new DwgDocument();
        drawing.Header.InsUnits = UnitsType.Millimeters;
        static LwPolyline Rectangle(double x, double y, double width, double height)
        {
            var rectangle = new LwPolyline { IsClosed = true };
            rectangle.Vertices.AddRange([new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)]);
            return rectangle;
        }
        foreach (var rectangle in new[]
        {
            Rectangle(0, 0, 100, 100), Rectangle(10, 10, 20, 20), Rectangle(50, 50, 20, 20),
            Rectangle(200, 0, 100, 100), Rectangle(210, 10, 20, 20), Rectangle(250, 50, 20, 20)
        }) drawing.Entities.Add(rectangle);
        var source = FilePath("多母板 图纸.dxf");
        using var writer = new DxfWriter(source, drawing, false);
        writer.Write();
        return source;
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        // Keep the established NC/coordinate golden assertions tied to the explicit legacy profile.
        var exitCode = await CommandLine.RunAsync([.. arguments, "--profile", "legacy"], output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task UnicodeAndSpacePathsProduceNormalizedNcAndMatchingSummary()
    {
        var source = Drawing();
        var sourceBytes = File.ReadAllBytes(source);
        var target = FilePath("加工 结果.nc");
        var summary = FilePath("加工 摘要.json");

        var result = await Run("--convert", source, "--output", target, "--summary", summary);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        var bytes = File.ReadAllBytes(target);
        Assert.All(bytes, value => Assert.InRange(value, (byte)0, (byte)127));
        var nc = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%\nO0001\n", nc);
        Assert.Contains("G21 G17 G90 G94\n", nc);
        Assert.Contains("G01 Z-0.100 F1000", nc);
        Assert.Contains("X104.000", nc);
        Assert.Contains("Y64.000", nc);
        Assert.EndsWith("M30\n%\n", nc);
        Assert.Equal(result.Output.TrimEnd('\r', '\n'), File.ReadAllText(summary).TrimEnd('\r', '\n'));
        using var json = JsonDocument.Parse(result.Output);
        var payload = json.RootElement;
        Assert.Equal(source, payload.GetProperty("source").GetString());
        Assert.Equal(target, payload.GetProperty("output").GetString());
        Assert.Equal(1, payload.GetProperty("contours").GetInt32());
        Assert.Equal(1, payload.GetProperty("selected_contours").GetInt32());
        Assert.Equal(1, payload.GetProperty("toolpaths").GetInt32());
        Assert.True(payload.GetProperty("prioritize_part_stability").GetBoolean());
        Assert.Equal("depth-major", payload.GetProperty("cut_order").GetString());
        Assert.False(payload.GetProperty("automatic_safety_layering").GetBoolean());
        Assert.True(payload.GetProperty("layered_cutting").GetBoolean());
        Assert.Equal(1, payload.GetProperty("depth_passes").GetArrayLength());
        Assert.Equal("OUTSIDE", payload.GetProperty("cut_sides").GetProperty("C0001").GetString());
        Assert.Empty(payload.GetProperty("outer_candidates").EnumerateArray());
        Assert.Equal(8, payload.GetProperty("origin")[0].GetDouble());
        Assert.Equal(18, payload.GetProperty("origin")[1].GetDouble());
        Assert.Equal(104, payload.GetProperty("stats").GetProperty("width_mm").GetDouble());
        Assert.Equal(64, payload.GetProperty("stats").GetProperty("height_mm").GetDouble());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task ExplicitlyDisabledStabilityUsesPathMajorOrderAndStillExportsWarning()
    {
        var source = Drawing();
        var target = FilePath("传统顺序.nc");
        var summary = FilePath("传统顺序.json");
        var configPath = FilePath("传统参数.cad2nc.config.json");
        var config = ConfigurationDefaults.Legacy() with
        {
            Machining = ConfigurationDefaults.Legacy().Machining with { PrioritizePartStability = false }
        };
        ParameterConfigurationStorage.Save(configPath, "传统顺序", config);

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CommandLine.RunAsync([
            "--convert", source, "--output", target, "--summary", summary,
            "--config", configPath
        ], output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.True(File.Exists(target));
        using var payload = JsonDocument.Parse(output.ToString());
        Assert.False(payload.RootElement.GetProperty("prioritize_part_stability").GetBoolean());
        Assert.Equal("path-major", payload.RootElement.GetProperty("cut_order").GetString());
        Assert.False(payload.RootElement.GetProperty("automatic_safety_layering").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("layered_cutting").GetBoolean());
        Assert.Contains(payload.RootElement.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("传统", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitBoardSummaryIncludesStabilityFieldsAtEveryLevel()
    {
        var source = BatchDrawing();
        var outputDirectory = FilePath("批量输出");
        var summary = FilePath("批量摘要.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CommandLine.RunAsync([
            "--convert", source, "--output", outputDirectory, "--summary", summary, "--split-boards"
        ], output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        using var payload = JsonDocument.Parse(output.ToString());
        var root = payload.RootElement;
        Assert.True(root.GetProperty("prioritize_part_stability").GetBoolean());
        Assert.Equal("depth-major", root.GetProperty("cut_order").GetString());
        Assert.False(root.GetProperty("automatic_safety_layering").GetBoolean());
        Assert.False(root.GetProperty("layered_cutting").GetBoolean());
        Assert.Equal(1, root.GetProperty("depth_passes").GetArrayLength());
        var programs = root.GetProperty("programs").EnumerateArray().ToArray();
        Assert.Equal(2, programs.Length);
        Assert.All(programs, program =>
        {
            Assert.True(program.GetProperty("prioritize_part_stability").GetBoolean());
            Assert.Equal("depth-major", program.GetProperty("cut_order").GetString());
            Assert.False(program.GetProperty("automatic_safety_layering").GetBoolean());
            Assert.False(program.GetProperty("layered_cutting").GetBoolean());
            Assert.Equal(1, program.GetProperty("depth_passes").GetArrayLength());
        });
    }

    [Fact]
    public async Task UnitlessDrawingRequiresItsOwnConfirmationBeforeTouchingOutputs()
    {
        var source = Drawing(UnitsType.Unitless);
        var target = FilePath("已有 刀路.nc");
        var summary = FilePath("已有 摘要.json");
        File.WriteAllText(target, PreviousNc);
        File.WriteAllText(summary, PreviousSummary);

        // Accepting import diagnostics does not imply an interpretation of units.
        var rejected = await Run("--convert", source, "--output", target, "--summary", summary,
            "--accept-import-diagnostics");
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("--assume-mm", rejected.Error);
        Assert.Empty(rejected.Output);
        Assert.Equal(PreviousNc, File.ReadAllText(target));
        Assert.Equal(PreviousSummary, File.ReadAllText(summary));

        var accepted = await Run("--convert", source, "--output", target, "--summary", summary, "--assume-mm");
        Assert.Equal(0, accepted.ExitCode);
        Assert.Empty(accepted.Error);
        Assert.StartsWith("%\nO0001\n", File.ReadAllText(target));
        using var payload = JsonDocument.Parse(File.ReadAllText(summary));
        Assert.Contains(payload.RootElement.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("单位", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--accept-import-diagnostics")]
    [InlineData("--accept-converter-diagnostics")]
    public async Task ImportReviewIsRequiredAndBothConfirmationSpellingsAcceptIt(string confirmation)
    {
        // A valid planar profile at Z=5 needs explicit acknowledgement before its XY projection is exported.
        var source = Drawing(elevation: 5);
        var target = FilePath("待核对 刀路.nc");
        var summary = FilePath("待核对 摘要.json");
        File.WriteAllText(target, PreviousNc);
        File.WriteAllText(summary, PreviousSummary);

        var rejected = await Run("--convert", source, "--output", target, "--summary", summary, "--assume-mm");
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("--accept-import-diagnostics", rejected.Error);
        Assert.Empty(rejected.Output);
        Assert.Equal(PreviousNc, File.ReadAllText(target));
        Assert.Equal(PreviousSummary, File.ReadAllText(summary));

        var accepted = await Run("--convert", source, "--output", target, "--summary", summary, confirmation);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Empty(accepted.Error);
        Assert.StartsWith("%\nO0001\n", File.ReadAllText(target));
        using var payload = JsonDocument.Parse(accepted.Output);
        Assert.Equal(1, payload.RootElement.GetProperty("toolpaths").GetInt32());
        Assert.NotEmpty(payload.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData("source")]
    [InlineData(".dwg")]
    [InlineData(".DXF")]
    public async Task NcDestinationCannotOverwriteTheSourceOrAnotherCadFile(string destination)
    {
        var source = Drawing();
        var sourceBytes = File.ReadAllBytes(source);
        var target = destination == "source" ? Path.Combine(directory, ".", Path.GetFileName(source)) : FilePath("其他 图纸" + destination);
        if (destination != "source") File.WriteAllText(target, "OTHER CAD DATA");
        var targetBytes = File.ReadAllBytes(target);
        var summary = FilePath("禁止输出.json");

        var result = await Run("--convert", source, "--output", target, "--summary", summary);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not overwrite source CAD", result.Error);
        Assert.Empty(result.Output);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(summary));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("same-output")]
    [InlineData(".dwg")]
    [InlineData(".dxf")]
    [InlineData(".nc")]
    [InlineData(".tap")]
    [InlineData(".gcode")]
    public async Task InvalidOrConflictingSummaryDestinationsPreserveAllExistingFiles(string destination)
    {
        var source = Drawing();
        var sourceBytes = File.ReadAllBytes(source);
        // A .json NC destination is allowed; a summary still cannot share its canonical path.
        var target = FilePath(destination == "same-output" ? "共有 输出.json" : "已有 刀路.nc");
        File.WriteAllText(target, PreviousNc);
        var summary = destination switch
        {
            "source" => source,
            "same-output" => Path.Combine(directory, ".", Path.GetFileName(target)),
            _ => FilePath("已有 摘要" + destination)
        };
        if (destination is not "source" and not "same-output") File.WriteAllText(summary, PreviousSummary);
        var summaryBytes = File.ReadAllBytes(summary);

        var result = await Run("--convert", source, "--output", target, "--summary", summary);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Summary must be a separate .json file", result.Error);
        Assert.Empty(result.Output);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Equal(PreviousNc, File.ReadAllText(target));
        Assert.Equal(summaryBytes, File.ReadAllBytes(summary));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
