using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cad2Nc.Core;

namespace Cad2Nc.Application;

public static class CommandLine
{
    public static bool IsRequested(string[] args) => args.Any(a => a is "--convert" or "--help" or "-h");
    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            await output.WriteLineAsync("CAD2NC --convert <DWG/DXF> --output <NC> [--summary <JSON>] [--config <CONFIG_JSON> | --profile lnc|legacy] [--origin sheet|manual|toolpath] [--origin-x <X> --origin-y <Y>] [--sheet-contour <ID>] [--include-outer] [--split-boards] [--assume-mm] [--accept-import-diagnostics]\nDefault profile: LNC router, board-bottom Z0, 17 mm board, unique rectangular sheet origin required. Leave-skin cutting defaults to 0 mm (one final-depth pass); a positive leaveSkin value generates one leave-skin pass followed by the final pass. --split-boards splits multiple independent motherboard rectangles into one NC per board; with this switch --output is the destination directory. --config and --profile are mutually exclusive. Manual coordinates require --origin manual.\n--accept-converter-diagnostics remains an alias for --accept-import-diagnostics.");
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var values = new Dictionary<string, string>(); var switches = new HashSet<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (key is "--include-outer" or "--split-boards" or "--assume-mm" or "--accept-import-diagnostics" or "--accept-converter-diagnostics") switches.Add(key);
                else if (key is "--convert" or "--output" or "--summary" or "--config" or "--profile" or "--origin" or "--origin-x" or "--origin-y" or "--sheet-contour")
                {
                    if (++i >= args.Length || args[i].StartsWith("--") || !values.TryAdd(key, args[i])) throw new ArgumentException($"Missing or duplicate value: {key}");
                }
                else throw new ArgumentException($"Unknown argument: {key}");
            }
            if (!values.ContainsKey("--convert") || !values.ContainsKey("--output")) throw new ArgumentException("--convert and --output are required");
            if (values.ContainsKey("--config") && values.ContainsKey("--profile")) throw new ArgumentException("--config and --profile are mutually exclusive");
            var config = values.TryGetValue("--config", out var configPath) ? ParameterConfigurationStorage.Read(configPath).Config
                : values.GetValueOrDefault("--profile", "lnc").ToLowerInvariant() switch
                {
                    "lnc" => ConfigurationDefaults.Lnc(), "legacy" => ConfigurationDefaults.Legacy(),
                    _ => throw new ArgumentException("--profile must be lnc or legacy")
                };
            if (values.TryGetValue("--origin", out var originMode)) config = config with
            {
                OriginMode = originMode.ToLowerInvariant() switch
                {
                    "sheet" => OriginMode.SheetLowerLeft, "manual" => OriginMode.Manual, "toolpath" => OriginMode.ToolpathMinimum,
                    _ => throw new ArgumentException("--origin must be sheet, manual or toolpath")
                }
            };
            WorkpieceSetup workpiece = new();
            if (values.ContainsKey("--origin-x") || values.ContainsKey("--origin-y"))
            {
                if (config.OriginMode != OriginMode.Manual || !values.ContainsKey("--origin-x") || !values.ContainsKey("--origin-y"))
                    throw new ArgumentException("Manual coordinates require --origin manual and both --origin-x and --origin-y");
                double Coordinate(string key) => double.TryParse(values[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? value : throw new ArgumentException($"{key} must be a finite CAD coordinate");
                workpiece = workpiece with { ManualOrigin = new(Coordinate("--origin-x"), Coordinate("--origin-y")) };
            }
            if (switches.Contains("--split-boards") && config.OriginMode == OriginMode.Manual)
                throw new ArgumentException("--split-boards requires --origin sheet or --origin toolpath");
            if (values.TryGetValue("--sheet-contour", out var sheetId))
            {
                if (switches.Contains("--split-boards")) throw new ArgumentException("--sheet-contour cannot be combined with --split-boards");
                if (config.OriginMode != OriginMode.SheetLowerLeft) throw new ArgumentException("--sheet-contour requires --origin sheet");
                workpiece = workpiece with { SheetContourId = sheetId };
            }
            config.Validate();
            var source = Path.GetFullPath(values["--convert"]); var target = Path.GetFullPath(values["--output"]);
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(target).ToLowerInvariant() is ".dwg" or ".dxf") throw new ArgumentException("NC output must not overwrite source CAD");
            string? summary = null;
            if (values.TryGetValue("--summary", out var summaryPath))
            {
                summary = Path.GetFullPath(summaryPath);
                if (!summary.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || summary.Equals(source, StringComparison.OrdinalIgnoreCase) || summary.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Summary must be a separate .json file");
            }
            var result = await new CadImporter().LoadAsync(source);
            if (result.RequiresUnitsConfirmation && !switches.Contains("--assume-mm")) throw new InvalidOperationException("CAD units are unspecified; use --assume-mm only after verification");
            if (result.RequiresDiagnosticsConfirmation && !switches.Contains("--accept-import-diagnostics") && !switches.Contains("--accept-converter-diagnostics")) throw new InvalidOperationException("Import diagnostics require CAD comparison; use --accept-import-diagnostics after verification");
            var selection = result.Document.DefaultSelection;
            if (switches.Contains("--include-outer")) selection = selection.ToImmutableDictionary(p => p.Key, p => p.Value with { Included = true });
            if (config.OriginMode == OriginMode.SheetLowerLeft && !switches.Contains("--split-boards"))
            {
                var sheet = ToolpathPlanner.ResolveSheetContour(result.Document, workpiece);
                workpiece = workpiece with { SheetContourId = sheet.Id };
                selection = selection.SetItem(sheet.Id, selection[sheet.Id] with { Included = false });
            }
            object payload;
            if (switches.Contains("--split-boards"))
            {
                var batch = BatchNcGenerator.Generate(result.Document, config, selection,
                    Path.GetFileNameWithoutExtension(source));
                if (!batch.Succeeded) throw new InvalidOperationException(string.Join("；", batch.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)));
                var outputDirectory = Path.GetExtension(target).Equals(".nc", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(target)! : target;
                var outputs = BatchNcGenerator.Write(outputDirectory, batch);
                payload = new
                {
                    source, output_directory = Path.GetFullPath(outputDirectory), outputs,
                    programs = batch.Programs.Select((p, index) => new
                    {
                        sheet_contour = p.SheetContourId, output = outputs[index],
                        toolpaths = p.Plan.Toolpaths.Length, origin = new[] { p.Plan.Origin.X, p.Plan.Origin.Y }, stats = p.Plan.Stats,
                        leave_skin_mm = config.Machining.LeaveSkin,
                        prioritize_part_stability = config.Machining.PrioritizePartStability,
                        layered_cutting = config.Machining.LayeredCutting,
                        automatic_safety_layering = NcGenerator.UsesAutomaticSafetyLayering(config),
                        cut_order = config.Machining.PrioritizePartStability ? "depth-major" : "path-major",
                        depth_passes = NcGenerator.DepthPasses(config)
                    }),
                    contours = result.Document.Contours.Length,
                    machine_profile = config.Machine.Profile.ToString(), origin_mode = config.OriginMode.ToString(),
                    board_thickness_mm = config.Machining.BoardThickness, surface_z = config.SurfaceZ,
                    final_z = config.Machining.CuttingDepth, leave_skin_mm = config.Machining.LeaveSkin,
                    prioritize_part_stability = config.Machining.PrioritizePartStability,
                    layered_cutting = config.Machining.LayeredCutting,
                    automatic_safety_layering = NcGenerator.UsesAutomaticSafetyLayering(config),
                    depth_passes = NcGenerator.DepthPasses(config),
                    cut_order = config.Machining.PrioritizePartStability ? "depth-major" : "path-major",
                    warnings = batch.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error).Select(d => d.Message).Distinct(),
                    importer = "ACadSharp 3.7.1", application_version = typeof(CommandLine).Assembly.GetName().Version?.ToString(3)
                };
            }
            else
            {
                var request = new PlanningRequest(result.Document, config, selection, Workpiece: workpiece);
                var plan = ToolpathPlanner.Generate(request);
                NcPlaybackParser.ParseGenerated(NcGenerator.Generate(plan), config);
                NcGenerator.Write(target, plan);
                payload = new
                {
                    source, output = target, contours = result.Document.Contours.Length,
                    selected_contours = selection.Count(p => p.Value.Included),
                    cut_sides = selection.Where(p => p.Value.Included).ToDictionary(p => p.Key, p => p.Value.Side == CutSide.Inside ? "INSIDE" : "OUTSIDE"),
                    outer_candidates = result.Document.Contours.Where(c => c.IsOuterCandidate).Select(c => c.Id),
                    toolpaths = plan.Toolpaths.Length, origin = new[] { plan.Origin.X, plan.Origin.Y }, stats = plan.Stats,
                    machine_profile = config.Machine.Profile.ToString(), origin_mode = config.OriginMode.ToString(),
                    board_thickness_mm = config.Machining.BoardThickness, surface_z = config.SurfaceZ,
                    final_z = config.Machining.CuttingDepth, leave_skin_mm = config.Machining.LeaveSkin,
                    prioritize_part_stability = config.Machining.PrioritizePartStability,
                    layered_cutting = config.Machining.LayeredCutting,
                    automatic_safety_layering = NcGenerator.UsesAutomaticSafetyLayering(config),
                    depth_passes = NcGenerator.DepthPasses(config),
                    cut_order = config.Machining.PrioritizePartStability ? "depth-major" : "path-major",
                    warnings = plan.Diagnostics.Select(d => d.Message).Distinct(),
                    importer = "ACadSharp 3.7.1", application_version = typeof(CommandLine).Assembly.GetName().Version?.ToString(3)
                };
            }
            var json = JsonSerializer.Serialize(payload, TaskStorage.JsonOptions);
            if (summary is not null) AtomicFile.WriteText(summary, json + "\n", new UTF8Encoding(false));
            await output.WriteLineAsync(json); return 0;
        }
        catch (Exception e) { await error.WriteLineAsync($"CAD2NC conversion failed: {e.Message}"); return 1; }
    }

}
