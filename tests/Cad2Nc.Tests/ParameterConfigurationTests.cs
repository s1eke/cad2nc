using System.Text.Json.Nodes;
using Cad2Nc.Application;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class ParameterConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-configuration-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(directory, "工艺参数" + ParameterConfigurationStorage.Extension);
    private static ConversionConfig Custom => new()
    {
        Tool = new() { ToolNumber = 4, LengthOffset = 12, ToolType = "8 mm 双刃刀", Diameter = 8, SpindleDirection = SpindleDirection.Counterclockwise },
        Machining = new() { SpindleSpeed = 12000, CuttingFeed = 7000, PlungeFeed = 900, CuttingDepth = -8.5,
            SafeZ = 12, MaxStepDown = 2.5, EdgeAllowance = 0.4, CutDirection = CutDirection.Clockwise }
    };

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void RoundTripPreservesEveryParameterIncludingIndependentToolAndOffset()
    {
        var saved = ParameterConfigurationStorage.Save(Path.Combine(directory, "工艺参数"), "  专用刀具  ", Custom);
        Assert.Equal(ConfigPath, saved);
        var read = ParameterConfigurationStorage.Read(saved);
        Assert.Equal("专用刀具", read.Name);
        Assert.Equal(Custom, read.Config);
        Assert.False(read.IsBuiltIn);
        Assert.Equal(4, read.Config.Tool.ToolNumber);
        Assert.Equal(12, read.Config.Tool.LengthOffset);
    }

    [Fact]
    public void ExportContainsOnlyVersionNameAndCompletePortableParameters()
    {
        ParameterConfigurationStorage.Save(ConfigPath, "刀具参数", Custom);
        var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal(["schemaVersion", "name", "originMode", "machine", "tool", "machining"], json.Select(p => p.Key));
        Assert.Equal(["toolNumber", "lengthOffset", "toolType", "diameter", "spindleDirection"], json["tool"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["spindleSpeed", "cuttingFeed", "cuttingDepth", "cutDirection", "edgeAllowance", "safeZ", "plungeFeed", "maxStepDown", "boardThickness", "leaveSkin", "zReference", "layeredCutting", "prioritizePartStability", "entryMode", "rampLength", "rampFeed"],
            json["machining"]!.AsObject().Select(p => p.Key));
        Assert.Equal(3, json["schemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("工艺参数")]
    [InlineData("工艺参数.json")]
    [InlineData("工艺参数.cad2nc")]
    [InlineData("工艺参数.config.json")]
    [InlineData("工艺参数.cad2nc.json")]
    [InlineData("工艺参数.cad2nc.config")]
    [InlineData("工艺参数.cad2nc.config.json")]
    public void FileSuffixIsNormalizedOnce(string fileName)
    {
        Assert.Equal(ConfigPath, ParameterConfigurationStorage.NormalizePath(Path.Combine(directory, fileName)));
        Assert.False(Directory.Exists(directory));
    }

    public static IEnumerable<object[]> RequiredFields()
    {
        foreach (var name in new[] { "schemaVersion", "name", "tool", "machining", "machine", "originMode" }) yield return ["", name];
        foreach (var name in new[] { "toolNumber", "lengthOffset", "toolType", "diameter", "spindleDirection" }) yield return ["tool", name];
        foreach (var name in new[] { "spindleSpeed", "cuttingFeed", "cuttingDepth", "cutDirection", "edgeAllowance", "safeZ", "plungeFeed", "maxStepDown", "boardThickness", "zReference", "layeredCutting", "prioritizePartStability", "entryMode", "rampLength", "rampFeed" }) yield return ["machining", name];
        foreach (var name in new[] { "profile", "toolChange", "useProgramEnvelope", "parkAtEnd", "parkX", "parkY", "parkZ", "emitM17" }) yield return ["machine", name];
    }

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public void MissingRequiredFieldIsNeverFilledFromRecordDefaults(string group, string name)
    {
        ParameterConfigurationStorage.Save(ConfigPath, "完整配置", Custom);
        var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        (group.Length == 0 ? json : json[group]!.AsObject()).Remove(name);
        File.WriteAllText(ConfigPath, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
    }

    [Theory]
    [InlineData("", "schemaVersion", "4")]
    [InlineData("", "name", "null")]
    [InlineData("", "name", "\" \"")]
    [InlineData("", "tool", "null")]
    [InlineData("", "machining", "[]")]
    [InlineData("tool", "toolNumber", "0")]
    [InlineData("tool", "lengthOffset", "1.5")]
    [InlineData("tool", "toolType", "\"\"")]
    [InlineData("tool", "diameter", "-1")]
    [InlineData("tool", "diameter", "\"6\"")]
    [InlineData("tool", "spindleDirection", "\"0\"")]
    [InlineData("tool", "spindleDirection", "0")]
    [InlineData("tool", "spindleDirection", "\"Invalid\"")]
    [InlineData("machining", "cuttingDepth", "0.1")]
    [InlineData("machining", "cuttingDepth", "-0.0001")]
    [InlineData("machining", "safeZ", "0")]
    [InlineData("machining", "maxStepDown", "0")]
    [InlineData("machining", "spindleSpeed", "0")]
    [InlineData("machining", "cuttingFeed", "0")]
    [InlineData("machining", "plungeFeed", "-1")]
    [InlineData("machining", "edgeAllowance", "-1")]
    [InlineData("machining", "cutDirection", "\"Invalid\"")]
    public void InvalidValuesDoNotMutateSourceOrAnyStore(string group, string field, string value)
    {
        ParameterConfigurationStorage.Save(ConfigPath, "完整配置", Custom);
        var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        (group.Length == 0 ? json : json[group]!.AsObject())[field] = JsonNode.Parse(value);
        var original = json.ToJsonString();
        File.WriteAllText(ConfigPath, original);
        var store = new UserStore(Path.Combine(directory, "settings"));
        var presets = store.Presets.ToArray();
        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Equal(presets, store.Presets.ToArray());
        Assert.Equal(new ConversionConfig(), store.LastConfig);
        Assert.False(Directory.Exists(Path.Combine(directory, "settings")));
    }

    [Fact]
    public void DuplicateAndUnknownPropertiesAreRejected()
    {
        ParameterConfigurationStorage.Save(ConfigPath, "配置", Custom);
        var original = File.ReadAllText(ConfigPath);
        File.WriteAllText(ConfigPath, original.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 3, \"SchemaVersion\": 3"));
        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
        var json = JsonNode.Parse(original)!.AsObject();
        json["sourcePath"] = "private.dwg";
        File.WriteAllText(ConfigPath, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));

        var v2 = JsonNode.Parse(original)!.AsObject();
        v2["schemaVersion"] = 2;
        v2["machining"]!.AsObject().Remove("prioritizePartStability");
        v2["machining"]!["prioritizePartStability"] = false;
        File.WriteAllText(ConfigPath, v2.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
    }

    [Fact]
    public void PreviousSchemaIsRejected()
    {
        ParameterConfigurationStorage.Save(ConfigPath, "v2", Custom);
        var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        json["schemaVersion"] = 2;
        json["machining"]!.AsObject().Remove("prioritizePartStability");
        File.WriteAllText(ConfigPath, json.ToJsonString());

        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("type")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public void VersionThreeStabilityFieldIsStrictlyValidated(string mutation)
    {
        ParameterConfigurationStorage.Save(ConfigPath, "v3", Custom);
        var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        var machining = json["machining"]!.AsObject();
        switch (mutation)
        {
            case "remove": machining.Remove("prioritizePartStability"); break;
            case "type": machining["prioritizePartStability"] = "true"; break;
            case "unknown": machining["stabilityOrder"] = true; break;
            case "duplicate":
                var duplicate = File.ReadAllText(ConfigPath).Replace("\"prioritizePartStability\": true", "\"prioritizePartStability\": true, \"PrioritizePartStability\": false", StringComparison.Ordinal);
                File.WriteAllText(ConfigPath, duplicate);
                Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
                return;
        }
        File.WriteAllText(ConfigPath, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ParameterConfigurationStorage.Read(ConfigPath));
    }

    [Fact]
    public void InvalidExportPreservesDestination()
    {
        ParameterConfigurationStorage.Save(ConfigPath, "原配置", Custom);
        var original = File.ReadAllText(ConfigPath);
        Assert.Throws<ArgumentException>(() => ParameterConfigurationStorage.Save(ConfigPath, "新配置", Custom with { Tool = Custom.Tool with { Diameter = 0 } }));
        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void LockedExportPreservesOriginalAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        ParameterConfigurationStorage.Save(ConfigPath, "原配置", Custom);
        var original = File.ReadAllText(ConfigPath);
        using (var locked = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => ParameterConfigurationStorage.Save(ConfigPath, "新配置", new()));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(original, File.ReadAllText(ConfigPath));
        }
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void PreviewContainsEveryValueAndPreciselyIdentifiesChangedOffset()
    {
        var current = new ConversionConfig();
        var candidate = current with { Tool = current.Tool with { LengthOffset = 8 } };
        var rows = ParameterConfigurationStorage.Compare(current, candidate);
        Assert.Equal(30, rows.Count);
        var change = Assert.Single(rows, p => p.IsChanged);
        Assert.Equal(nameof(ToolConfig.LengthOffset), change.Key);
        Assert.Equal("2", change.CurrentValue);
        Assert.Equal("8", change.ImportedValue);
        Assert.Contains(rows, p => p.Key == nameof(MachiningConfig.CuttingDepth) && p.ImportedValue == "-0.1 mm");
    }
}
