using Cad2Nc.Core;

namespace Cad2Nc.Application;

public enum WorkbenchPhase
{
    Empty,
    Loading,
    NeedsInput,
    ReadyToGenerate,
    Generating,
    GenerationFailed,
    Stale,
    ReadyToSave,
    ExternalPlayable,
    ExternalTextOnly
}

public enum WorkbenchPrimaryCommand { None, Generate, Export }
public enum WorkbenchResultRoute { Summary, Diagnostics, ProgramParameters, Code }

public sealed record WorkbenchUiState(
    WorkbenchPhase Phase,
    string ResultText,
    string NextActionText,
    string GenerateLabel,
    WorkbenchPrimaryCommand PrimaryCommand,
    WorkbenchResultRoute ResultRoute,
    string DiagnosticBadgeText,
    bool HasDiagnostics,
    DiagnosticSeverity? HighestDiagnosticSeverity,
    int DiagnosticOccurrenceCount)
{
    public static WorkbenchUiState Create(
        WorkbenchSession session,
        int diagnosticGroupCount,
        int diagnosticOccurrenceCount,
        DiagnosticSeverity? highestDiagnosticSeverity)
    {
        var included = session.Contours.Count(contour => contour.IsIncluded);
        var errors = session.ValidationErrors.Count;
        var hasDiagnostics = diagnosticGroupCount > 0;
        var diagnosticText = DiagnosticText(diagnosticGroupCount, highestDiagnosticSeverity);
        var resultRoute = session.IsNcDocument
            ? WorkbenchResultRoute.Code
            : session.GenerationOutcome == GenerationOutcome.Failed || highestDiagnosticSeverity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error
                ? WorkbenchResultRoute.Diagnostics
                : WorkbenchResultRoute.Summary;

        if (session.IsNcDocument)
        {
            return session.CanPlay
                ? State(WorkbenchPhase.ExternalPlayable, "NC 可播放", "查看程序参数、源码或播放轨迹", "生成刀路", WorkbenchPrimaryCommand.None)
                : State(WorkbenchPhase.ExternalTextOnly, "NC 仅文本", "查看程序源码与解析提示", "生成刀路", WorkbenchPrimaryCommand.None);
        }

        if (session.IsBusy)
        {
            var generating = session.GenerationOutcome == GenerationOutcome.Running && session.Document is not null;
            return generating
                ? State(WorkbenchPhase.Generating, "正在生成刀路…", "可取消当前计算", "正在生成…", WorkbenchPrimaryCommand.None)
                : State(WorkbenchPhase.Loading, "正在读取文件…", "请稍候", "生成刀路", WorkbenchPrimaryCommand.None);
        }

        if (session.Document is null)
            return State(WorkbenchPhase.Empty, "等待文件", "打开或拖入 DWG、DXF、NC 文件", "生成刀路", WorkbenchPrimaryCommand.None);

        if (errors > 0)
            return State(WorkbenchPhase.NeedsInput, $"{errors} 项参数有误", "修正参数后生成刀路", session.Plan is null ? "生成刀路" : "更新刀路", WorkbenchPrimaryCommand.None);

        if (included == 0)
            return State(WorkbenchPhase.NeedsInput, "尚未选择轮廓", "勾选至少一个参与加工的轮廓", session.Plan is null ? "生成刀路" : "更新刀路", WorkbenchPrimaryCommand.None);

        if (session.GenerationOutcome == GenerationOutcome.Failed)
            return State(WorkbenchPhase.GenerationFailed, "生成失败", "查看诊断并修正相关轮廓或参数", "重新生成", WorkbenchPrimaryCommand.Generate);

        if (session.HasStalePlan)
            return State(WorkbenchPhase.Stale, "刀路需要更新", "加工内容已改变，请重新生成", "更新刀路", WorkbenchPrimaryCommand.Generate);

        if (session.GenerationOutcome is GenerationOutcome.Canceled or GenerationOutcome.Superseded)
            return State(WorkbenchPhase.ReadyToGenerate, "尚未生成刀路", "重新生成当前加工设置", "重新生成", WorkbenchPrimaryCommand.Generate);

        if (session.CanExport && session.IsBatchMode && session.SelectedBatchProgram is null)
            return State(WorkbenchPhase.ReadyToSave, "多母板刀路已就绪", "选择母板预览 NC，或导出全部母板", "重新生成", WorkbenchPrimaryCommand.Export);

        if (session.CanExport)
            return State(WorkbenchPhase.ReadyToSave, "刀路已就绪", "可预览、播放或保存 NC", "重新生成", WorkbenchPrimaryCommand.Export);

        if (session.Plan is not null && highestDiagnosticSeverity == DiagnosticSeverity.Error)
            return State(WorkbenchPhase.GenerationFailed, "结果不可保存", "查看诊断并修正问题", "重新生成", WorkbenchPrimaryCommand.Generate);

        return State(WorkbenchPhase.ReadyToGenerate, "加工设置就绪", "生成当前设置的刀路", "生成刀路", WorkbenchPrimaryCommand.Generate);

        WorkbenchUiState State(WorkbenchPhase phase, string result, string next, string generate,
            WorkbenchPrimaryCommand primary) => new(phase, result, next, generate, primary,
                resultRoute, diagnosticText, hasDiagnostics, highestDiagnosticSeverity, diagnosticOccurrenceCount);
    }

    private static string DiagnosticText(int groups, DiagnosticSeverity? severity)
    {
        if (groups <= 0) return "";
        var label = severity switch
        {
            DiagnosticSeverity.Error => "错误",
            DiagnosticSeverity.Warning => "提示",
            _ => "信息"
        };
        return groups == 1 ? $"1 条{label}" : $"{groups} 类{label}";
    }
}
