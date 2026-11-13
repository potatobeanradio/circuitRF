using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf render cavity.c3d -o cut.png --field Field1</c> and <c>--list-fields</c> — a 3D view's field plot
/// (brief-em3d-83) drawn with no window (brief-em3d-84).
///
/// <para><b>This file draws nothing and does no field arithmetic</b>, on <see cref="Render"/>'s terms (cli.md §13.1). The plot
/// is resolved by <see cref="FieldPlotResolver"/> — the 3D view's and the 3D editor's own resolution, moved below the firewall
/// for this — the cut and its range are <see cref="FieldSection"/>'s, the colours and the thinning
/// <see cref="Em3dSectionField"/>'s, and every pixel <see cref="Em3dSectionRenderer"/>'s. What is here is the argument rule,
/// the refusals and the report.</para>
///
/// <para><b>A plot is drawn from ITS run or not at all.</b> A plot whose saved solution, setup or quantity is missing is a
/// refusal carrying R-em3d83-5's sentence verbatim: never the nearest frequency, never an outline passed off as the field.
/// A stale run still draws, as in the 3D view, and says so.</para>
/// </summary>
internal static class RenderEm3dField
{
    /// <summary>The environment variable a test lowers the vector thinning limit with (gate 10); unset, the measured
    /// <see cref="Em3dSectionField.VectorTriangleLimit"/>.</summary>
    internal const string ThinLimitVariable = "CRF_FIELD_THIN_LIMIT";

    // ── --list-fields ────────────────────────────────────────────────────────

    public static int List(string path, RenderEm3d.Request req)
    {
        string? alone = req.OutputStated ? "-o" : req.Field is not null ? "--field" : req.Sections.Count > 0 ? "--section"
                      : req.Iso ? "--iso" : req.Phase is not null ? "--phase" : req.Mip is not null ? "--mip" : req.NoLegend ? "--no-legend" : req.NoThin ? "--no-thin"
                      : req.Labels ? "--labels" : req.Tight ? "--tight" : req.ViewDir is not null ? "--view-dir"
                      : req.Region is not null ? "--region" : req.NoMirror ? "--no-mirror" : null;
        if (alone is not null) return JsonRun.Fail(CliDiagnostics.RenderFieldListAlone(alone));

        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }

        string root = ResultsRoot.For(full, DocumentKinds.AncestorCws(full));
        var sources = new Dictionary<string, Em3dSetupSource>(StringComparer.Ordinal);
        var list = new List<RenderFieldPlotJson>();
        foreach (var plot in doc.FieldPlots)
        {
            RunHost.Cancellation.ThrowIfCancellationRequested();
            var (setupName, run, setupProblem) = Setup(plot, doc, full);
            Em3dProblem? problem = null;
            if (run is not null)
            {
                if (!sources.TryGetValue(setupName, out var src)) sources[setupName] = src = Em3dSetupSource.ForThreeDView(full, setupName);
                problem = src.Generated?.Problem;
            }
            var request = FieldPlotResolver.Request(plot, setupName, run, setupProblem, run);
            string? why = setupProblem;
            if (why is null)
            {
                var found = FieldPlotResolver.Discover(run, root, problem, request.Solver);
                var item = FieldPlotResolver.PickSolution(request.Solution, request.Solver, found.Items, problem, request.Quantity);
                why = item is null ? Missing(request, found, problem) : QuantityProblem(item, request);
            }
            list.Add(new RenderFieldPlotJson(
                plot.Name, setupName, request.Solver, SolutionJson(plot.Solution), plot.Solution?.Describe() ?? "the first saved solution",
                plot.Quantity, plot.Mode, plot.On.ToString(), Target(plot, doc), plot.Hidden, why is null, why, Needs(plot)));
        }
        JsonRun.FieldPlots = list;

        if (list.Count == 0) Console.WriteLine($"{Path.GetFileName(full)} has no field plots.");
        foreach (var p in list)
        {
            var plot = doc.FieldPlots.First(x => x.Name == p.Name);
            Console.WriteLine($"{p.Name}: {FieldPlotResolver.QuantitySymbol(plot)} · {p.Describe} · {p.Target} · setup '{p.Setup}'" +
                              (p.Hidden ? " · hidden" : ""));
            Console.WriteLine(p.Problem is { } why ? $"  data: missing — {why}" : "  data: ready");
            if (p.Headless is { } h) Console.WriteLine($"  --field needs {h}");
        }
        return 0;
    }

    // ── --field ──────────────────────────────────────────────────────────────

    public static int Draw(string path, RenderEm3d.Request req)
    {
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }

        // The plot, by exact name — an unknown one is a refusal LISTING them, as --view lists views.
        var plot = doc.FieldPlots.FirstOrDefault(p => p.Name == req.Field);
        if (plot is null)
            return JsonRun.Fail(CliDiagnostics.RenderFieldUnknown(req.Field!, path,
                doc.FieldPlots.Count == 0 ? "(none)" : string.Join(", ", doc.FieldPlots.Select(p => p.Name))));
        // brief-sar-6 R-sar6-5/8 — a SAR plot's maximum-intensity projection: --mip's axis, or the plot's own Mip along its plane's.
        // It is drawn as a clip plane on that axis, through the middle of the tissue (below, once the volume is read).
        int? mip = null;
        if (req.Mip is { } mipText)
        {
            if (mipText.Trim().ToLowerInvariant() is not ("x" or "y" or "z") || mipText.Trim().Length != 1)
                return JsonRun.Fail(CliDiagnostics.RenderFieldMipMalformed(mipText));
            mip = "xyz".IndexOf(mipText.Trim().ToLowerInvariant()[0]);
        }
        else if (plot.Mip && plot.On == C3dFieldPlotOn.ClipPlane) mip = (int)(plot.Axis ?? C3dAxis.Z);
        if (mip is int mipAxis)
        {
            if (!FieldNames.IsSar(plot.Quantity)) return JsonRun.Fail(CliDiagnostics.RenderFieldMipNotSar(plot.Name, FieldPlotResolver.QuantitySymbol(plot)));
            if (req.Sections.Count > 0)
                return JsonRun.Fail(CliDiagnostics.RenderFieldOptionNotApplicable("--section", plot.Name,
                    "a maximum-intensity projection is drawn on its own plane, through the middle of the tissue along its axis."));
            plot.On = C3dFieldPlotOn.ClipPlane;          // this process's copy of the document; nothing is saved
            plot.Axis = (C3dAxis)mipAxis;
            plot.Through = null;                         // a projection is along its axis, never a two-point plane
            plot.Mip = true;
        }
        var asked = req.Sections.Select(s => $"--section {s}").ToList();
        if (req.Iso) asked.Add("--iso");
        if (req.ViewDir is { } vd) asked.Add($"--view-dir {vd}");
        if (asked.Count > 1) return JsonRun.Fail(CliDiagnostics.RenderEm3dMultipleViews(string.Join(" and ", asked)));
        // brief-em3d-111 — a glTF carries a plot on the model's surfaces; a section has none to carry it on
        if (req.FieldSink is not null && plot.On == C3dFieldPlotOn.ClipPlane) return JsonRun.Fail(CliDiagnostics.ConvertGltfFieldClipPlane(plot.Name));
        // brief-em3d-110 R-em3d110-1e — a realistic picture draws a Surfaces or Faces plot on the model, in the Look's field style
        RenderEm3dRealistic.LookRead? realistic = null;
        Em3dProjection? realisticDirection = null;
        if (req.Realistic)
        {
            if (plot.On == C3dFieldPlotOn.ClipPlane) return JsonRun.Fail(CliDiagnostics.RenderLookClipPlanePlot(plot.Name));
            if (RenderEm3dRealistic.ReadLook(path, req, out realistic) is { } badLook) return badLook;
            if (RenderEm3dRealistic.Direction(path, req, realistic!, out realisticDirection) is { } noCamera) return noCamera;
        }

        // The view follows the plot: a ClipPlane plot IS a section. Another picture is a refusal, never a re-cut.
        bool surface = plot.On != C3dFieldPlotOn.ClipPlane;
        Em3dView own;
        if (!surface)
        {
            own = OwnView(plot, doc);
            string plane = own.Line is { } through ? $"the vertical plane through {Em3dSectionScene.LineText(through)}"
                                                   : $"{own.Axis} = {Em3dSectionScene.FormatLength(own.At)}";
            if (asked.Count == 1 && req.Sections.Count == 0) return JsonRun.Fail(CliDiagnostics.RenderFieldViewDisagrees(plot.Name, plane, asked[0]));
            if (req.Sections.Count == 1)
            {
                var (parsed, refusal) = RenderEm3d.ParseSection(req.Sections[0]);
                if (refusal is { } r) return r;
                if (parsed.Kind != own.Kind || Math.Abs(parsed.At - own.At) > 1e-12 + 1e-9 * Math.Abs(own.At))
                    return JsonRun.Fail(CliDiagnostics.RenderFieldViewDisagrees(plot.Name, plane, $"--section {req.Sections[0]}"));
            }
            if (req.Region is not null)
                return JsonRun.Fail(CliDiagnostics.RenderFieldOptionNotApplicable("--region", plot.Name,
                    "a ClipPlane plot cuts the volume on its own plane. --region names the region a Surfaces plot is drawn on."));
            if (req.NoMirror)
                return JsonRun.Fail(CliDiagnostics.RenderFieldOptionNotApplicable("--no-mirror", plot.Name,
                    "a section is drawn as the model was solved. Only a Surfaces or Faces temperature is mirrored across the symmetry planes."));
        }
        else
        {
            // brief-em3d-89 — a picture of surfaces in depth: a stated direction (the view's camera is not saved), PNG only
            string on = OnText(plot);
            if (req.Sections.Count > 0) return JsonRun.Fail(CliDiagnostics.RenderFieldNotASection(plot.Name, on, asked[0]));
            if (asked.Count == 0 && realistic is null && req.FieldSink is null) return JsonRun.Fail(CliDiagnostics.RenderFieldDirectionRequired(plot.Name, on));
            // a realistic picture taken from the Look's Camera has no stated direction: the caption names that camera instead
            var (look, bad) = asked.Count == 0 ? (Em3dProjection.Standard(Em3dStandardView.Isometric) with { Name = "from the Look's Camera" }, null)
                                               : Direction(req.ViewDir, req.Iso);
            if (bad is { } b) return b;
            if (req.Format != "png") return JsonRun.Fail(CliDiagnostics.RenderFieldSurfacePngOnly(plot.Name, on, req.Format.ToUpperInvariant()));
            if (req.ScaleBar && !AxisAligned(look)) return JsonRun.Fail(CliDiagnostics.RenderFieldScaleBarOblique(look.Name));
            if (req.NoMirror && !plot.IsTemperature)
                return JsonRun.Fail(CliDiagnostics.RenderFieldOptionNotApplicable("--no-mirror", plot.Name,
                    "an EM field is drawn as it was solved. Only a temperature is mirrored across the symmetry planes, as the 3D view mirrors it."));
            own = new Em3dView(Em3dViewKind.Projection, 0) { Projection = look };
        }

        // Its setup, its run, its solution by value, its quantity — or R-em3d83-5's sentence.
        var (setupName, run, setupProblem) = Setup(plot, doc, full);
        if (setupProblem is { } noSetup) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, noSetup));
        var overrides = new RenderTransparency(path, req.Transparency);
        var loaded = Em3dSetupSource.ForThreeDView(full, setupName, overrides.Apply);
        if (overrides.Refusal is { } unknown) return unknown;
        if (loaded.Refusal is { } unbuildable) return JsonRun.Fail(CliDiagnostics.RenderEm3dUnbuildable(path, unbuildable));
        var problem = loaded.Generated!.Problem!;
        RunHost.Cancellation.ThrowIfCancellationRequested();

        string root = ResultsRoot.For(full, DocumentKinds.AncestorCws(full));
        var request = FieldPlotResolver.Request(plot, setupName, run, null, run);
        var found = FieldPlotResolver.Discover(run, root, problem, request.Solver);
        var item = FieldPlotResolver.PickSolution(request.Solution, request.Solver, found.Items, problem, request.Quantity);
        if (item is null) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, Missing(request, found, problem)));
        string? runDir = found.DirectoryOf(item);
        if (runDir is not null) Console.Error.WriteLine($"[circuitRF] fields: {runDir}");

        FieldStep? volume, boundary;
        try
        {
            volume = item.Solution.VolumePvtu is { } v ? FieldStep.Open(v, item.Run.ToMetres, item.Solution.DumpScale) : null;
            boundary = item.Solution.BoundaryPvtu is { } b ? FieldStep.Open(b, item.Run.ToMetres, item.Solution.DumpScale) : null;
        }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.RenderFieldUnreadable(plot.Name, e.Message)); }
        var offered = FieldQuantity.Offered(volume?.Arrays ?? [], boundary?.Arrays ?? []);
        if (FieldPlotResolver.PickQuantity(offered, request, out string? gone) is not { } q)
            return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, gone!));
        if (!surface && (q.OnBoundary || volume is null)) return JsonRun.Fail(CliDiagnostics.RenderFieldOnBoundary(plot.Name, q.Label));
        if (req.Phase is not null && !q.Animated) return JsonRun.Fail(CliDiagnostics.RenderFieldPhaseNotAnimated(plot.Name, q.Label));
        // brief-em3d-100 — the plot's drive, read from its record as the 3D view reads it: no flag, and its refusal in the same words
        var drive = FieldPlotResolver.Drive(request, item.Solution);
        if (drive.Problem is { } noDrive) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, noDrive));
        if (mip is int along)
        {
            if (volume?.WithMip(q.Array.Name, along) is not { } projected)
                return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, $"The run's {FieldNames.Friendly(q.Array.Name)} is not on a grid a projection can be taken along."));
            volume = projected;
            plot.Offset = (long)Math.Round(projected.Middle(along) / C3dLowering.Metres(1, doc.DbuPerMicron));
            own = OwnView(plot, doc);
        }
        // brief-sar-6 R-sar6-5 — the peak averaging cube, drawn where the plane meets it (or always, on a projection)
        var peak = volume is not null ? FieldSarPeak.Of(volume, q.Array.Name, item.Solution.DumpScale, drive.Factor) : null;

        // A stale run still draws (as in the 3D view), and says so: the one comparison the editor's banner makes.
        bool stale = false;
        // brief-em3d-87 — and names what moved on: the document, or any file the run was solved from.
        if (runDir is not null && C3dRunDocument.Check(runDir, doc, full, setupName) is { Stale: true } st)
        {
            stale = true;
            var d = CliDiagnostics.RenderFieldStale(plot.Name, $"{st.What} {st.Has}", st.Written.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }

        // The cut at the 3D view's own origin, on the plane as the view holds it — the same triangles the window draws.
        var origin = FieldPlotResolver.SceneOrigin(loaded.Elaboration?.DisplayExtent());
        if (surface)
            return DrawSurfaces(path, req, doc, plot, own, loaded, problem, setupName, run, found, item, q, volume, boundary, origin, stale, runDir, drive,
                                realistic, realisticDirection);
        var clip = FieldPlotResolver.ScenePlane(plot, doc.DbuPerMicron, origin);
        FieldSectionCut? cut;
        try { cut = FieldSection.Cut(q, volume!, origin, clip, request.Db, request.Percentile, RunHost.Cancellation, drive, own.Line, req.Smooth || plot.Smooth); }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.RenderFieldUnreadable(plot.Name, e.Message)); }
        if (cut is null) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, gone ?? $"The run no longer offers {FieldNames.Friendly(q.Array.Name)}."));

        // brief-em3d-88 — a temperature: the wires from their own T(s), the range extended to them, the thermal boundaries.
        List<Em3dWirePiece> wires = [];
        List<Em3dWireCrossing> crossings = [];
        List<Em3dBoundaryMark> marks = [];
        string hotSpot = "";
        // brief-dfr34-3 — the wires' crossings and the thermal boundaries are found on an axis plane; a two-point section shows the
        // temperature alone
        if (q.IsTemperature && own.Line is null)
        {
            int point = item.Solution.Index;
            var chains = found.Table is { } table && loaded.Elaboration is { } el ? ThermalWireChains.At(el, table, point) : [];
            (wires, crossings) = Em3dSectionThermal.Wires(chains, cut.Axis, own.At);
            cut = Em3dSectionThermal.WithWires(cut, wires);
            if (loaded.Elaboration is { } e2 && run is not null)
                marks = Em3dSectionThermal.Boundaries(doc, e2, run, cut.Axis, own.At, found.Table, point);
            hotSpot = Em3dSectionThermal.HotSpot(cut, wires);
        }

        double phaseDeg = q.Animated ? req.Phase ?? 0 : 0;
        var legend = req.NoLegend ? [] : FieldPlotResolver.LegendLines(plot.Name, q, cut.Scale, item.Label, phaseDeg, loopSeconds: null,
                                                                        stepLabel: item.Label, hotSpot: hotSpot, drive: drive);
        if (mip is int shown && legend.Count > 0) legend.Add($"Maximum along {"xyz"[shown]} (projection)");
        var markers = peak is null || own.Line is not null ? [] : Em3dSectionField.PeakMarkers(peak, cut.Axis, own.At, projection: mip is not null);
        int? thin = req.NoThin ? null : ThinLimit();

        Em3dFieldLayer Layer(Em3dScene scene) => Em3dSectionField.Build(cut, phaseDeg * Math.PI / 180, raster: req.Format == "png", thin, legend,
            q.IsTemperature ? new Em3dThermalPage(wires, crossings, marks,
                                                  Em3dSectionThermal.Caption(scene, setupName, item.Label, marks, wires.Select(w => w.Wire).Distinct().Count()))
                            : null, markers);
        RenderFieldJson Report(Em3dFieldLayer l) => new(
            plot.Name, setupName, request.Solver, SolutionJson(plot.Solution), item.Label, q.Array.Name, q.Mode.ToString(), plot.On.ToString(),
            l.Triangles, l.TrianglesDrawn,
            new RenderFieldRangeJson(cut.Scale.Lo, cut.Scale.Hi, cut.Scale.RangeUnit, cut.Scale.Db, cut.Scale.Percentile),
            stale, runDir, q.Animated ? phaseDeg : null)
        {
            Wires = l.Thermal is null ? null : [.. wires.GroupBy(w => w.Wire).Select(g => new RenderFieldWireJson(
                g.Key, g.Count(), g.SelectMany(w => w.T).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min(),
                g.SelectMany(w => w.T).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max(),
                [.. crossings.Where(c => c.Wire == g.Key).Select(c => new RenderFieldCrossingJson(c.At.U, c.At.V, c.S, c.T))]))],
            Boundaries = l.Thermal is null ? null : [.. marks.Select(m => m.Label)],
        };
        string Line(Em3dFieldLayer l)
        {
            string G(double x) => x.ToString("G4", CultureInfo.InvariantCulture);
            string unit = cut.Scale.RangeUnit;
            return $"{plot.Name}: {q.Symbol} at {item.Label}, {l.Triangles:N0} triangles" +
                   (l.TrianglesDrawn != l.Triangles ? $", drawn as {l.TrianglesDrawn:N0}" : "") +
                   $", {G(cut.Scale.Lo)} … {G(cut.Scale.Hi)}{(unit.Length > 0 ? " " + unit : "")}" +
                   (q.Animated ? $", φ = {phaseDeg.ToString("0.##", CultureInfo.InvariantCulture)}°" : "") +
                   (l.Thermal is { Wires.Count: > 0 } t ? $", {t.Wires.Select(w => w.Wire).Distinct().Count()} wire(s) from their T(s)" : "") +
                   (stale ? " (the model has changed since this run)" : "");
        }
        return RenderEm3d.Picture(path, req, loaded, own, (Layer, Report, Line));
    }

    // ── brief-em3d-89: a Surfaces or Faces plot ──────────────────────────────

    /// <summary>
    /// A Surfaces or Faces plot seen along <paramref name="view"/>'s direction. The 3D view's scene is built here as the 3D editor
    /// builds it (its origin, its colours, its face names); its surfaces are <see cref="FieldSurfacePlot"/>'s — the window's own
    /// builders — and every pixel is <see cref="Em3dSurfaceField"/>'s. What is here is the argument rule, the refusals and the report.
    /// </summary>
    private static int DrawSurfaces(string path, RenderEm3d.Request req, C3dDocument doc, C3dFieldPlot plot, Em3dView view,
                                    Em3dSetupSource loaded, Em3dProblem problem, string setupName, EmSetup? run, FieldDiscovery found,
                                    FieldSolutionItem item, FieldQuantity q, FieldStep? volume, FieldStep? boundary,
                                    (double X, double Y, double Z) origin, bool stale, string? runDir, FieldDriveReading drive,
                                    RenderEm3dRealistic.LookRead? realistic = null, Em3dProjection? realisticDirection = null)
    {
        var look = view.Projection!.Value;
        var e = loaded.Elaboration;
        var (theme, _, _, themeRefusal) = req.themeOf(loaded.Path);
        if (themeRefusal is { } tr) return tr;
        // brief-em3d-110 — one builder call for both pictures (the realistic one keeps its air box, for the Look's ShowAirBox)
        var scene = RenderEm3dRealistic.Scene(loaded, theme, req.Variant, airBox: realistic is not null);
        Scene3DObject? Named(string name) => scene.Objects.FirstOrDefault(o => o.Name == name);
        var faces = plot.On == C3dFieldPlotOn.Faces ? FieldPlotResolver.SceneFaces(plot, Named) : [];
        // the 3D view leaves out a face the model no longer has; a picture says which it left out
        var gone = plot.On == C3dFieldPlotOn.Faces ? plot.Faces.Where(f => FieldPlotResolver.SceneFace(f, Named) is null).Select(f => f.Face).ToList() : [];
        if (gone.Count > 0)
        {
            var d = CliDiagnostics.RenderFieldFacesMissing(plot.Name, string.Join(", ", gone.Select(f => $"'{f}'")));
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }
        var ct = RunHost.Cancellation;
        double phaseDeg = q.Animated ? req.Phase ?? 0 : 0;

        List<FieldSurface> surfaces;
        List<Vector3> nudges;
        List<string> objects;
        HashSet<string> covered;
        FieldColorScale scale;
        List<FieldWireSurface> wires = [];
        List<(int Axis, double AtM)> mirrors = [];
        List<string> refused = [];
        string hotSpot = "";
        Point3? hotAt = null;
        string target;
        try
        {
            if (q.IsTemperature)
            {
                if (volume?.Load(q.Array.Name) is not { } array)
                    return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, $"The run no longer offers {FieldNames.Friendly(q.Array.Name)}."));
                double m = C3dLowering.Metres(1, doc.DbuPerMicron);
                if (!req.NoMirror) mirrors = [.. doc.SymmetryPlanes.Select(p => ((int)p.Axis, p.At * m))];
                var b = FieldSurfacePlot.Temperature(q, volume, array, scene, default, found.Groups,
                    [.. faces.Select(f => new TemperatureFace(f.Object, f.Face))], allFaces: plot.On == C3dFieldPlotOn.Surfaces, onClip: false,
                    found.Table, item.Solution.Index, item.Run.Solutions, plot.FixRange, ct, mirrors);
                if (b.Surfaces.Count == 0 && b.Wires.Count == 0)
                    return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name,
                        "None of the faces it names is in this run's mesh: the model has changed since the run, or the faces are not solids'."));
                wires = [.. b.Wires];
                surfaces = [.. b.Surfaces, .. wires.Select(w => w.Surface)];
                nudges = [.. b.Nudges, .. wires.Select(_ => Vector3.Zero)];
                objects = [.. b.Objects, .. wires.Select(w => w.Wire)];
                covered = [.. b.Covered, .. wires.Select(w => w.Wire)];
                scale = b.Scale;
                // the 3D view's hot-spot marker and its label (Viewer3DViewModel.AdoptTemperature)
                if (FieldHotSpot.Of(q, surfaces, nudges) is { } h)
                {
                    hotSpot = $"{h.Value.ToString("0.0", CultureInfo.InvariantCulture)} °C, {objects[h.Surface]}";
                    var (x, y, z) = scene.ToWorld(h.At);
                    hotAt = new Point3(x, y, z);
                }
                target = plot.On == C3dFieldPlotOn.Surfaces ? "every exposed face" : string.Join(", ", plot.Faces.Select(f => f.Face));
            }
            else
            {
                Scene3DObject? region = null;
                if (plot.On == C3dFieldPlotOn.Surfaces && !q.OnBoundary)
                {
                    var regions = scene.Objects.Where(o => o.Kind is Scene3DKind.Dielectric or Scene3DKind.Air or Scene3DKind.Body &&
                                                           found.Groups.Any(g => g.Name == o.Name && g.Dimension == 3))
                                               .Select(o => o.Name).ToList();
                    string list = regions.Count == 0 ? "(none)" : string.Join(", ", regions);
                    if (req.Region is not { } asked) return JsonRun.Fail(CliDiagnostics.RenderFieldRegionRequired(plot.Name, q.Label, list));
                    if (!regions.Contains(asked)) return JsonRun.Fail(CliDiagnostics.RenderFieldRegionUnknown(asked, list));
                    region = Named(asked);
                }
                else if (req.Region is not null)
                    return JsonRun.Fail(CliDiagnostics.RenderFieldOptionNotApplicable("--region", plot.Name,
                        plot.On == C3dFieldPlotOn.Faces ? "a Faces plot paints the faces it names." : $"{q.Label} is drawn on the conductors, not on a region."));
                var b = FieldSurfacePlot.Em(q, volume, boundary, found.Groups, scene, default, onPlane: false, plot.On == C3dFieldPlotOn.Surfaces,
                    region, FieldSurfacePlot.FaceTargets(scene, faces), null, string.Equals(item.Run.Solver, "openEMS", StringComparison.OrdinalIgnoreCase), ct,
                    drive: drive, smooth: req.Smooth || plot.Smooth);
                refused = [.. b.Refused];
                if (b.Surfaces.Count == 0)
                    return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, string.Join(" ", refused.Append(b.Hint ?? "")).Trim()));
                surfaces = [.. b.Surfaces];
                nudges = [.. b.Nudges];
                objects = [.. b.Objects];
                covered = b.Covered;
                scale = FieldSurfacePlot.EmScale(q, surfaces, plot.Db, plot.Percentile, drive);
                target = plot.On == C3dFieldPlotOn.Faces ? string.Join(", ", plot.Faces.Select(f => f.Face))
                       : region is not null ? $"the boundary of '{region.Name}'" : "the conductors";
            }
        }
        catch (Exception x) when (x is FieldReadException or IOException or UnauthorizedAccessException or ArgumentException)
        { return JsonRun.Fail(CliDiagnostics.RenderFieldUnreadable(plot.Name, x.Message)); }

        // every combination of the planes the view reflects a temperature across; the edges of an All Faces plot follow it
        var sets = new List<(int, double)[]>();
        for (int mask = 1; mask < 1 << mirrors.Count; mask++) sets.Add([.. mirrors.Where((_, i) => (mask & (1 << i)) != 0)]);
        var boundaries = q.IsTemperature && e is not null && run is not null
            ? Em3dSectionThermal.BoundaryLabels(run, e, found.Table, item.Solution.Index) : [];
        var legend = req.NoLegend ? [] : FieldPlotResolver.LegendLines(plot.Name, q, scale, item.Label, phaseDeg, loopSeconds: null,
            fixedAcrossSweep: q.IsTemperature && plot.FixRange && item.Run.Solutions.Count > 1, stepLabel: item.Label, hotSpot: hotSpot, drive: drive);
        if (realistic is not null || req.FieldSink is not null)
        {
            // the 3D view's own field layer: its triangles packed as the view packs them, its range and map, the objects it stands in for
            int triangles = surfaces.Sum(x => x.TriangleCount);
            var report = new RenderFieldJson(
                plot.Name, setupName, FieldPlotResolver.Request(plot, setupName, run, null, run).Solver, SolutionJson(plot.Solution), item.Label,
                q.Array.Name, q.Mode.ToString(), plot.On.ToString(), triangles, triangles,
                new RenderFieldRangeJson(scale.Lo, scale.Hi, scale.RangeUnit, scale.Db, scale.Percentile), stale, runDir, q.Animated ? phaseDeg : null)
            {
                Mirrored = q.IsTemperature ? mirrors.Count : null, HotSpot = hotSpot.Length > 0 ? hotSpot : null,
            };
            string G(double x) => x.ToString("G4", CultureInfo.InvariantCulture);
            string line = $"{plot.Name}: {q.Symbol} at {item.Label}, {triangles:N0} triangles on {target}, {G(scale.Lo)} … {G(scale.Hi)}" +
                          $"{(scale.RangeUnit.Length > 0 ? " " + scale.RangeUnit : "")}" +
                          (q.Animated ? $", φ = {phaseDeg.ToString("0.##", CultureInfo.InvariantCulture)}°" : "") +
                          (stale ? " (the model has changed since this run)" : "");
            var part = new RenderEm3dRealistic.FieldPart(
                new Scene3DFieldGeometry(Scene3DFieldGeometry.Pack(q, surfaces, nudges), 1), covered, q, scale, phaseDeg * Math.PI / 180, legend,
                item.Label, report, line);
            return req.FieldSink is { } sink ? sink(part) : RenderEm3dRealistic.Picture(path, req, realistic!, loaded, scene, realisticDirection, part);
        }
        var caption = Em3dSurfaceField.Caption(q.IsTemperature, q.Symbol, target, look, setupName, item.Label, mirrors.Count,
                                               wires.Select(w => w.Wire).Distinct().Count(), boundaries, refused);
        var (layer, pageScene) = Em3dSurfaceField.Build(scene, problem, look, surfaces, nudges, objects, covered, q, scale, phaseDeg * Math.PI / 180,
            legend, caption, plot.On == C3dFieldPlotOn.Surfaces ? sets : null, hotAt);

        RenderFieldJson Report(Em3dFieldLayer l) => new(
            plot.Name, setupName, FieldPlotResolver.Request(plot, setupName, run, null, run).Solver, SolutionJson(plot.Solution), item.Label,
            q.Array.Name, q.Mode.ToString(), plot.On.ToString(), l.Triangles, l.TrianglesDrawn,
            new RenderFieldRangeJson(scale.Lo, scale.Hi, scale.RangeUnit, scale.Db, scale.Percentile), stale, runDir, q.Animated ? phaseDeg : null)
        {
            Wires = q.IsTemperature ? [.. wires.Select(w => new RenderFieldWireJson(w.Wire, w.Surface.TriangleCount,
                                          w.Surface.Values.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min(),
                                          w.Surface.Values.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max(), []))] : null,
            Boundaries = q.IsTemperature ? boundaries : null,
            Mirrored = q.IsTemperature ? mirrors.Count : null,
            HotSpot = hotSpot.Length > 0 ? hotSpot : null,
        };
        string Line(Em3dFieldLayer l)
        {
            string G(double x) => x.ToString("G4", CultureInfo.InvariantCulture);
            string unit = scale.RangeUnit;
            return $"{plot.Name}: {q.Symbol} at {item.Label}, {l.Triangles:N0} triangles on {target}, {look.Name}" +
                   $", {G(scale.Lo)} … {G(scale.Hi)}{(unit.Length > 0 ? " " + unit : "")}" +
                   (q.Animated ? $", φ = {phaseDeg.ToString("0.##", CultureInfo.InvariantCulture)}°" : "") +
                   (mirrors.Count > 0 ? $", mirrored across {mirrors.Count} plane(s)" : "") +
                   (wires.Count > 0 ? $", {wires.Select(w => w.Wire).Distinct().Count()} wire(s) from their T(s)" : "") +
                   (stale ? " (the model has changed since this run)" : "");
        }
        return RenderEm3d.Picture(path, req, loaded, view, (_ => layer, Report, Line), pageScene);
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The setup a plot reads: the one it pins, or — a plot pinning none (its Setup left unset) — the document's only setup,
    /// which is what the 3D view has active on opening it. The name, the setup as its run is named, or the sentence saying
    /// there is none.
    /// </summary>
    private static (string Name, EmSetup? Run, string? Problem) Setup(C3dFieldPlot plot, C3dDocument doc, string full)
    {
        string? name = plot.Setup;
        if (name is null)
        {
            var all = C3dSetups.Read(doc);
            if (all.Count != 1) return ("", null, FieldPlotResolver.NoActiveSetup);
            name = all[0].Name;
        }
        var (run, problem) = FieldPlotResolver.ResolveSetup(doc, full, name);
        return (name, run, problem);
    }

    /// <summary>R-em3d83-5 — why a plot's run lacks its solution (or its files could not be read at all).</summary>
    private static string Missing(FieldPlotRequest request, FieldDiscovery found, Em3dProblem? problem)
        => found.Items.Count == 0 && found.Why is { } unreadable
            ? "The fields could not be read: " + unreadable
            : FieldPlotResolver.PlotProblem(request, found.Items, found.Ran, problem, found.FieldsDeleted) ?? "The run holds no solution this plot can show.";

    /// <summary>A step that no longer offers the plot's quantity (the second half of R-em3d83-5), or null.</summary>
    private static string? QuantityProblem(FieldSolutionItem item, FieldPlotRequest request)
    {
        try
        {
            var vol = item.Solution.VolumePvtu is { } v ? FieldStep.Open(v, item.Run.ToMetres) : null;
            var bnd = item.Solution.BoundaryPvtu is { } b ? FieldStep.Open(b, item.Run.ToMetres) : null;
            FieldPlotResolver.PickQuantity(FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []), request, out string? why);
            return why;
        }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
        { return "The fields could not be read: " + e.Message; }
    }

    /// <summary>What a Surfaces or Faces plot is drawn on, as its refusals say it.</summary>
    private static string OnText(C3dFieldPlot plot) => plot.On switch
    {
        C3dFieldPlotOn.Surfaces => plot.IsTemperature ? "every exposed face" : "surfaces",
        _ => $"{plot.Faces.Count} face{(plot.Faces.Count == 1 ? "" : "s")}",
    };

    /// <summary>What <c>--list-fields</c> says a plot needs besides <c>--field</c>, or null for a ClipPlane plot (its own section).</summary>
    private static string? Needs(C3dFieldPlot plot) => plot.On == C3dFieldPlotOn.ClipPlane ? null
        : "a direction (--iso or --view-dir) and a .png" +
          (plot.On == C3dFieldPlotOn.Surfaces && !plot.IsTemperature ? ", and --region <object> for a volume quantity" : "");

    /// <summary>
    /// brief-em3d-89 — the direction a surface plot is seen along: <c>--iso</c> (the 3D view's Standard Views ▸ Isometric), a
    /// standard view by name, or <c>x,y,z</c> — the direction from the model toward the viewer, at the 3D view's camera angles
    /// (so <c>0,0,1</c> is exactly Top). The name is what the caption says.
    /// </summary>
    internal static (Em3dProjection View, int? Refusal) Direction(string? text, bool iso)
    {
        if (iso || string.Equals(text, "isometric", StringComparison.OrdinalIgnoreCase))
            return (Em3dProjection.Standard(Em3dStandardView.Isometric) with { Name = "isometric, from +x −y +z" }, null);
        string t = text!.Trim().ToLowerInvariant();
        (Em3dStandardView, string)? named = t switch
        {
            "top" => (Em3dStandardView.Top, "from the top (+z)"),
            "bottom" => (Em3dStandardView.Bottom, "from the bottom (−z)"),
            "front" => (Em3dStandardView.Front, "from the front (−y)"),
            "back" => (Em3dStandardView.Back, "from the back (+y)"),
            "left" => (Em3dStandardView.Left, "from the left (−x)"),
            "right" => (Em3dStandardView.Right, "from the right (+x)"),
            _ => null,
        };
        if (named is var (sv, name)) return (Em3dProjection.Standard(sv) with { Name = name }, null);
        var parts = t.Split(',', StringSplitOptions.TrimEntries);
        var v = new double[3];
        if (parts.Length != 3 || !parts.Select((x, i) => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) && double.IsFinite(v[i])).All(ok => ok))
            return (default, JsonRun.Fail(CliDiagnostics.RenderFieldViewDirMalformed(text)));
        double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        if (!(len > 0)) return (default, JsonRun.Fail(CliDiagnostics.RenderFieldViewDirMalformed(text)));
        double x = v[0] / len, y = v[1] / len, z = v[2] / len;
        // straight up or down, the yaw Top and Bottom are at: the picture's up is +y, as Standard Views ▸ Top shows it
        double yaw = Math.Abs(z) > 1 - 1e-12 ? -Math.PI / 2 : Math.Atan2(y, x);
        string G(double d) => d.ToString("G4", CultureInfo.InvariantCulture).Replace('-', '−');
        return (Em3dProjection.FromYawPitch(yaw, Math.Asin(Math.Clamp(z, -1, 1)), $"from ({G(v[0])}, {G(v[1])}, {G(v[2])})"), null);
    }

    /// <summary>A view along an axis: the picture has one scale, so a scale bar measures it.</summary>
    private static bool AxisAligned(Em3dProjection p)
        => new[] { p.Toward.X, p.Toward.Y, p.Toward.Z }.Count(c => Math.Abs(c) > 1e-9) == 1;

    /// <summary>A ClipPlane plot's own section: its axis at its offset, world metres.</summary>
    private static Em3dView OwnView(C3dFieldPlot plot, C3dDocument doc)
        => FieldSectionLine.Of(plot, doc.DbuPerMicron) is { } line
            ? new Em3dView(Em3dViewKind.SectionLine, 0) { Line = line }      // brief-dfr34-3 — the vertical plane through two points
            : new(plot.Axis switch { C3dAxis.X => Em3dViewKind.SectionX, C3dAxis.Y => Em3dViewKind.SectionY, _ => Em3dViewKind.SectionZ },
                  FieldPlotResolver.PlaneMetres(plot, doc.DbuPerMicron));

    private static string Target(C3dFieldPlot plot, C3dDocument doc) => plot.On switch
    {
        C3dFieldPlotOn.ClipPlane when FieldSectionLine.Of(plot, doc.DbuPerMicron) is { } line => $"section through {Em3dSectionScene.LineText(line)}",
        C3dFieldPlotOn.ClipPlane => $"clip {(plot.Axis ?? C3dAxis.Z).ToString().ToLowerInvariant()} = " +
                                    Em3dSectionScene.FormatLength(FieldPlotResolver.PlaneMetres(plot, doc.DbuPerMicron)),
        C3dFieldPlotOn.Surfaces => plot.IsTemperature ? "all faces" : "surfaces",
        _ => string.Join(", ", plot.Faces.Select(f => f.Face)),
    };

    private static RenderFieldSolutionJson? SolutionJson(C3dFieldSolution? s)
        => s is null ? null : new RenderFieldSolutionJson(s.GHz, s.Port, s.Mode, s.Terminal, s.Point);

    private static int ThinLimit()
        => int.TryParse(Environment.GetEnvironmentVariable(ThinLimitVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0
            ? n : Em3dSectionField.VectorTriangleLimit;
}
