// Framework-free. No Avalonia, no SkiaSharp.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What <see cref="EmSnpSchematic.Create"/> did.</summary>
public enum EmSnpSchematicOutcome
{
    /// <summary>The cell was made: an SnP of the run, a TermG per port, an S-parameter analysis and a Data Display.</summary>
    Created,
    /// <summary>The cell existed and its SnP already reads this run's Touchstone; nothing was written.</summary>
    Current,
    /// <summary>The cell existed and its SnP read this setup's results under another port count; its <c>File</c> and
    /// <c>NumPorts</c> now name this run's Touchstone and nothing else was changed.</summary>
    Repointed,
    /// <summary>The cell existed and does not read this setup's results; nothing was written.</summary>
    LeftAlone,
    /// <summary>Nothing could be created — the run wrote no Touchstone, or the write failed.</summary>
    NotCreated,
}

/// <param name="Outcome">What happened.</param>
/// <param name="CellName">The cell, <c>&lt;setup&gt;_sim</c>.</param>
/// <param name="SchematicPath">The schematic created, re-pointed or found; null when there is none.</param>
/// <param name="Sentence">What the run says about it, in the user's words.</param>
public sealed record EmSnpSchematicResult(
    EmSnpSchematicOutcome Outcome, string CellName, string? SchematicPath, string Sentence)
{
    /// <summary>True when there is a schematic to open.</summary>
    public bool HasSchematic => SchematicPath is not null && Outcome is EmSnpSchematicOutcome.Created
                                    or EmSnpSchematicOutcome.Current or EmSnpSchematicOutcome.Repointed;
}

/// <summary>Which side of the SnP block a port's TermG goes on: the side the port faces in the layout.</summary>
public enum EmPortFace { Left, Right, Top, Bottom }

/// <summary>
/// brief-dfr31-4 — <b>the schematic a finished driven run is first used in</b>: an SnP reading the run's Touchstone, a
/// TermG on every port facing out from the block on the side its port faces in the layout, an S-parameter analysis over
/// the setup's sweep, and a Data Display of the through paths and the input matches.
///
/// <para><b>Made through <see cref="CellCreate"/></b>, the writer New Cell calls, so there is one implementation of a
/// cell on disk. <b>An existing cell is never overwritten</b>: a re-run finds the cell already reading the same path
/// (the Touchstone path is predictable by design, <see cref="EmRunService.ResolveSnpPath"/>) and leaves it alone.</para>
///
/// <para><b>Called by <see cref="EmRunService"/> once a run has written its Touchstone</b>, so the panel's Simulate and
/// <c>circuitrf em</c> both honour the setup's flag through one door.</para>
/// </summary>
public static class EmSnpSchematic
{
    /// <summary>What is appended to the setup's result key to name the cell.</summary>
    public const string CellSuffix = "_sim";

    /// <summary>The SnP instance's name.</summary>
    public const string SnpInstanceName = "S1";

    /// <summary>The S-parameter analysis's name.</summary>
    public const string AnalysisName = "SP1";

    /// <summary>Grid between TermGs along the left and right sides: a TermG turned on its side carries its four label
    /// lines below it, so they need the room.</summary>
    private const double SidePitch = 800;

    /// <summary>Grid between TermGs along the top and bottom, where the labels sit beside the glyph.</summary>
    private const double RowPitch = 600;

    private const double P = 100;

    /// <summary>The cell a setup's schematic is made as: the result's name (the setup's Output name, or its key when that is blank)
    /// plus <see cref="CellSuffix"/>, with any character a cell name may not hold replaced. Each output name has its own cell.</summary>
    public static string CellNameFor(EmSetup setup)
    {
        string key = EmResultNames.CompanionKey(setup);
        var sb = new System.Text.StringBuilder(key.Length + CellSuffix.Length);
        foreach (char c in key)
            sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c);
        return (sb.Length > 0 ? sb.ToString() : "em") + CellSuffix;
    }

    // ── Creation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Make <c>&lt;setup&gt;_sim</c> in <paramref name="workspaceDir"/> for the Touchstone at <paramref name="snpPath"/>,
    /// or report what an existing one of that name does. Never throws: a failure is a
    /// <see cref="EmSnpSchematicOutcome.NotCreated"/> carrying the reason.
    /// </summary>
    /// <param name="faces">The side each port faces, by port number; a port not listed takes
    /// <see cref="FallbackFace"/>.</param>
    public static EmSnpSchematicResult Create(
        string workspaceDir, EmSetup setup, string snpPath, IReadOnlyDictionary<int, EmPortFace> faces)
    {
        string cellName = CellNameFor(setup);
        try
        {
            if (NameValidator.Validate(cellName) is { } bad)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null,
                           $"No schematic was created for this run: '{cellName}' cannot be a cell name. {bad}");

            var snp = RfCore.TouchstoneIO.ReadFile(snpPath, readComments: false);
            int n = snp.Ports;
            if (n < 1)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null,
                           $"No schematic was created for this run: {Path.GetFileName(snpPath)} holds no ports.");

            string cellDir = Path.Combine(workspaceDir, cellName);
            if (Directory.Exists(cellDir) || File.Exists(cellDir))
                return Existing(workspaceDir, cellDir, cellName, snpPath, n);

            string stored = SnpPathPolicy.ToStored(Path.GetFullPath(snpPath), workspaceDir);
            var z = Enumerable.Range(1, n).Select(k => snp.Z0PerPort is { } per && k - 1 < per.Length ? per[k - 1] : snp.Z0).ToArray();
            var model = Build(setup, stored, n, k => faces.TryGetValue(k, out var f) ? f : FallbackFace(k), k => z[k - 1]);

            var created = CellCreate.Create(workspaceDir, cellName, CellViews.Schematic, model);
            string schematic = created.SchematicPath!;
            CellCreate.WriteTemplateDataDisplay(workspaceDir, schematic, null, DataDisplayJson(n, "em_snp.npy"));

            return new(EmSnpSchematicOutcome.Created, cellName, schematic,
                       $"Created cell {cellName}: an S{n}P of this run with {n} TermG, ready to simulate.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or JsonException or FormatException or ArgumentException)
        {
            return new(EmSnpSchematicOutcome.NotCreated, cellName, null,
                       $"No schematic was created for this run: {e.Message}");
        }
    }

    /// <summary>R-dfr31-4-2/3 — a cell of that name exists. It is re-pointed only when its SnP still reads this
    /// setup's results (the same path, under another port count); otherwise nothing in it is touched.</summary>
    private static EmSnpSchematicResult Existing(string workspaceDir, string cellDir, string cellName, string snpPath, int n)
    {
        string? schematic = null;
        if (Directory.Exists(cellDir))
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            if (primary.State is PrimaryState.SoleFile or PrimaryState.NamedPresent && primary.ResolvedName is { } name)
                schematic = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
        }
        if (schematic is null || !File.Exists(schematic))
            return LeftAlone(cellName, null);

        var (model, view, storedCell) = SchematicPersistence.LoadFromFile(schematic);
        string target = Normalise(snpPath);
        string targetStem = StemOf(target);

        EditableComponent? sameStem = null;
        foreach (var c in model.Components.Where(c => c.Symbol == SymbolKind.Snp))
        {
            string? file = c.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression;
            if (SnpPathPolicy.Resolve(file, workspaceDir, Path.GetDirectoryName(schematic)) is not { } abs) continue;
            string norm = Normalise(abs);
            if (string.Equals(norm, target, StringComparison.OrdinalIgnoreCase))
                return new(EmSnpSchematicOutcome.Current, cellName, schematic,
                           $"Cell {cellName} already reads this run's s-parameters, so it was left as it is.");
            if (sameStem is null && string.Equals(StemOf(norm), targetStem, StringComparison.OrdinalIgnoreCase)) sameStem = c;
        }
        if (sameStem is null) return LeftAlone(cellName, schematic);

        string stored = SnpPathPolicy.ToStored(Path.GetFullPath(snpPath), workspaceDir);
        SetParam(sameStem, "File", stored);
        SetParam(sameStem, "NumPorts", n.ToString(CultureInfo.InvariantCulture));
        SchematicPersistence.SaveToFile(schematic, model, storedCell, view.PanX, view.PanY, view.Zoom);
        int terms = model.Components.Count(c => c.Symbol is SymbolKind.TermG or SymbolKind.Term);
        string wiring = terms == n ? "" : $" It has {terms} TermG and the block now has {n} ports, so the wiring needs a look.";
        return new(EmSnpSchematicOutcome.Repointed, cellName, schematic,
                   $"Cell {cellName}'s {sameStem.InstanceName} now reads this run's S{n}P.{wiring}");
    }

    private static EmSnpSchematicResult LeftAlone(string cellName, string? schematic)
        => new(EmSnpSchematicOutcome.LeftAlone, cellName, schematic,
               $"A cell named {cellName} already exists and does not read this run's s-parameters, so it was left alone " +
               "and no schematic was created.");

    private static void SetParam(EditableComponent c, string name, string value)
    {
        if (c.Parameters.FirstOrDefault(p => p.Name == name) is { } p) p.Expression = value;
        else c.Parameters.Add(new EditableParameter { Name = name, Expression = value, ShowOnSchematic = false });
    }

    private static string Normalise(string p)
    {
        try { return Path.GetFullPath(p).Replace('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p.Replace('\\', '/'); }
    }

    /// <summary>A Touchstone path without its <c>.sNp</c>, so an <c>.s2p</c> and an <c>.s4p</c> of one setup compare equal.</summary>
    private static string StemOf(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Length > 2 && ext.StartsWith(".s", StringComparison.OrdinalIgnoreCase)
               && ext.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? path[..^ext.Length] : path;
    }

    // ── The schematic ─────────────────────────────────────────────────────────────────────────

    /// <summary>A port with nothing to say which way it faces: odd ports on the left, even on the right, the way a
    /// two-port's 1 and 2 are drawn.</summary>
    public static EmPortFace FallbackFace(int port) => port % 2 == 1 ? EmPortFace.Left : EmPortFace.Right;

    /// <summary>
    /// The schematic: the SnP at the origin, each TermG on its port's side and turned to face away from the block, the
    /// wires between them drawn by <see cref="SchematicAutoRouter"/> (a connection it cannot draw is made by net label,
    /// which is a real connection), and the S-parameter analysis over the setup's sweep.
    /// </summary>
    public static SchematicEditModel Build(
        EmSetup setup, string storedFile, int n, Func<int, EmPortFace> faceOf, Func<int, Complex> z0Of)
    {
        var model = new SchematicEditModel();

        var config = PinConfigFor(n, faceOf);
        var snp = new EditableComponent { Symbol = SymbolKind.Snp, InstanceName = SnpInstanceName, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, n))
            snp.Parameters.Add(Param(dp, dp.Name switch
            {
                "File"      => storedFile,
                "NumPorts"  => n.ToString(CultureInfo.InvariantCulture),
                "PinConfig" => config.ToString(),
                "Pitch"     => nameof(SnpPitch.Loose),
                _           => dp.Expression,
            }));
        model.Components.Add(snp);

        var pins = SymbolPortDefs.GenerateSnpPorts(n, refNode: false, config, SnpPitch.Loose);
        double minPinY = pins.Min(p => (double)p.LocalY), maxPinY = pins.Max(p => (double)p.LocalY);
        double top = Math.Min(minPinY, -P) - P, bottom = Math.Max(maxPinY, P) + P;
        double gap = 300 + P * n;

        var blocks = new List<SchematicAutoRouter.Block> { SnpBlock(pins) };

        // Per side, the ports in the order that keeps their wires from crossing: walking round the block from the side
        // opposite, so a pin nearer the side lands nearer the middle of it.
        var bySide = Enumerable.Range(1, n).GroupBy(faceOf);
        foreach (var side in bySide)
        {
            var ordered = side.OrderBy(k => SideKey(side.Key, pins[k - 1].LocalX, pins[k - 1].LocalY)).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int port = ordered[i];
                double pitch = side.Key is EmPortFace.Left or EmPortFace.Right ? SidePitch : RowPitch;
                double along = (i - (ordered.Count - 1) / 2.0) * pitch;
                var (pinX, pinY, rot) = side.Key switch
                {
                    EmPortFace.Left   => (-200 - gap, along, SymbolRotation.R90),
                    EmPortFace.Right  => (200 + gap, along, SymbolRotation.R270),
                    EmPortFace.Top    => (along, top - gap, SymbolRotation.R180),
                    _                 => (along, bottom + gap, SymbolRotation.R0),
                };
                // TermG's pin is at local (0, −200): the component sits that far behind its pin, the other way from the block.
                var (ox, oy) = SchematicGeometry.LocalToWorld(0f, -200f, 0, 0, rot, false);
                var term = new EditableComponent
                {
                    Symbol = SymbolKind.TermG, InstanceName = $"Term{port}", X = pinX - ox, Y = pinY - oy, Rotation = rot,
                };
                foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.TermG, 0))
                {
                    var p = Param(dp, dp.Name == "Num" ? port.ToString(CultureInfo.InvariantCulture) : dp.Expression);
                    if (dp.Name == "Z") (p.Expression, p.Unit) = ZText(z0Of(port), dp.Unit);
                    term.Parameters.Add(p);
                }
                model.Components.Add(term);
                blocks.Add(TermBlock(term, $"P{port}"));
            }
        }

        var routed = SchematicAutoRouter.Route(blocks);
        foreach (var path in routed.Wires)
        {
            var wire = new EditableWire();
            foreach (var pt in path) wire.Points.Add(pt);
            model.Wires.Add(wire);
        }
        foreach (var (x, y, net) in routed.Unrouted)
            model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = net });

        // Every segment of the plan, so the schematic sweeps the frequencies the EM run solved (GitHub discussion 26).
        model.Analyses.Add(new SParameterAnalysis(AnalysisName, setup.FrequencyPlan));
        return model;
    }

    /// <summary>
    /// The SnP pin arrangement that puts the most ports' pins on the side their TermG is on, so the fewest wires go round
    /// the block. Standard wins a tie. DualRow (odd ports left, even right) is what an EM run's own numbering usually
    /// wants — an odd port and the even one after it are the two ends of one path.
    /// </summary>
    private static SnpPinConfig PinConfigFor(int n, Func<int, EmPortFace> faceOf)
    {
        if (n < 4) return SnpPinConfig.Standard;
        int Score(SnpPinConfig c) => SymbolPortDefs.GenerateSnpPorts(n, refNode: false, c, SnpPitch.Loose)
            .Select((p, i) => (p.LocalX, Face: faceOf(i + 1)))
            .Count(q => q.LocalX < 0 && q.Face == EmPortFace.Left || q.LocalX > 0 && q.Face == EmPortFace.Right);
        return new[] { SnpPinConfig.Standard, SnpPinConfig.DualRow, SnpPinConfig.SplitLR }
            .Select(c => (Config: c, Score: Score(c))).OrderByDescending(x => x.Score).First().Config;
    }

    /// <summary>
    /// The order of the ports on one side: the angle of each port's PIN round the block, measured from the side
    /// opposite and turning towards this one. A top row read left to right is then the pins met walking clockwise up
    /// the left and over the top, which is the order whose wires do not cross.
    /// </summary>
    private static double SideKey(EmPortFace face, double x, double y)
    {
        double a = Math.Atan2(y, x);   // schematic y grows downward, so this angle turns clockwise on screen
        double key = face switch
        {
            EmPortFace.Top    => a - Math.PI / 2,
            EmPortFace.Bottom => -Math.PI / 2 - a,
            EmPortFace.Left   => -a,
            _                 => a - Math.PI,
        };
        key %= 2 * Math.PI;
        return key < 0 ? key + 2 * Math.PI : key;
    }

    private static EditableParameter Param(DefaultParam dp, string expression) => new()
    {
        Name = dp.Name, Expression = expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
    };

    /// <summary>A real impedance in ohms as the TermG default writes it, a complex one as <c>complex(re,im)</c>.</summary>
    private static (string Expression, string Unit) ZText(Complex z, string ohmUnit)
    {
        string F(double v) => v.ToString("G15", CultureInfo.InvariantCulture);
        return z.Imaginary == 0 ? (F(z.Real), ohmUnit) : ($"complex({F(z.Real)},{F(z.Imaginary)})", "");
    }

    /// <summary>The SnP as the router sees it: its pins, and its body as a keep-out with each pin reachable only along
    /// its own lead.</summary>
    private static SchematicAutoRouter.Block SnpBlock((string Name, float LocalX, float LocalY)[] pins)
    {
        var terminals = pins.Select((p, i) => ((double)p.LocalX, (double)p.LocalY, (string?)$"P{i + 1}")).ToList();
        var exempt = new HashSet<(double, double)>();
        foreach (var p in pins)
        {
            var (ax, ay) = p.LocalX < 0 ? (-1, 0) : p.LocalX > 0 ? (1, 0) : (0, -1);
            exempt.Add((p.LocalX, p.LocalY));
            exempt.Add((p.LocalX + ax * P, p.LocalY + ay * P));
        }
        double minY = pins.Min(p => (double)p.LocalY), maxY = pins.Max(p => (double)p.LocalY);
        var keep = new List<(double, double)>();
        for (double x = -300; x <= 300; x += P)
            for (double y = Math.Min(minY, -P) - P; y <= Math.Max(maxY, P) + P; y += P)
                if (!exempt.Contains((x, y))) keep.Add((x, y));
        return new SchematicAutoRouter.Block(terminals, keep);
    }

    /// <summary>A TermG as the router sees it: its one pin, and its glyph — the lead and the ground bars — as a keep-out.</summary>
    private static SchematicAutoRouter.Block TermBlock(EditableComponent term, string net)
    {
        var (px, py) = SchematicGeometry.LocalToWorld(0f, -200f, term.X, term.Y, term.Rotation, false);
        var keep = new List<(double, double)>();
        for (float lx = -100; lx <= 100; lx += (float)P)
            for (float ly = -100; ly <= 300; ly += (float)P)
                keep.Add(SchematicGeometry.LocalToWorld(lx, ly, term.X, term.Y, term.Rotation, false));
        return new SchematicAutoRouter.Block([(px, py, net)], keep);
    }

    // ── Which way each port faces ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The side each port of a layout faces: the opposite of its direction, which points INTO the metal
    /// (<see cref="LayoutPortDirection"/>'s convention) — a port whose current flows +x̂ sits on the conductor's low-x
    /// end and faces left. A port with no direction, stated or inferred from the artwork, faces away from the middle
    /// of the ports.
    /// </summary>
    public static IReadOnlyDictionary<int, EmPortFace> FacesOf(EmLayoutSource source)
    {
        var shapes = EmComponentPorts.Collect(source).Shapes;
        var numbered = EmPortExtraction.NumberPorts(shapes);
        var lookup = LayoutPortDirection.LookupFor(shapes);
        var faces = new Dictionary<int, EmPortFace>();
        var undirected = new List<(int, double, double)>();
        foreach (var (number, label) in numbered)
        {
            var direction = label.PortDirection ?? LayoutPortDirection.Resolve(lookup, label)?.Direction;
            if (direction is { } d)
                faces[number] = d switch
                {
                    LayoutRotation.R0   => EmPortFace.Left,
                    LayoutRotation.R180 => EmPortFace.Right,
                    LayoutRotation.R90  => EmPortFace.Bottom,   // layout y grows upward: current up, the port on the low end
                    _                   => EmPortFace.Top,
                };
            else undirected.Add((number, label.X, label.Y));
        }
        if (undirected.Count > 0)
        {
            var all = numbered.Select(p => ((double)p.Label.X, (double)p.Label.Y)).ToList();
            foreach (var (number, x, y) in undirected) faces[number] = FromCentre(x, y, all, number);
        }
        return faces;
    }

    /// <summary>The side each port of a 3D view faces: away from the middle of the ports, seen from above.</summary>
    public static IReadOnlyDictionary<int, EmPortFace> FacesOf(C3dDocument document)
    {
        var at = document.Ports.Where(p => p.Model).Select(p => (p.Number, Plan(p))).ToList();
        var all = at.Select(p => p.Item2).ToList();
        var faces = new Dictionary<int, EmPortFace>();
        foreach (var (number, (x, y)) in at) faces.TryAdd(number, FromCentre(x, y, all, number));
        return faces;

        static (double, double) Plan(C3dPort p)
        {
            double u = p.Rect.Min.U + p.Rect.Size.U / 2.0, v = p.Rect.Min.V + p.Rect.Size.V / 2.0;
            return p.Plane switch
            {
                C3dPlane.XY => (u, v),
                C3dPlane.YZ => (p.Offset, u),
                _           => (u, p.Offset),
            };
        }
    }

    /// <summary>The side a point lies on, seen from the middle of all of them, in y-up coordinates; a port at the
    /// middle takes <see cref="FallbackFace"/>.</summary>
    private static EmPortFace FromCentre(double x, double y, IReadOnlyList<(double X, double Y)> all, int port)
    {
        double cx = all.Average(p => p.X), cy = all.Average(p => p.Y);
        double dx = x - cx, dy = y - cy;
        if (dx == 0 && dy == 0) return FallbackFace(port);
        return Math.Abs(dx) >= Math.Abs(dy)
            ? (dx < 0 ? EmPortFace.Left : EmPortFace.Right)
            : (dy > 0 ? EmPortFace.Top : EmPortFace.Bottom);
    }

    // ── The Data Display ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The display the schematic's run opens: dB of the through paths — S(2k, 2k−1), the convention every EM port
    /// numbering here follows (an odd port and the even one after it are the two ends of one path) — and dB of every
    /// input match S(k, k). <paramref name="source"/> is a placeholder; <see cref="CellCreate.WriteTemplateDataDisplay"/>
    /// repoints it to the file the schematic's run writes.
    /// </summary>
    public static string DataDisplayJson(int n, string source)
    {
        var plots = new JsonArray();
        var through = Enumerable.Range(1, n / 2).Select(k => (Out: 2 * k, In: 2 * k - 1)).ToList();
        double topAt = 0;
        if (through.Count > 0)
        {
            plots.Add(Plot("Through paths", topAt, [.. through.Select((t, i) => Trace(source, t.Out, t.In, i))]));
            topAt += 340;
        }
        plots.Add(Plot("Input matches", topAt, [.. Enumerable.Range(1, n).Select(k => Trace(source, k, k, k - 1))]));

        var root = new JsonObject
        {
            ["FormatVersion"] = 2,
            ["SelectedDataSource"] = source,
            ["SourceAliases"] = new JsonObject(),
            ["Tabs"] = new JsonArray(new JsonObject
            {
                ["Name"] = "S-parameters", ["ZoomLevel"] = 1, ["ViewOffsetX"] = 0, ["ViewOffsetY"] = 0, ["Plots"] = plots,
            }),
            ["ActiveTabIndex"] = 0,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        static JsonObject Plot(string title, double top, JsonNode[] traces) => new()
        {
            ["Left"] = 0, ["Top"] = top, ["Width"] = 520, ["Height"] = 320,
            ["PlotType"] = "Rect", ["FreqUnit"] = "GHz",
            ["CustomTitle"] = title, ["CustomTitleOn"] = true,
            ["CustomYLabel"] = "dB", ["CustomYLabelOn"] = true,
            ["Traces"] = new JsonArray(traces),
        };

        static JsonNode Trace(string source, int outPort, int inPort, int colour) => new JsonObject
        {
            ["SourcePath"] = source, ["Row"] = 0, ["Col"] = 0, ["MatrixType"] = "S", ["Derived"] = "None",
            ["InputPort"] = inPort, ["OutputPort"] = outPort, ["YAxis"] = "Db", ["UseSecondaryAxis"] = false,
            ["Z0"] = "50", ["Z0Override"] = false, ["CubeName"] = "S",
            ["CubeSlice"] = new JsonArray(
                Slice("freq", "KeepAsX", 0), Slice("i", "PinToIndex", outPort - 1), Slice("j", "PinToIndex", inPort - 1)),
            ["CubeTransform"] = "dB20", ["Expression"] = null,
            ["Label"] = $"dB(S({outPort},{inPort}))",
            ["Properties"] = new JsonObject
            {
                ["LineEnabled"] = true, ["LineWidth"] = 2, ["LineColorIndex"] = colour, ["LineType"] = "Solid",
                ["MarkerEnabled"] = false, ["MarkerSize"] = 1.5, ["MarkerColorIndex"] = colour, ["MarkerType"] = "Circle",
            },
        };

        static JsonObject Slice(string axis, string role, int index) => new()
        {
            ["AxisName"] = axis, ["Role"] = role, ["Index"] = index, ["RangeStart"] = 0, ["RangeEndExclusive"] = -1, ["Label"] = "",
        };
    }
}
