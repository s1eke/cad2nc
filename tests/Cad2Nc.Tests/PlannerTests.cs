using System.Collections.Immutable;
using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class PlannerTests
{
    internal static Polygon Polygon(params (double X, double Y)[] points)
    {
        var coordinates = points.Select(p => new Coordinate(p.X, p.Y)).ToList();
        if (!coordinates[0].Equals2D(coordinates[^1])) coordinates.Add(coordinates[0].Copy());
        return new Polygon(new LinearRing(coordinates.ToArray()));
    }

    internal static Contour Rectangle(string id, double x, double y, double width, double height, bool sheet = false) =>
        new(id, "CUT", Polygon((x, y), (x + width, y), (x + width, y + height), (x, y + height)), sheet);

    internal static CadDocument Document(params Contour[] contours) => new("shape.dxf", "test-source", [.. contours], []);

    internal static PlanningRequest Request(CadDocument document, ConversionConfig? config = null,
        params (string Id, bool Included, CutSide Side)[] changes)
    {
        var selection = document.DefaultSelection.ToBuilder();
        foreach (var change in changes) selection[change.Id] = new(change.Included, change.Side);
        return new(document, config ?? new(), selection.ToImmutable());
    }

    [Fact]
    public void DefaultRouteUsesTwoStepOutwardOffsetAndOneSharedOrigin()
    {
        var document = Document(Rectangle("OUTER", 10, 10, 10, 10));
        var plan = ToolpathPlanner.Generate(Request(document));
        var route = Assert.Single(plan.Toolpaths);
        Assert.Equal(2, route.OffsetDistance);
        Assert.Equal(CutDirection.Counterclockwise, route.Direction);
        Assert.Equal(new Point2(8, 8), plan.Origin);
        Assert.Equal(new Bounds2(0, 0, 14, 14), plan.Bounds);
        Assert.Equal(route.Points[0], route.Points[^1]);
        Assert.Equal(0, route.Points.Min(p => p.X));
        Assert.Equal(0, route.Points.Min(p => p.Y));
        Assert.Equal(2, plan.Stats["net_offset_mm"]);
        Assert.Equal(route.Length, plan.Stats["total_length_mm"]);
        var finished = Assert.Single(plan.FinishedProfiles);
        Assert.Equal(new Bounds2(11, 11, 19, 19), Bounds2.FromPoints(finished.Points));
    }

    [Theory]
    [InlineData(CutDirection.Clockwise, false)]
    [InlineData(CutDirection.Counterclockwise, true)]
    public void RouteDirectionAndDeterministicStartAreCorrect(CutDirection direction, bool positiveArea)
    {
        var config = new ConversionConfig { Machining = new() { CutDirection = direction } };
        var plan = ToolpathPlanner.Generate(Request(Document(Rectangle("A", 10, 10, 20, 20)), config));
        var points = plan.Toolpaths[0].Points;
        var signedArea = points.Zip(points.Skip(1), (a, b) => a.X * b.Y - b.X * a.Y).Sum() / 2;
        Assert.Equal(positiveArea, signedArea > 0);
        Assert.Equal(points.OrderBy(p => p.X).ThenBy(p => p.Y).First(), points[0]);
    }

    [Fact]
    public void LargeCadCoordinatesKeepRelativePlacementAndNormalizeExactlyOnce()
    {
        var document = Document(Rectangle("A", 590000, 62000, 10, 10), Rectangle("B", 590100, 70000, 10, 10));
        var plan = ToolpathPlanner.Generate(Request(document));
        Assert.Equal(new Point2(589998, 61998), plan.Origin);
        Assert.Equal(new Bounds2(0, 0, 14, 14), plan.Toolpaths.Single(p => p.ContourId == "A").Bounds);
        Assert.Equal(new Bounds2(100, 8000, 114, 8014), plan.Toolpaths.Single(p => p.ContourId == "B").Bounds);
        var code = NcGenerator.Generate(plan);
        Assert.Contains("Y8000.000", code);
        Assert.DoesNotContain("X590", code);
    }

    [Fact]
    public void ExplicitSelectionCanIncludeMotherboard()
    {
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true));
        Assert.Empty(ToolpathPlanner.Generate(Request(document)).Toolpaths);
        Assert.Single(ToolpathPlanner.Generate(Request(document, null, ("STOCK", true, CutSide.Outside))).Toolpaths);
    }

    [Fact]
    public void ExplicitTrustedSheetHostsSelectedPartsWithoutOuterCandidateMarker()
    {
        // A task may explicitly identify a rectangular stock contour that an
        // importer did not mark as an outer candidate.  The trusted id must
        // still suppress the nested-outside clearance error when the stock
        // route itself is selected.
        var document = Document(
            Rectangle("SHEET", 0, 0, 100, 100),
            Rectangle("PART", 20, 20, 20, 20));
        var config = new ConversionConfig
        {
            OriginMode = OriginMode.ToolpathMinimum,
            Machining = new() { PrioritizePartStability = true }
        };
        var selection = document.DefaultSelection
            .SetItem("SHEET", new(true, CutSide.Outside))
            .SetItem("PART", new(true, CutSide.Outside));
        var plan = ToolpathPlanner.Generate(new(document, config, selection,
            Workpiece: new() { SheetContourId = "SHEET" }));

        Assert.Equal(new[] { "PART", "SHEET" }, plan.Toolpaths.Select(path => path.ContourId));
        Assert.DoesNotContain(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.outside_clearance");
        Assert.DoesNotContain(plan.Diagnostics, diagnostic => diagnostic.Code == "nested_outside");
    }

    [Fact]
    public void MissingSelectionEntriesUseDocumentDefaults()
    {
        var document = Document(Rectangle("A", 0, 0, 10, 10));
        var plan = ToolpathPlanner.Generate(new(document, new(), ImmutableDictionary<string, ContourSelection>.Empty));
        Assert.Single(plan.Toolpaths);
    }

    [Fact]
    public void UnknownSelectionCannotProducePartialOutput()
    {
        var request = Request(Document(Rectangle("A", 0, 0, 10, 10)), null, ("MISSING", true, CutSide.Outside));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(request));
        Assert.Contains("MISSING", error.Message);
    }

    [Fact]
    public void EmptySelectionReturnsAnEmptyPlanWithDiagnostic()
    {
        var document = Document(Rectangle("A", 0, 0, 10, 10));
        var plan = ToolpathPlanner.Generate(Request(document, null, ("A", false, CutSide.Outside)));
        Assert.Empty(plan.Toolpaths);
        Assert.Empty(plan.FinishedProfiles);
        Assert.Equal(default, plan.Origin);
        Assert.Equal(0, plan.Stats["total_length_mm"]);
        Assert.Contains(plan.Diagnostics, d => d.Code == "no_selection");
    }

    [Fact]
    public void SelectedContourBudgetIsCheckedBeforePairwisePlanning()
    {
        var contours = Enumerable.Range(0, 2001).Select(i => Rectangle($"C{i}", i * 20, 0, 10, 10)).ToArray();
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(Document(contours))));
        Assert.Contains("2000", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoleRunsBeforeHostAndKeepsItsRoleWhenSelectedAlone(bool includeHost)
    {
        var document = Document(Rectangle("OUTER", 0, 0, 100, 100), Rectangle("HOLE", 20, 20, 20, 20));
        var plan = ToolpathPlanner.Generate(Request(document, null,
            ("OUTER", includeHost, CutSide.Outside), ("HOLE", true, CutSide.Inside)));
        Assert.Equal("HOLE", plan.Toolpaths[0].ContourId);
        Assert.Equal(-2, plan.Toolpaths[0].OffsetDistance);
        Assert.Equal(includeHost ? 2 : 1, plan.Toolpaths.Length);
        if (includeHost) Assert.Equal(2, plan.Toolpaths[1].OffsetDistance);
        Assert.Equal(new Bounds2(19, 19, 41, 41), Bounds2.FromPoints(plan.FinishedProfiles[0].Points));
    }

    [Fact]
    public void AStandaloneInsideRingRequiresAHost()
    {
        var document = Document(Rectangle("HOLE", 0, 0, 20, 20));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(
            Request(document, null, ("HOLE", true, CutSide.Inside))));
        Assert.Contains("没有包含", error.Message);
    }

    [Fact]
    public void HoleCannotBreakThroughFinishedHost()
    {
        var document = Document(Rectangle("HOST", 0, 0, 20, 20), Rectangle("HOLE", 0.5, 5, 10, 10));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(
            Request(document, null, ("HOLE", true, CutSide.Inside))));
        Assert.Contains("穿透", error.Message);
    }

    [Fact]
    public void AllowanceCannotMergeHoles()
    {
        var document = Document(Rectangle("HOST", 0, 0, 30, 20), Rectangle("H1", 5, 5, 5, 5), Rectangle("H2", 11, 5, 5, 5));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(document, null,
            ("H1", true, CutSide.Inside), ("H2", true, CutSide.Inside))));
        Assert.Contains("重叠", error.Message);
    }

    [Fact]
    public void SelectedMotherboardAndNestedIndependentPartRemainOutsideProfiles()
    {
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true), Rectangle("PART", 20, 20, 20, 20));
        var plan = ToolpathPlanner.Generate(Request(document, null, ("STOCK", true, CutSide.Outside)));
        Assert.Equal(2, plan.Toolpaths.Length);
        Assert.All(plan.Toolpaths, p => Assert.Equal(2, p.OffsetDistance));
    }

    [Fact]
    public void NestedSelectedNonSheetProfileMustHaveAnExplicitHoleRole()
    {
        var document = Document(Rectangle("HOST", 0, 0, 100, 100), Rectangle("INNER", 20, 20, 20, 20));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(document)));
        Assert.Contains("内侧", error.Message);
    }

    [Fact]
    public void UnselectedHostPermitsAnIndependentPartButReportsAmbiguity()
    {
        var document = Document(Rectangle("HOST", 0, 0, 100, 100), Rectangle("INNER", 20, 20, 20, 20));
        var plan = ToolpathPlanner.Generate(Request(document, null, ("HOST", false, CutSide.Outside)));
        Assert.Single(plan.Toolpaths);
        var diagnostic = Assert.Single(plan.Diagnostics, d => d.Code == "nested_outside");
        Assert.Equal("INNER", diagnostic.ContourId); Assert.True(diagnostic.RequiresConfirmation);
        Assert.Equal("CutSide", diagnostic.RelatedField); Assert.Equal(new[] { "INNER", "HOST" }, diagnostic.ContourIds);
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(14, true)]
    public void OutsidePartsRequireAFullDiameterBetweenFinishedEdges(double secondX, bool succeeds)
    {
        var request = Request(Document(Rectangle("A", 0, 0, 10, 10), Rectangle("B", secondX, 0, 10, 10)));
        if (succeeds) Assert.Equal(2, ToolpathPlanner.Generate(request).Toolpaths.Length);
        else Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(request));
    }

    [Fact]
    public void DeselectedSiblingRemainsProtectedFromCutterSweep()
    {
        var document = Document(Rectangle("A", 0, 0, 20, 20), Rectangle("B", 21, 0, 20, 20));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(
            Request(document, null, ("B", false, CutSide.Outside))));
        Assert.Contains("未选轮廓 B", error.Message);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(18)]
    public void OutsideToolCannotBridgeNarrowOrShallowNotches(double notchX)
    {
        var contour = new Contour("NOTCH", "CUT", Polygon((0, 0), (20, 0), (20, 9), (notchX, 9),
            (notchX, 11), (20, 11), (20, 20), (0, 20)));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(Document(contour))));
        Assert.Contains("窄小特征", error.Message);
    }

    [Fact]
    public void ARecessWiderThanTheCutterRemainsMachinable()
    {
        var contour = new Contour("WIDE", "CUT", Polygon((0, 0), (20, 0), (20, 5), (5, 5),
            (5, 15), (20, 15), (20, 20), (0, 20)));
        Assert.Single(ToolpathPlanner.Generate(Request(Document(contour))).Toolpaths);
    }

    private static Contour CurvedU(double x, double y, int arcSegments, bool narrowNotch = false)
    {
        // A 457 mm semicircular opening with 50 mm side walls. Finer than 0.01 mm
        // chord error; buffer input simplification can falsely bridge these facets.
        var points = new List<(double X, double Y)> { (0, 0), (50, 0) };
        for (var index = 0; index <= arcSegments; index++)
        {
            var angle = Math.PI * (1 - (double)index / arcSegments);
            points.Add((278.5 + 228.5 * Math.Cos(angle), 971.5 + 228.5 * Math.Sin(angle)));
        }
        points.AddRange([(507, 0), (557, 0), (557, 1348)]);
        if (narrowNotch) points.AddRange([(279, 1348), (279, 1300), (277, 1300), (277, 1348)]);
        points.Add((0, 1348));
        return new("CURVED_U", "CUT", Polygon(points.Select(p => (p.X + x, p.Y + y)).ToArray()));
    }

    [Theory]
    [InlineData(0, 0, 200)]
    [InlineData(589668.064, 63562.642, 200)]
    [InlineData(589668.064, 63562.642, 286)]
    public void FineLargeRadiusRecessIsMachinableWithoutSimplifyingAwayItsFacets(double x, double y, int segments)
    {
        var plan = ToolpathPlanner.Generate(Request(Document(CurvedU(x, y, segments))));
        var route = Assert.Single(plan.Toolpaths);
        Assert.Equal(x - 2, plan.Origin.X, 6);
        Assert.Equal(y - 2, plan.Origin.Y, 6);
        Assert.Equal(561, route.Bounds.Width, 6);
        Assert.Equal(1352, route.Bounds.Height, 6);
        // The U opening survives in the cutter-centre route, including its apex.
        Assert.Contains(route.Points, point => Math.Abs(point.X - 280.5) < 0.02 && Math.Abs(point.Y - 1200) < 0.02);
        Assert.All(route.Points, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
    }

    [Fact]
    public void FineCurvedContourStillRejectsAnActualNarrowNotch()
    {
        var contour = CurvedU(589668.064, 63562.642, 200, narrowNotch: true);
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(Document(contour))));
        Assert.Contains("窄小特征", error.Message);
    }

    [Fact]
    public void InsideToolCannotEraseAHoleBranch()
    {
        var hole = new Contour("HOLE", "CUT", Polygon((10, 10), (20, 10), (20, 14), (35, 14),
            (35, 16), (20, 16), (20, 20), (10, 20)));
        var document = Document(Rectangle("HOST", 0, 0, 50, 50), hole);
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(document, null,
            ("HOST", false, CutSide.Outside), ("HOLE", true, CutSide.Inside))));
        Assert.Contains("窄小特征", error.Message);
    }

    [Fact]
    public void SealedRecessCannotBeHiddenAsAnInteriorRing()
    {
        var contour = new Contour("BOTTLE", "CUT", Polygon((0, 0), (30, 0), (30, 30), (16, 30),
            (16, 25), (22, 25), (22, 8), (8, 8), (8, 25), (14, 25), (14, 30), (0, 30)));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(Document(contour))));
    }

    [Fact]
    public void OneCollapsedContourBlocksTheWholePlan()
    {
        var document = Document(Rectangle("BIG", 0, 0, 20, 20), Rectangle("SMALL", 40, 0, 1, 1));
        var error = Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(document)));
        Assert.Contains("SMALL", error.Message);
        Assert.Contains("未生成部分 NC", error.Message);
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("plan.finished_profile", diagnostic.Code); Assert.Equal("EdgeAllowance", diagnostic.RelatedField);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity); Assert.Equal(new[] { "SMALL" }, diagnostic.ContourIds);
    }

    [Fact]
    public void InvalidConfigurationOrCutSideIsRejected()
    {
        var document = Document(Rectangle("A", 0, 0, 20, 20));
        Assert.Throws<ArgumentException>(() => ToolpathPlanner.Generate(Request(document,
            new() { Tool = new() { Diameter = double.NaN } })));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(Request(document, null,
            ("A", true, (CutSide)99))));
    }

    [Fact]
    public void CancellationDoesNotReturnAPartialPlan()
    {
        var token = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => ToolpathPlanner.Generate(
            Request(Document(Rectangle("A", 0, 0, 20, 20))), token));
    }

    [Fact]
    public void StabilityModeOrdersHolesThenPartsThenMotherboardCandidates()
    {
        var document = Document(
            Rectangle("STOCK", 0, 0, 200, 100, sheet: true),
            Rectangle("FRAME", 300, 0, 20, 20, sheet: true),
            Rectangle("PART", 30, 30, 40, 40),
            Rectangle("HOLE", 40, 40, 10, 10));
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };
        var plan = ToolpathPlanner.Generate(Request(document, config,
            ("STOCK", true, CutSide.Outside), ("FRAME", true, CutSide.Outside),
            ("PART", true, CutSide.Outside), ("HOLE", true, CutSide.Inside)));

        Assert.Equal(new[] { "HOLE", "PART", "FRAME", "STOCK" },
            plan.Toolpaths.Select(path => path.ContourId));
        var workholding = Assert.Single(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.workholding_unmodeled");
        Assert.Equal(DiagnosticSeverity.Warning, workholding.Severity);
        Assert.False(workholding.RequiresConfirmation);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
    }

    [Fact]
    public void StabilityModeUsesContourIdAsTheFinalEqualAreaTieBreaker()
    {
        var document = Document(
            Rectangle("PART-B", 100, 0, 20, 20),
            Rectangle("PART-A", 0, 0, 20, 20));
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };

        var plan = ToolpathPlanner.Generate(Request(document, config));

        Assert.Equal(new[] { "PART-A", "PART-B" },
            plan.Toolpaths.Select(path => path.ContourId));
    }

    [Fact]
    public void DisabledStabilityModeRetainsLegacyAreaOrderingAndWarns()
    {
        var document = Document(
            Rectangle("LARGE", 0, 0, 40, 40),
            Rectangle("OUTER_SMALL", 100, 0, 5, 5, sheet: true));
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = false } };
        var plan = ToolpathPlanner.Generate(Request(document, config, ("OUTER_SMALL", true, CutSide.Outside)));

        Assert.Equal(new[] { "OUTER_SMALL", "LARGE" },
            plan.Toolpaths.Select(path => path.ContourId));
        var warning = Assert.Single(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_order_disabled");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.RequiresConfirmation);
    }

    [Fact]
    public void StabilityModeUsesSheetDistanceToMoveEntryAwayFromNearEdge()
    {
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true),
            Rectangle("PART", 10, 40, 20, 20));
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };
        var plan = ToolpathPlanner.Generate(Request(document, config, ("STOCK", false, CutSide.Outside)));
        var route = Assert.Single(plan.Toolpaths);

        // The route starts on the side furthest from the stock's left edge;
        // the legacy left/lower corner would be near X=8 instead.
        Assert.True(route.Points[0].X > 20, $"unexpected stable entry: {route.Points[0]}");
        Assert.DoesNotContain(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
    }

    [Fact]
    public void SingleContourWithoutSheetKeepsLegacyEntryAndReportsFallback()
    {
        var document = Document(Rectangle("PART", 10, 10, 20, 20));
        var plan = ToolpathPlanner.Generate(Request(document));
        var route = Assert.Single(plan.Toolpaths);

        var legacy = ToolpathPlanner.Generate(Request(document,
            new ConversionConfig { Machining = new() { PrioritizePartStability = false } }));
        Assert.Equal(legacy.Toolpaths.Single().Points[0], route.Points[0]);
        var fallback = Assert.Single(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
        Assert.Equal("PART", fallback.ContourId);
        Assert.Equal(DiagnosticSeverity.Warning, fallback.Severity);
        Assert.False(fallback.RequiresConfirmation);
    }

    [Theory]
    [InlineData(CutDirection.Counterclockwise)]
    [InlineData(CutDirection.Clockwise)]
    public void StableEntryRotationPreservesDirectionAndLength(CutDirection direction)
    {
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true),
            Rectangle("A", 10, 10, 20, 20), Rectangle("B", 60, 60, 20, 20));
        var config = new ConversionConfig
        {
            Machining = new() { PrioritizePartStability = true, CutDirection = direction }
        };
        var request = Request(document, config, ("STOCK", false, CutSide.Outside));
        var stable = ToolpathPlanner.Generate(request);
        var legacy = ToolpathPlanner.Generate(request with
        {
            Config = config with { Machining = config.Machining with { PrioritizePartStability = false } }
        });
        var stableRoute = stable.Toolpaths.Single(path => path.ContourId == "A");
        var legacyRoute = legacy.Toolpaths.Single(path => path.ContourId == "A");

        Assert.Equal(legacyRoute.Length, stableRoute.Length, 6);
        Assert.Equal(SignedArea(legacyRoute.Points) > 0, SignedArea(stableRoute.Points) > 0);
        Assert.Equal(direction == CutDirection.Counterclockwise, SignedArea(stableRoute.Points) > 0);
        Assert.Equal(stableRoute.Points[0], stableRoute.Points[^1]);
    }

    [Fact]
    public void StableEntryIsIndependentOfInputCycleAndDirection()
    {
        var stock = Rectangle("STOCK", 0, 0, 100, 100, sheet: true);
        var forward = Polygon((10, 20), (40, 20), (40, 35), (10, 35));
        var cycled = Polygon((40, 20), (40, 35), (10, 35), (10, 20));
        var reversed = Polygon((10, 35), (40, 35), (40, 20), (10, 20));
        var first = ToolpathPlanner.Generate(Request(Document(stock, new("PART", "CUT", forward)),
            new ConversionConfig { Machining = new() { PrioritizePartStability = true } },
            ("STOCK", false, CutSide.Outside)));
        var second = ToolpathPlanner.Generate(Request(Document(stock, new("PART", "CUT", reversed)),
            new ConversionConfig { Machining = new() { PrioritizePartStability = true } },
            ("STOCK", false, CutSide.Outside)));
        var third = ToolpathPlanner.Generate(Request(Document(stock, new("PART", "CUT", cycled)),
            new ConversionConfig { Machining = new() { PrioritizePartStability = true } },
            ("STOCK", false, CutSide.Outside)));
        var firstPoints = first.Toolpaths.Single().Points;
        var secondPoints = second.Toolpaths.Single().Points;
        var thirdPoints = third.Toolpaths.Single().Points;
        Assert.Equal(firstPoints.ToArray(), secondPoints.ToArray());
        Assert.Equal(firstPoints.ToArray(), thirdPoints.ToArray());
    }

    [Fact]
    public void StableEntryUsesCadCoordinatesAcrossFractionalManualOrigins()
    {
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true),
            Rectangle("PART", 10, 40, 20, 20));
        var config = new ConversionConfig
        {
            OriginMode = OriginMode.Manual,
            Machining = new() { PrioritizePartStability = true }
        };
        var selection = document.DefaultSelection.SetItem("STOCK", new(false));

        ToolpathPlan Generate(Point2 manualOrigin) => ToolpathPlanner.Generate(new(
            document, config, selection,
            Workpiece: new() { SheetContourId = "STOCK", ManualOrigin = manualOrigin }));

        var first = Generate(new(0.1234, 0.5678));
        var second = Generate(new(0.9876, 1.2345));
        var firstRoute = Assert.Single(first.Toolpaths);
        var secondRoute = Assert.Single(second.Toolpaths);
        var firstCadStart = new Point2(firstRoute.Points[0].X + first.Origin.X,
            firstRoute.Points[0].Y + first.Origin.Y);
        var secondCadStart = new Point2(secondRoute.Points[0].X + second.Origin.X,
            secondRoute.Points[0].Y + second.Origin.Y);

        Assert.Equal(new Point2(0.1234, 0.5678), first.Origin);
        Assert.Equal(new Point2(0.9876, 1.2345), second.Origin);
        Assert.Equal(firstCadStart.X, secondCadStart.X, 6);
        Assert.Equal(firstCadStart.Y, secondCadStart.Y, 6);
    }

    [Fact]
    public void QuantizedMidpointThatLandsOnEndpointIsFiltered()
    {
        // The first source edge is exactly 0.001 mm long but its midpoint
        // rounds to the upper endpoint (10.001 mm). It must not be inserted as
        // a new entry point; the remaining route is still usable.
        var tinyEdge = new Contour("TINY", "CUT", Polygon(
            (10.0004, 10), (10.0014, 10), (30, 10), (30, 30), (10.0004, 30)));
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true), tinyEdge);
        var config = new ConversionConfig
        {
            Tool = new() { Diameter = .0001 },
            Machining = new() { EdgeAllowance = 0, PrioritizePartStability = true, EntryMode = EntryMode.Vertical }
        };
        var plan = ToolpathPlanner.Generate(Request(document, config, ("STOCK", false, CutSide.Outside)));
        var route = Assert.Single(plan.Toolpaths);
        var invalidMidpoint = new Point2((10.0004 + 10.0014) / 2, 10);
        Assert.NotEqual(invalidMidpoint, route.Points[0]);
        Assert.DoesNotContain(route.Points, point =>
            Math.Abs(point.X - invalidMidpoint.X) < 1e-12 && Math.Abs(point.Y - invalidMidpoint.Y) < 1e-12);
    }

    [Fact]
    public void StableEntryRejectsRoutesThatCollapseAfterManualOriginQuantization()
    {
        // Keep the physical profile large enough to plan, but retain a very
        // short collinear source segment.  With this fractional manual origin
        // the segment endpoints land in one NC bin; every rotated candidate
        // would therefore emit a zero-length move and must be rejected.
        var contour = new Contour("PART", "CUT", Polygon(
            (0, 0), (20, 0), (20, 20), (10.0012, 20), (10.0004, 20), (0, 20)));
        var document = Document(Rectangle("STOCK", -10, -10, 100, 100, sheet: true), contour);
        var config = new ConversionConfig
        {
            OriginMode = OriginMode.Manual,
            Tool = new() { Diameter = 2e-12 },
            Machining = new() { EdgeAllowance = 0, PrioritizePartStability = true }
        };
        var selection = document.DefaultSelection.SetItem("STOCK", new(false));
        var request = new PlanningRequest(document, config, selection,
            Workpiece: new() { SheetContourId = "STOCK", ManualOrigin = new(-0.0001, 0.0002) });

        var plan = ToolpathPlanner.Generate(request);
        var fallback = Assert.Single(plan.Diagnostics,
            diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
        Assert.Equal("PART", fallback.ContourId);
        Assert.Equal(DiagnosticSeverity.Warning, fallback.Severity);
        Assert.False(fallback.RequiresConfirmation);
        Assert.Contains("量化", fallback.Message);
        Assert.DoesNotContain(plan.Diagnostics,
            diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
        Assert.Equal(new Point2(-0.0001, 0.0002), plan.Origin);

        var absoluteQuantizationPlan = ToolpathPlanner.Generate(request with
        {
            Workpiece = new() { SheetContourId = "STOCK", ManualOrigin = new(0, 0) }
        });
        Assert.DoesNotContain(absoluteQuantizationPlan.Diagnostics,
            diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
    }

    [Fact]
    public void SignedSheetDistancePrefersNearestPointWhenRouteIsOutsideBoard()
    {
        var document = Document(Rectangle("SHEET", 0, 0, 100, 100, sheet: true),
            Rectangle("PART", 120, 40, 20, 20));
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };
        var request = new PlanningRequest(document, config,
            document.DefaultSelection.SetItem("SHEET", new(false)),
            Workpiece: new() { SheetContourId = "SHEET" });
        var plan = ToolpathPlanner.Generate(request);
        var route = Assert.Single(plan.Toolpaths);
        var cadStartX = route.Points[0].X + plan.Origin.X;

        // All candidate points are outside the sheet. Signed distance makes
        // the point nearest the right sheet edge win (rather than the farthest
        // point that an unsigned distance would select).
        Assert.InRange(cadStartX, 117.9, 125.0);
        Assert.DoesNotContain(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
    }

    [Fact]
    public void MissingSheetStillScoresAgainstOtherFinishedEdges()
    {
        var document = Document(Rectangle("A", 10, 10, 20, 20), Rectangle("B", 60, 10, 20, 20));
        var stableConfig = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };
        var stable = ToolpathPlanner.Generate(Request(document, stableConfig));
        var legacy = ToolpathPlanner.Generate(Request(document,
            stableConfig with { Machining = stableConfig.Machining with { PrioritizePartStability = false } }));

        Assert.Contains(stable.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
        Assert.DoesNotContain(stable.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
        Assert.NotEqual(legacy.Toolpaths.Single(path => path.ContourId == "B").Points[0],
            stable.Toolpaths.Single(path => path.ContourId == "B").Points[0]);
    }

    [Fact]
    public void AmbiguousSheetsWarnButOtherFinishedEdgesRemainUsable()
    {
        var document = Document(Rectangle("S1", 0, 0, 100, 100, sheet: true),
            Rectangle("S2", 200, 0, 100, 100, sheet: true),
            Rectangle("A", 10, 10, 20, 20), Rectangle("B", 60, 10, 20, 20));
        var changes = new[] { ("S1", false, CutSide.Outside), ("S2", false, CutSide.Outside) };
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };
        var plan = ToolpathPlanner.Generate(Request(document, config, changes));

        var warning = Assert.Single(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.RequiresConfirmation);
        Assert.DoesNotContain(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
    }

    [Fact]
    public void ExplicitSheetContourWinsAmongMultipleCandidates()
    {
        var document = Document(Rectangle("S1", 0, 0, 100, 100, sheet: true),
            Rectangle("S2", 200, 0, 100, 100, sheet: true), Rectangle("PART", 120, 40, 20, 20));
        var config = new ConversionConfig { Machining = new() { PrioritizePartStability = true } };
        var selection = document.DefaultSelection.SetItem("S1", new(false)).SetItem("S2", new(false));
        var first = ToolpathPlanner.Generate(new(document, config, selection,
            Workpiece: new() { SheetContourId = "S1" }));
        var second = ToolpathPlanner.Generate(new(document, config, selection,
            Workpiece: new() { SheetContourId = "S2" }));

        Assert.DoesNotContain(first.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
        Assert.DoesNotContain(second.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_sheet_unknown");
        Assert.NotEqual(first.Toolpaths.Single().Points[0], second.Toolpaths.Single().Points[0]);
    }

    [Fact]
    public void RampEntryScoresBothStartAndReleasePoint()
    {
        var document = Document(Rectangle("STOCK", 0, 0, 100, 100, sheet: true),
            Rectangle("PART", 10, 40, 20, 20));
        var verticalConfig = new ConversionConfig
        {
            Machining = new() { PrioritizePartStability = true, EntryMode = EntryMode.Vertical, RampLength = 20 }
        };
        var rampConfig = verticalConfig with
        {
            Machining = verticalConfig.Machining with { EntryMode = EntryMode.Ramp }
        };
        var vertical = ToolpathPlanner.Generate(Request(document, verticalConfig,
            ("STOCK", false, CutSide.Outside)));
        var ramp = ToolpathPlanner.Generate(Request(document, rampConfig,
            ("STOCK", false, CutSide.Outside)));

        // If only the initial point were scored, ramp and vertical would pick
        // the same entry. The release-aware score deliberately chooses another
        // candidate for this short ramp.
        Assert.NotEqual(vertical.Toolpaths.Single().Points[0], ramp.Toolpaths.Single().Points[0]);
        Assert.DoesNotContain(ramp.Diagnostics, diagnostic => diagnostic.Code == "plan.stable_entry_fallback");
    }

    private static double SignedArea(IReadOnlyList<Point2> points) =>
        points.Zip(points.Skip(1), (a, b) => a.X * b.Y - b.X * a.Y).Sum() / 2;
}
