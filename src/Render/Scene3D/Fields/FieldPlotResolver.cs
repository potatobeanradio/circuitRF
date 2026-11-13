// brief-em3d-84 R-em3d84-1 — a document FIELD PLOT (brief-em3d-83) resolved against a run, with no window: which run it
// reads, which saved solution it shows (picked BY VALUE), which quantity, where, and — when it cannot be drawn — the sentence
// that says why (R-em3d83-5). The 3D view, the 3D editor and `circuitrf render --field` all resolve a plot HERE, so a plot the
// window draws is the plot the command line draws, and a plot one of them refuses the other refuses in the same words.
//
// Moved out of Viewer3DViewModel.Plot.cs and C3dEditorViewModel.FieldPlots.cs unchanged. What stayed behind is view state:
// the phase, the loop, play/pause, the selected solid, and the editor's active and external setups.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A solution in the picker: a saved frequency (driven), a mode (eigenmode), a terminal (static).</summary>
public sealed record FieldSolutionItem(FieldSolution Solution, string Label, FieldRun Run)
{
    public override string ToString() => Label;
}

/// <summary>brief-em3d-82 — a face painted with the EM field: the object's name, its face index in the scene, and — on a
/// sheet, for a volume quantity — the side shown (+1 along the sheet's own normal, −1 against it; 0 otherwise).</summary>
public readonly record struct PaintedFieldFace(string Object, int Face, int Side = 0);

/// <summary>brief-em3d-83 — the field plot the viewer draws, as the editor resolved it against the current scene.</summary>
public sealed record FieldPlotRequest
{
    /// <summary>The plot's name: the legend's first line.</summary>
    public string Name { get; init; } = "";

    /// <summary>The setup it reads, as the user names it (the sentences say this).</summary>
    public string SetupName { get; init; } = "";

    /// <summary>The setup as its run is named (C3dSetups.ForRun) — where the fields are read from. Null: the view's own setup
    /// (a setup's 3D view, which has no other).</summary>
    public EmSetup? RunSetup { get; init; }

    /// <summary>Why there is no <see cref="RunSetup"/> (the setup was deleted or renamed), or null.</summary>
    public string? SetupProblem { get; init; }

    /// <summary>"Palace" or "openEMS" for a setup that runs both; null otherwise.</summary>
    public string? Solver { get; init; }

    public C3dFieldSolution? Solution { get; init; }
    public string Quantity { get; init; } = "E";
    public FieldMode? Mode { get; init; }
    public C3dFieldPlotOn On { get; init; }

    /// <summary>A ClipPlane plot's plane, scene-local metres; always enabled.</summary>
    public ClipPlane3D Plane { get; init; }

    /// <summary>A Faces plot's faces, resolved to the scene's face indices.</summary>
    public IReadOnlyList<PaintedFieldFace> Faces { get; init; } = [];

    public bool Db { get; init; }

    /// <summary>brief-cxp-5 — the plot's <c>Smooth</c>: values averaged within each material before drawing.</summary>
    public bool Smooth { get; init; }
    public double Percentile { get; init; } = 99;
    public bool FixRange { get; init; }

    /// <summary>brief-sar-6 R-sar6-5 — a SAR ClipPlane plot drawn as its maximum-intensity projection along its axis.</summary>
    public bool Mip { get; init; }

    /// <summary>brief-em3d-100 — the plot's drive power, W (null: the solver's own), and what it is referred to.</summary>
    public double? DrivePowerW { get; init; }
    public C3dDriveReferredTo DriveReferredTo { get; init; }

    public bool IsTemperature => Quantity == C3dFieldPlot.TemperatureQuantity;

    /// <summary>brief-em3d-96 — what the requester says identifies this request (the plot, its plane, its faces, its run): the 3D
    /// view leaves a drawn plot whose key is unchanged exactly as it is. Empty: always applied.</summary>
    public string Key { get; init; } = "";
}

/// <summary>brief-em3d-83 — what a setup's runs saved, read synchronously: the editor's Solution picker and the missing-data
/// check of a plot that is not drawn.</summary>
public sealed record FieldDiscovery(IReadOnlyList<FieldRun> Runs, IReadOnlyList<FieldSolutionItem> Items, ThermalResultTable? Table,
                                    IReadOnlyList<FieldGroup> Groups, string? Why, bool Ran, string? PalaceDir, string? OpenEmsDir)
{
    /// <summary>The run directory <paramref name="item"/> was read from.</summary>
    public string? DirectoryOf(FieldSolutionItem item)
        => string.Equals(item.Run.Solver, "openEMS", StringComparison.OrdinalIgnoreCase) ? OpenEmsDir : PalaceDir;

    /// <summary>brief-dfr32-3 — the Palace run's saved fields were deleted (Delete Saved Fields) and it has not run since.</summary>
    public bool FieldsDeleted { get; init; }
}

public static class FieldPlotResolver
{
    /// <summary>A plot with no setup of its own, in a document with no active one (R-em3d83-5).</summary>
    public const string NoActiveSetup = "No setup is active: pick this plot's setup in the Properties Inspector.";

    /// <summary>A plot whose pinned setup the document no longer holds (R-em3d83-5).</summary>
    public static string MissingSetup(string name)
        => $"The setup '{name}' is no longer in this 3D view: pick this plot's setup in the Properties Inspector.";

    // ── the setup and the request ───────────────────────────────────────────────────────────

    /// <summary>The document's setup named <paramref name="name"/>, as its run is named (C3dSetups.ForRun), or the sentence
    /// saying it is gone.</summary>
    public static (EmSetup? Run, string? Problem) ResolveSetup(C3dDocument doc, string topPath, string name)
        => C3dSetups.Read(doc).FirstOrDefault(s => s.Name == name) is { Setup: { } found }
            ? (C3dSetups.ForRun(found, topPath), null)
            : (null, MissingSetup(name));

    /// <summary>
    /// <paramref name="p"/> as the viewer takes it, before the scene is consulted: its run's setup (<paramref name="run"/>, or
    /// why there is none), its solver when <paramref name="shape"/> runs both, and its reading.
    /// </summary>
    public static FieldPlotRequest Request(C3dFieldPlot p, string setupName, EmSetup? run, string? setupProblem, EmSetup? shape)
    {
        string? solver = shape is { Solver3D.IsCompare: true } ? Em3dSolvers.Get(p.Solver ?? shape.Solver3D.Reference).DisplayName : null;
        return new FieldPlotRequest
        {
            Name = p.Name, SetupName = setupName, RunSetup = run, SetupProblem = setupProblem, Solver = solver,
            Solution = p.Solution, Quantity = p.Quantity, On = p.On, Db = p.Db, Smooth = p.Smooth, Percentile = p.Percentile, FixRange = p.FixRange,
            DrivePowerW = p.DrivePowerW, DriveReferredTo = p.DriveReferredTo,
            Mip = p.Mip && p.On == C3dFieldPlotOn.ClipPlane && FieldNames.IsSar(p.Quantity),
            Mode = Enum.TryParse<FieldMode>(p.Mode, ignoreCase: true, out var m) ? m : null,
        };
    }

    /// <summary>The origin a 3D view's scene is placed at for elaboration <paramref name="e"/>: the centre of what it displays
    /// (a millimetre cube round the origin when it displays nothing). A ClipPlane plot's plane and its slice are in metres
    /// from here, so the headless picture cuts the field exactly where the window does.</summary>
    public static (double X, double Y, double Z) SceneOrigin((double X0, double Y0, double Z0, double X1, double Y1, double Z1)? extent)
    {
        var x = extent ?? EmptyExtent;
        return ((x.X0 + x.X1) / 2, (x.Y0 + x.Y1) / 2, (x.Z0 + x.Z1) / 2);
    }

    /// <summary>What an empty 3D view displays: a millimetre cube round the origin.</summary>
    public static readonly (double X0, double Y0, double Z0, double X1, double Y1, double Z1) EmptyExtent = (-5e-4, -5e-4, -5e-4, 5e-4, 5e-4, 5e-4);

    /// <summary>A ClipPlane plot's plane in scene-local metres: its axis, its DBU offset less the scene's origin — or the vertical
    /// plane through its two points — kept on the side <paramref name="flip"/> keeps.</summary>
    public static ClipPlane3D ScenePlane(C3dFieldPlot p, int dbuPerMicron, (double X, double Y, double Z) origin, bool flip = false)
    {
        // brief-dfr34-3 R-dfr34-3-1 — a section through two points: the vertical plane holding both
        if (FieldSectionLine.Of(p, dbuPerMicron) is { } line) return line.ScenePlane(origin, flip);
        int a = (int)(p.Axis ?? C3dAxis.Z);
        double world = PlaneMetres(p, dbuPerMicron);
        double o = a == 0 ? origin.X : a == 1 ? origin.Y : origin.Z;
        return new ClipPlane3D { Enabled = true, Axis = (ClipAxis3D)a, Offset = (float)(world - o), Flip = flip };
    }

    /// <summary>Where a ClipPlane plot's plane is along its axis, world metres.</summary>
    public static double PlaneMetres(C3dFieldPlot p, int dbuPerMicron) => C3dLowering.Metres(p.Offset ?? 0, dbuPerMicron);

    /// <summary>A Faces plot's faces by the scene's indices; a face <paramref name="objectNamed"/> no longer has is left out.</summary>
    public static List<PaintedFieldFace> SceneFaces(C3dFieldPlot p, Func<string, Scene3DObject?> objectNamed)
    {
        var list = new List<PaintedFieldFace>();
        foreach (var f in p.Faces)
            if (SceneFace(f, objectNamed) is { } i)
                list.Add(new PaintedFieldFace(i.Object, i.Face, f.Side switch { C3dFieldSide.Top => 1, C3dFieldSide.Bottom => -1, _ => 0 }));
        return list;
    }

    /// <summary><c>object/face</c> as the scene's object and face index, or null when the scene has no such face.</summary>
    public static (string Object, int Face)? SceneFace(C3dFieldPlotFace f, Func<string, Scene3DObject?> objectNamed)
    {
        var (obj, face) = f.Parts;
        if (objectNamed(obj) is not { } so) return null;
        for (int i = 0; i < Math.Max(so.FaceNames.Count, 1); i++)
            if (so.FaceName(i) == face) return (so.Name, i);
        return null;
    }

    /// <summary>What a plot reads, as the tree writes it: <c>|E|</c>, <c>Re{E}</c>, <c>V</c>, <c>T</c>.</summary>
    public static string QuantitySymbol(C3dFieldPlot p) => p.IsTemperature ? "T" : p.Mode switch
    {
        nameof(FieldMode.Instantaneous) => $"Re{{{p.Quantity}}}",
        nameof(FieldMode.Value) => p.Quantity,
        _ => $"|{p.Quantity}|",
    };

    // ── the solution, by value ──────────────────────────────────────────────────────────────

    /// <summary>The saved solution a plot shows: its value among the run's (of its solver), or the first when it names none.
    /// brief-sar-6 — of the solutions at that value, the one whose step holds <paramref name="quantity"/> (a SAR run's SAR or
    /// B1 volumes, FieldSolution.Offers), else an ordinary field step.</summary>
    public static FieldSolutionItem? PickSolution(C3dFieldSolution? wanted, string? solver, IEnumerable<FieldSolutionItem> items, Em3dProblem? problem,
                                                  string? quantity = null)
    {
        var mine = items.Where(i => solver is null || string.Equals(i.Run.Solver, solver, StringComparison.OrdinalIgnoreCase));
        var at = (wanted is null ? mine : mine.Where(i => SolutionKey(i.Solution, problem).SameAs(wanted))).ToList();
        return at.FirstOrDefault(i => quantity is not null && i.Solution.Offers?.Contains(quantity) == true)
               ?? at.FirstOrDefault(i => i.Solution.Offers is null && (quantity is null || !FieldNames.IsMagnitude(quantity)))
               ?? (quantity is not null && FieldNames.IsMagnitude(quantity) ? null : at.FirstOrDefault());
    }

    /// <summary>A saved step as a plot names it, by value.</summary>
    public static C3dFieldSolution SolutionKey(FieldSolution s, Em3dProblem? problem) => s.Kind switch
    {
        FieldProblemKind.Driven => new C3dFieldSolution { GHz = s.Timestep, Port = s.Excitation > 0 ? s.Excitation : null, Cosim = s.Cosim },
        FieldProblemKind.Eigenmode => new C3dFieldSolution { Mode = s.Index + 1 },
        FieldProblemKind.Thermal => new C3dFieldSolution { Point = s.Index + 1 },
        // brief-dfr31-3 — the terminal the run itself named first: the 3D view's display problem names none
        _ => new C3dFieldSolution
        {
            Terminal = s.Terminal ?? (problem?.Terminals is { } t && s.Index >= 0 && s.Index < t.Count ? t[s.Index].Name : (s.Index + 1).ToString(CultureInfo.InvariantCulture)),
        },
    };

    /// <summary>
    /// R-em3d83-5 — the sentence a plot whose run lacks its solution carries: no run yet, or what the run DID save beside what
    /// the plot asks for. Null when <paramref name="saved"/> holds it. <paramref name="fieldsDeleted"/> (brief-dfr32-3): the run's
    /// saved fields were deleted since it ran (<see cref="FieldDiscovery.FieldsDeleted"/>), which is why it holds none.
    /// </summary>
    public static string? PlotProblem(FieldPlotRequest p, IReadOnlyList<FieldSolutionItem> saved, bool ran, Em3dProblem? problem,
                                      bool fieldsDeleted = false)
    {
        if (p.SetupProblem is { } why) return why;
        var mine = saved.Where(i => p.Solver is null || string.Equals(i.Run.Solver, p.Solver, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0 && ran && fieldsDeleted) return FieldsDeletedSentence(p.SetupName);
        if (mine.Count == 0)
            return ran ? $"The run of setup {p.SetupName} saved no fields — set Save fields at in its setup and Simulate again."
                       : $"No finished run of setup {p.SetupName} yet — Simulate to draw this plot.";
        if (PickSolution(p.Solution, p.Solver, mine, problem, p.Quantity) is not null) return null;
        // brief-sar-6 — a SAR or B1 plot of a run that computed none
        if (FieldNames.IsMagnitude(p.Quantity) && !mine.Any(i => i.Solution.Offers?.Contains(p.Quantity) == true))
            return mine.Any(i => i.Solution.Offers is not null)
                ? $"The run of setup {p.SetupName} saved no {FieldNames.Friendly(p.Quantity)}: add its mass to Sar.MassesGrams (or turn Sar.B1 on) and Simulate again."
                : $"The run of setup {p.SetupName} computed no SAR: give the setup a Sar group and Simulate again.";
        var w = p.Solution!;
        string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        var keys = mine.Select(i => SolutionKey(i.Solution, problem)).ToList();
        if (w.GHz is { } f)
        {
            var at = keys.Where(k => k.GHz is not null).Select(k => k.GHz!.Value).Distinct().OrderBy(x => x).Select(G);
            if (keys.Any(k => k.GHz is { } g && Math.Abs(g - f) <= 1e-9 * Math.Max(g, f)))
                return $"The run saved {G(f)} GHz, but not for port {w.Port}. Pick a saved port.";
            return $"The run saved {string.Join(", ", at)} GHz; this plot shows {G(f)} GHz. Pick a saved frequency, or add {G(f)} to " +
                   "Save fields at and run again.";
        }
        if (w.Mode is { } m)
            return $"The run saved modes {string.Join(", ", keys.Select(k => k.Mode).OfType<int>())}; this plot shows mode {m}. " +
                   "Pick a saved mode, or ask for more modes and run again.";
        if (w.Terminal is { } t)
            return $"The run saved terminals {string.Join(", ", keys.Select(k => $"'{k.Terminal}'"))}; this plot shows terminal '{t}'. Pick a saved terminal.";
        if (w.Point is { } k0)
            return $"The run has {keys.Count} sweep point{(keys.Count == 1 ? "" : "s")}; this plot shows point {k0}. Pick a saved point.";
        return null;
    }

    /// <summary>brief-dfr32-3 — why a plot of a run whose saved fields were deleted draws nothing.</summary>
    public static string FieldsDeletedSentence(string? setupName)
        => $"The saved fields of setup {setupName} were deleted (Delete Saved Fields) — Simulate it again to draw this plot.";

    // ── before the run (brief-dfr31-3 R-dfr31-3-4) ──────────────────────────────────────────

    /// <summary>The Solution picker's one row while <paramref name="setup"/> has no finished run: what will appear there, in the
    /// setup's own terms — never a driven setup's frequencies for a static one.</summary>
    public static string AwaitingRun(EmSetup setup) => setup.IsThermal ? "Sweep points of the thermal setup, once it has run" : setup.Problem3D switch
    {
        Em3dProblemType.Electrostatic => "Terminals of the electrostatic setup, once it has run",
        Em3dProblemType.Magnetostatic => "Terminals of the magnetostatic setup, once it has run",
        Em3dProblemType.Eigenmode => "Modes of the eigenmode setup, once it has run",
        _ => "Frequencies of the driven setup, once it has run",
    };

    /// <summary>The quantities <paramref name="setup"/>'s run will offer, for the Quantity picker before it has run: the arrays
    /// the FEM solver writes for its problem, read as <see cref="FieldQuantity.Offered"/> reads them, so each label is the one
    /// the run's own will have. Empty where the arrays are not known ahead (another solver, a thermal run).</summary>
    public static IReadOnlyList<FieldQuantity> ExpectedQuantities(EmSetup setup)
    {
        if (setup.IsThermal || FieldSolver(setup, Em3dSolverFit.Fem, null)?.Capabilities.FieldArraysKnownAhead != true) return [];
        static FieldArrayInfo A(string name, int components, bool complex = false) => new(name, components, complex, PerCell: false);
        return setup.Problem3D switch
        {
            Em3dProblemType.Electrostatic => FieldQuantity.Offered([A("E", 3), A("V", 1), A("U_e", 1)], [A("Q_s", 1)]),
            Em3dProblemType.Magnetostatic => FieldQuantity.Offered([A("B", 3), A("U_m", 1)], []),
            _ => FieldQuantity.Offered([A("E", 3, true), A("B", 3, true), A("S", 3), A("U_e", 1), A("U_m", 1)],
                                       [A("J_s", 3, true), A("Q_s", 1, true)]),
        };
    }

    /// <summary>The plot's quantity among what the loaded step offers, or null and the sentence saying the step offers none.</summary>
    public static FieldQuantity? PickQuantity(IReadOnlyList<FieldQuantity> offered, FieldPlotRequest p, out string? problem)
    {
        var q = offered.FirstOrDefault(x => x.Array.Name == p.Quantity && (p.Mode is null || x.Mode == p.Mode));
        problem = q is not null ? null
            : $"The run no longer offers {FieldNames.Friendly(p.Quantity)}" +
              (p.Mode is { } m && offered.Any(x => x.Array.Name == p.Quantity) ? $" read as {m}" : "") + ".";
        return q;
    }

    // ── discovery ───────────────────────────────────────────────────────────────────────────

    /// <summary>The run directories of <paramref name="setup"/>'s 3D solvers under <paramref name="root"/>: the FEM solver's (or a
    /// thermal run's own), openEMS's — whichever it runs. <paramref name="solver"/> (a plot's, by display name) picks among the
    /// FEM solvers of a comparison; otherwise the first that saves fields.</summary>
    public static (string? Palace, string? OpenEms) RunDirectories(EmSetup? setup, string? root, string? solver = null)
    {
        if (setup is null || root is null) return (null, null);
        if (setup.IsThermal) return (ThermalRunService.RunDirectory(root, setup), null);
        string? Dir(Em3dSolverFit method)
            => FieldSolver(setup, method, solver) is { } d ? Em3dRunService.RunDirectory(root, setup, d.Id) : null;
        return (Dir(Em3dSolverFit.Fem), Dir(Em3dSolverFit.Fdtd));
    }

    /// <summary>brief-emi-1/5/7 — the solver of <paramref name="setup"/> whose run directory holds its <paramref name="method"/>'s
    /// field set: of the solvers that save fields, <paramref name="solver"/> when it is one, else the first chosen.</summary>
    private static Em3dSolverDescriptor? FieldSolver(EmSetup setup, Em3dSolverFit method, string? solver)
    {
        var able = Em3dSolvers.Of(setup.Solver3D).Where(d => d.Method == method && d.Capabilities.Fields).ToList();
        return able.FirstOrDefault(d => string.Equals(d.DisplayName, solver, StringComparison.OrdinalIgnoreCase)) ?? able.FirstOrDefault();
    }

    /// <summary>What <paramref name="setup"/>'s runs saved, each step labelled as the picker shows it. File reads: call it off the
    /// UI thread when it can be large, or cache it. <paramref name="solver"/> as <see cref="RunDirectories"/> takes it.</summary>
    public static FieldDiscovery Discover(EmSetup? setup, string? root, Em3dProblem? problem, string? solver = null)
    {
        var (dir, openEmsDir) = RunDirectories(setup, root, solver);
        bool emerge = setup is { IsThermal: false } && FieldSolver(setup, Em3dSolverFit.Fem, solver)?.Id == EmergeSolver.Id;
        FieldRun? run = null, openEms = null;
        string? why = null;
        var table = setup is { IsThermal: true } && root is not null ? ReadThermalTable(setup, root) : null;
        try
        {
            run = dir is null ? null : emerge ? FieldRun.OpenEmerge(dir) : FieldRun.OpenPalace(dir, GmshGeoWriter.LengthUnitM);
            openEms = openEmsDir is null ? null : FieldRun.OpenOpenEms(openEmsDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { why = e.Message; }
        var groups = dir is null ? [] : FieldGroups.Read(dir);
        var modes = run?.Kind != FieldProblemKind.Eigenmode ? null
            : emerge ? EmergeResults.ReportedModes(dir!)?.Kept.Select(m => new PalaceMode(m.Index, m.FrequencyHz, 0, m.Q, 0, 0)).ToList()
            : PalaceRun.ReadModes(Path.Combine(dir!, PalaceConfigWriter.OutputDirectory, PalaceRun.EigFile), out _);
        FieldRun[] runs = [.. new[] { run, openEms }.OfType<FieldRun>()];
        var items = new List<FieldSolutionItem>();
        foreach (var r in runs)
            foreach (var x in r.Solutions)
                items.Add(new FieldSolutionItem(x, (x.Kind == FieldProblemKind.Thermal
                    ? table is { } tt && x.Index < tt.Points ? tt.PointLabel(x.Index) : $"Point {x.Index + 1}"
                    : SolutionLabel(x, modes, problem, r.Solver)) + (runs.Length > 1 ? $" ({r.Solver})" : ""), r));
        // A run that failed or was cancelled leaves its directory behind (a Gmsh refusal keeps the mesher's files): that is no
        // run to have saved fields in, and "set Save fields at" sent the reader to a setting that was never the cause.
        bool Finished(string? d) => d is not null && Directory.Exists(d)
                                    && C3dRunStatus.Read(d) is not { State: C3dRunState.Failed or C3dRunState.Cancelled };
        bool ran = Finished(dir) || Finished(openEmsDir);
        return new FieldDiscovery(runs, items, table, groups, why, ran, dir, openEmsDir) { FieldsDeleted = PalaceFieldFiles.WereDeleted(dir) };
    }

    /// <summary>The thermal run's table, read when the fields are.</summary>
    public static ThermalResultTable? ReadThermalTable(EmSetup setup, string root)
    {
        string npy = ThermalRunService.NpyPath(root, setup);
        return File.Exists(npy) ? ThermalResultTable.Read(npy, out _) : null;
    }

    /// <summary>
    /// A saved step as the picker and the legend label it. brief-em3d-100 R-em3d100-4 — a driven step names the port it drives
    /// and no power: the drive is the PLOT's (FieldDrive.Read), stated on the legend's own line. (Round 11's "with 1 W incident"
    /// was Palace's own unit, which is 0.5 W time-averaged in circuitRF's peak convention — PalaceDrive.)
    /// </summary>
    public static string SolutionLabel(FieldSolution s, IReadOnlyList<PalaceMode>? modes, Em3dProblem? problem, string? solver = null)
    {
        string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        int port = s.Excitation > 0 ? s.Excitation : s.Drive?.Port ?? 0;
        // brief-sar-6 — a SAR run's volumes are their own solutions at the dump's frequency and port
        string sar = s.OffersLabel is { } what ? $" ({what})" : "";
        // brief-sar-9 — a fields analysis's recombined field, driven from its circuit's port
        if (s.Cosim is { } cosim)
            return $"{G(s.Timestep)} GHz, fields '{cosim}' (circuit port {s.Drive?.Port ?? 0} driven)" + sar;
        return sar.Length > 0 ? SolutionLabel(s with { Offers = null }, modes, problem, solver) + sar : s.Kind switch
        {
            FieldProblemKind.Driven => $"{G(s.Timestep)} GHz" + (port <= 0 ? ""
                : solver is null or "Palace" && ModeOf(port, problem) is { } mode ? $", {mode} driven" : $", port {port} driven"),
            FieldProblemKind.Eigenmode when modes?.FirstOrDefault(m => m.Index == s.Index + 1) is { } m =>
                $"Mode {s.Index + 1}: {G(m.FrequencyHz / 1e9)} GHz, Q {m.Q.ToString("G3", CultureInfo.InvariantCulture)}",
            FieldProblemKind.Eigenmode => $"Mode {s.Index + 1}",
            FieldProblemKind.Electrostatic => $"Terminal {(s.Terminal is { } named ? $"'{named}'" : TerminalName(s.Index, problem))} at 1 V, the others at 0 V",
            FieldProblemKind.Thermal => $"Point {s.Index + 1}",
            _ => $"Terminal {TerminalName(s.Index, problem)} carrying 1 A",
        };
    }

    /// <summary>
    /// brief-em3d-115 R-em3d115-6 (D8) — on Palace, a terminal face's excitation k drives the face's mode k, not terminal k
    /// alone (the run converts the modes to terminal S afterwards; no terminal drive is superposed here): <c>Left mode 2</c>.
    /// Null for a port on no terminal face.
    /// </summary>
    private static string? ModeOf(int port, Em3dProblem? problem)
    {
        if (problem?.Ports.FirstOrDefault(q => q.Number == port) is not { FaceGroup: { } g } p) return null;
        int k = problem.Ports.Where(q => q.FaceGroup == g).OrderBy(q => q.Number).ToList().IndexOf(p) + 1;
        return $"{p.FaceGroupLabel ?? p.Name} mode {k.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>brief-em3d-100 — request <paramref name="p"/>'s drive at <paramref name="s"/> for its own quantity
    /// (FieldDrive.Read).</summary>
    public static FieldDriveReading Drive(FieldPlotRequest p, FieldSolution s) => FieldDrive.Read(s, p.Quantity, p.DrivePowerW, p.DriveReferredTo);

    private static string TerminalName(int index, Em3dProblem? problem)
        => problem?.Terminals is { } t && index >= 0 && index < t.Count ? $"'{t[index].Name}'" : (index + 1).ToString(CultureInfo.InvariantCulture);

    // ── the legend ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The legend's lines: the plot's name (brief-em3d-83), the quantity, the range, and what was solved (R-em3d29-3c/3d). An
    /// animated quantity states its phase; <paramref name="loopSeconds"/> adds the window's "one cycle every … on screen", which
    /// a still picture has no use for. A temperature carries its own lines (brief-em3d-75 R-em3d75-4b).
    /// <para>brief-em3d-100 — <paramref name="drive"/>'s line follows the solution's (the drive a driven field is shown at), and
    /// a field with no referral reads with no unit.</para>
    /// </summary>
    public static List<string> LegendLines(string? plotName, FieldQuantity q, FieldColorScale s, string? solutionLabel,
                                           double phaseDegrees, double? loopSeconds,
                                           bool fixedAcrossSweep = false, string stepLabel = "", string hotSpot = "",
                                           FieldDriveReading? drive = null)
    {
        List<string> lines;
        if (q.IsTemperature)
        {
            lines = ["Temperature (°C)", s.Describe() + (fixedAcrossSweep ? ", fixed across the sweep" : "")];
            if (stepLabel.Length > 0) lines.Add(stepLabel);
            if (hotSpot.Length > 0) lines.Add("Hot spot: " + hotSpot);
        }
        else
        {
            string unit = drive is { Relative: true } ? "" : FieldNames.Unit(q.Array.Name);
            lines =
            [
                $"{q.Symbol}{(s.Db ? $" ({FieldColorScale.DbUnit(unit)})" : unit.Length > 0 ? $" ({unit})" : "")}",
                s.Describe(),
            ];
            if (solutionLabel is not null) lines.Add(solutionLabel);
            if (drive?.Line is { } d) lines.Add(d);
            if (q.Animated)
                lines.Add($"φ = {phaseDegrees.ToString("0", CultureInfo.InvariantCulture)}°" +
                          (loopSeconds is { } l ? $", one cycle every {l.ToString("0.##", CultureInfo.InvariantCulture)} s on screen" : ""));
        }
        if (plotName is { Length: > 0 }) lines.Insert(0, plotName);
        return lines;
    }
}
