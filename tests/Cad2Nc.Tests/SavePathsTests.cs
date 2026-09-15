using Cad2Nc.Application;

namespace Cad2Nc.Tests;

public sealed class SavePathsTests
{
    private static string At(string name) => Path.Combine(Path.GetTempPath(), "cad2nc-path-tests", name);

    [Theory]
    [InlineData("异型板", "异型板.cad2nc.json")]
    [InlineData("异型板.json", "异型板.cad2nc.json")]
    [InlineData("异型板.cad2nc", "异型板.cad2nc.json")]
    [InlineData("异型板.cad2nc.json", "异型板.cad2nc.json")]
    [InlineData("异型板.CAD2NC.JSON", "异型板.CAD2NC.JSON")]
    [InlineData("异型板.v2.json", "异型板.v2.cad2nc.json")]
    [InlineData("异型板.v2", "异型板.v2.cad2nc.json")]
    public void TaskSuffixIsNormalizedBeforeOverwriteDecisions(string selected, string expected)
    {
        var path = SavePaths.NormalizeTaskPath(At(selected));
        Assert.Equal(At(expected), path);
        Assert.Equal(path, SavePaths.NormalizeTaskPath(path));
    }

    [Theory]
    [InlineData("异型板", "异型板.nc")]
    [InlineData("异型板.NC", "异型板.NC")]
    [InlineData("异型板.v2", "异型板.v2.nc")]
    public void NcSuffixPreservesTheChosenBasename(string selected, string expected) =>
        Assert.Equal(At(expected), SavePaths.NormalizeNcPath(At(selected)));

    [Fact]
    public void NewSavesUseConfiguredDirectoriesAndExistingTasksKeepTheirOwnPath()
    {
        var source = At("原始图纸.v2.dwg");
        var taskDirectory = At("任务");
        var ncDirectory = At("NC");
        Assert.Equal("原始图纸.v2.cad2nc.json", SavePaths.TaskFileName(source));
        Assert.Equal("原始图纸.v2.nc", SavePaths.NcFileName(source));
        Assert.Equal(Path.Combine(taskDirectory, "原始图纸.v2.cad2nc.json"), SavePaths.TaskPath(source, taskDirectory));
        Assert.Equal(Path.Combine(ncDirectory, "原始图纸.v2.nc"), SavePaths.NcPath(source, ncDirectory));
        var existingTask = At(Path.Combine("另外位置", "自定义名称.cad2nc.json"));
        Assert.Equal(existingTask, SavePaths.TaskPath(source, taskDirectory, existingTask));
    }

    [Fact]
    public void MissingFilenameAndRelativeDefaultDirectoryAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SavePaths.NormalizeTaskPath(At("") + Path.DirectorySeparatorChar));
        Assert.Throws<ArgumentException>(() => SavePaths.NormalizeTaskPath(At(".json")));
        Assert.Throws<ArgumentException>(() => SavePaths.TaskPath(At("板.dwg"), "relative"));
    }
}
