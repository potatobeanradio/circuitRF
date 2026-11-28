// Owner: PCD-9 (showcase examples). The three showcase cells, each authored as a user authors it in the PCell Designer:
// Create, the netlist text applied, the parameters typed into the table, offsets set on the parts no net joins, Stretch by
// Parameter, Save. The tests, the example authoring and the probe all build through Design.

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.PCellDesigner;

namespace CircuitRF.Ui.Tests.PCellDesigner;

internal sealed record Pcd9Spec(
    string Cell,
    string Text,
    (string Name, string Default, string Unit, string Min, string Max, string Description)[] Parameters,
    (string Instance, OffsetPlacement Offset)[] Offsets,
    (string Instance, int[] Moving, string Parameter, double AxisDeg)[] Stretches,
    string[]? Mirrored = null,
    string? Sweep = null);

internal static class Pcd9Cells
{
    // ══ 1. Wilkinson3: a 3-way Wilkinson divider at 2.45 GHz ══════════════════════════════════════════════════════════
    //
    // The input feeds a cross X1; its middle arm AM runs straight to the tee T2, the outer arms fold once (B1, B3) and run
    // beside it to T1 and T3, 12.4 mm apart. Each tee's branch carries an isolation resistor to the star pad SH1, a drawn
    // pad with Model="short" (one node). A star of three resistors cannot be planar with all three outputs outside it, so
    // R3 is a 1206 whose body bridges the middle arm. Every closing joint of the two loops lands by expression: the outer
    // tees sit 6.2 mm off the axis and 6.2 mm short of T2 (the fold), and the pad's pins are where R1, R2 and R3 end.
    // Lengths in metres where a term mixes units; the outer arms' fold keeps their centreline length La. LQ2 keeps R2's pad
    // off the bend BR2: a pad on the bend's own pin reads to LVS as one copper piece across the bend's two nets.
    public static readonly Pcd9Spec Wilkinson = new(
        "Wilkinson3",
        """
        MLIN:TLIN  n1  nx  W=1.1 mm  L=3 mm
        MCROSS:X1  na  nx  nc  nm  W1=Wa  W2=1.1 mm  W3=Wa  W4=Wa  L1=0.6 mm  L2=0.6 mm  L3=0.6 mm  L4=0.6 mm
        MLIN:AM  nm  e2  W=Wa  L=La
        MLIN:A1V  na  b1  W=Wa  L=5.6e-3-Wa/2
        MBEND:B1  b1  c1  W=Wa  Angle=90 deg
        MLIN:A1H  c1  e1  W=Wa  L=La-5.6e-3-Wa/2
        MLIN:A3V  nc  b3  W=Wa  L=5.6e-3-Wa/2
        MBEND:B3  b3  c3  W=Wa  Angle=90 deg
        MLIN:A3H  c3  e3  W=Wa  L=La-5.6e-3-Wa/2
        MTEE:T1  e1  o1  r1  W1=Wa  W2=1.1 mm  W3=0.8 mm  L1=0.6 mm  L2=0.6 mm  L3=0.6 mm
        MTEE:T2  o2  e2  r2  W1=1.1 mm  W2=Wa  W3=0.8 mm  L1=0.6 mm  L2=0.6 mm  L3=0.6 mm
        MTEE:T3  o3  e3  r3  W1=1.1 mm  W2=Wa  W3=0.8 mm  L1=0.6 mm  L2=0.6 mm  L3=0.6 mm
        MLIN:O1  o1  p2  W=1.1 mm  L=3 mm
        MLIN:O2  o2  p3  W=1.1 mm  L=3 mm
        MLIN:O3  o3  p4  W=1.1 mm  L=3 mm
        MLIN:LR1  r1  q1  W=0.8 mm  L=1.4e-3-Sp
        R:R1  q1  s1  R=Riso  Footprint=smt:0603
        MLIN:LR2  r2  u2  W=0.8 mm  L=0.8e-3+Sp/2
        MBEND:BR2  u2  v2  W=0.8 mm  Angle=90 deg
        MLIN:LQ2  v2  q2  W=0.8 mm  L=0.3 mm
        R:R2  q2  s2  R=Riso  Footprint=smt:0603
        MLIN:LR3  r3  q3  W=0.8 mm  L=3.35 mm
        R:R3  q3  s3  R=Riso  Footprint=smt:1206
        MSHAPE:SH1  s1  s2  s3  Form="polygon"  W=0  X[1]=-3.1e-3-Sp/2  Y[1]=-Sp/2  X[2]=0  Y[2]=-Sp/2  X[3]=0  Y[3]=Sp/2  X[4]=-3.1e-3-Sp/2  Y[4]=Sp/2  PinX[1]=-3.1e-3  PinY[1]=-Sp/2  PinW[1]=0.8 mm  PinDir[1]=270 deg  PinX[2]=0  PinY[2]=0  PinW[2]=0.8 mm  PinDir[2]=0 deg  PinX[3]=-3.1e-3  PinY[3]=Sp/2  PinW[3]=0.8 mm  PinDir[3]=90 deg  Model="short"
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Port:P2  p2  0  Num=2  Z=50 Ohm
        Port:P3  p3  0  Num=3  Z=50 Ohm
        Port:P4  p4  0  Num=4  Z=50 Ohm
        """,
        [
            ("La",   "19.5",  "mm",  "16",   "22",  "arm length"),
            ("Wa",   "0.36",  "mm",  "0.3",  "0.7", "arm width, 86.6 Ω"),
            ("Sp",   "1",     "mm",  "0.6",  "1.2", "star pad size"),
            ("Riso", "50",    "Ohm", "20",   "100", "isolation resistor"),
        ],
        [], [],
        Mirrored: ["B3", "BR2", "SH1"],
        Sweep: "start=1.5 stop=3.5 npts=81 Unit=GHz");

    // ══ 2. Spurline: a spurline bandstop, one drawn polygon ═══════════════════════════════════════════════════════════
    //
    // A 50 Ω line 16 mm long with an L-shaped slit: a slot g wide along the axis from x = 2 mm, turning out through the top
    // edge at its far end, leaves a spur (W − g)/2 wide and Ls long, joined to the line at one end and open at the other. It
    // notches where the spur is a quarter wave. The slit's far end follows Ls by Stretch by Parameter. EM only.
    public static readonly Pcd9Spec Spurline = new(
        "Spurline",
        """
        MLIN:TL1  n1  a  W=W  L=3 mm
        MSHAPE:SH1  a  b  Form="polygon"  W=0  X[1]=0  Y[1]=-W/2  X[2]=16e-3  Y[2]=-W/2  X[3]=16e-3  Y[3]=W/2  X[4]=10.5e-3+g  Y[4]=W/2  X[5]=10.5e-3+g  Y[5]=-g/2  X[6]=2e-3  Y[6]=-g/2  X[7]=2e-3  Y[7]=g/2  X[8]=10.5e-3  Y[8]=g/2  X[9]=10.5e-3  Y[9]=W/2  X[10]=0  Y[10]=W/2  PinX[1]=0  PinY[1]=0  PinW[1]=W  PinDir[1]=180 deg  PinX[2]=16e-3  PinY[2]=0  PinW[2]=W  PinDir[2]=0 deg  Model="none"
        MLIN:TL2  b  n2  W=W  L=3 mm
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Port:P2  n2  0  Num=2  Z=50 Ohm
        """,
        [
            ("Ls", "8.5", "mm", "6",    "12",  "slit (spur) length"),
            ("g",  "0.2", "mm", "0.15", "0.5", "slit gap"),
            ("W",  "1.1", "mm", "0.8",  "1.4", "line width"),
        ],
        [],
        [("SH1", [4, 5, 8, 9], "Ls", 0)]);

    // ══ 3. DualModePatch: a dual-mode square-patch bandpass ═══════════════════════════════════════════════════════════
    //
    // A square patch a wide with its far corner cut by d, which splits its two degenerate modes. Two 50 Ω feeds run along
    // two adjacent edges, the gap s from them, each coupling to one of the modes; the cut corner is on the diagonal between
    // them. Nothing joins them: the patch is placed by offset from the first feed's open end, the second feed by offset from
    // the patch's second pin (Dx, Dy in mm; 0.55 mm is half a feed). The patch's pins join nothing; they are where the feeds
    // face it. EM only.
    public static readonly Pcd9Spec Patch = new(
        "DualModePatch",
        """
        MLIN:TL1  n1  f1  W=1.1 mm  L=a+3e-3
        MSHAPE:SH1  q1  q2  Form="polygon"  W=0  X[1]=0  Y[1]=-a/2  X[2]=a  Y[2]=-a/2  X[3]=a  Y[3]=a/2-d  X[4]=a-d  Y[4]=a/2  X[5]=0  Y[5]=a/2  PinX[1]=0  PinY[1]=0  PinW[1]=1.1 mm  PinDir[1]=180 deg  PinX[2]=a/2  PinY[2]=-a/2  PinW[2]=1.1 mm  PinDir[2]=270 deg  Model="none"
        MLIN:TL2  n2  f2  W=1.1 mm  L=a+3e-3
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Port:P2  n2  0  Num=2  Z=50 Ohm
        """,
        [
            ("a", "15",  "mm", "12",   "18", "patch side"),
            ("d", "3.9", "mm", "0.5",  "5",  "corner cut"),
            ("s", "0.16", "mm", "0.16", "1", "feed gap"),
        ],
        [
            ("SH1", new OffsetPlacement { Anchor = "TL1.2", Pin = 1, Dx = "-a/2", Dy = "s+0.55", Unit = "mm", Angle = "90" }),
            ("TL2", new OffsetPlacement { Anchor = "SH1.2", Pin = 2, Dx = "s+0.55", Dy = "-a/2", Unit = "mm", Angle = "90" }),
        ],
        []);

    // ══ 4. Slotline: an out-of-phase microstrip-slotline divider ══════════════════════════════════════════════════════
    //
    // A slot Ws wide cut in the ground (Bottom Copper), fed at its middle by the input microstrip crossing it, and read at
    // Lsl either side by two output microstrips crossing it. Each crossing is a microstrip-to-slotline transition: the
    // microstrip runs on Lms (a quarter wave) past the slot to an open end, and the slot runs on Lss (a quarter wave)
    // past each output crossing to its shorted end. The slot carries the same voltage both ways from the input, so two
    // outputs crossing it alike would be in phase; TLO3 crosses it the other way round (Angle 180: its port on the input's
    // side, its stub on the other), so the outputs are 180° apart. The ground is one drawn polygon on net 0 with the slot
    // inside it: a polygon has no hole, so its outline runs in to the slot along a zero-width cut (x = 0, below the slot)
    // and back out, and the copper is one piece with one pin (0.55 mm inside it, so LVS finds the copper it is on). It is
    // placed by offset from the input's crossing, and so is each output line (Dy = ±Lsl; Dx, Dy in mm). The ground is
    // drawn copper, so a 3D view of a bench turns it into solids with the slot open. EM only, in 3D: Planar's ground is an
    // unbroken plane.
    public static readonly Pcd9Spec Slotline = new(
        "Slotline",
        """
        MLIN:TLI1  n1  ni  W=1.1 mm  L=12 mm
        MLIN:TLI2  ni  ns1  W=1.1 mm  L=Lms
        MSHAPE:SH1  0  Form="polygon"  W=0  X[1]=-(Lsl+Lss+3e-3)  Y[1]=-12e-3  X[2]=0  Y[2]=-12e-3  X[3]=0  Y[3]=-Ws/2  X[4]=-(Lsl+Lss)  Y[4]=-Ws/2  X[5]=-(Lsl+Lss)  Y[5]=Ws/2  X[6]=Lsl+Lss  Y[6]=Ws/2  X[7]=Lsl+Lss  Y[7]=-Ws/2  X[8]=0  Y[8]=-Ws/2  X[9]=0  Y[9]=-12e-3  X[10]=Lsl+Lss+3e-3  Y[10]=-12e-3  X[11]=Lsl+Lss+3e-3  Y[11]=12e-3  X[12]=-(Lsl+Lss+3e-3)  Y[12]=12e-3  PinX[1]=0  PinY[1]=Ws/2+0.55e-3  PinW[1]=1.1 mm  PinDir[1]=90 deg  SignalLayer="Bottom Copper (1 oz)"  Model="none"
        MLIN:TLO2  n2  no2  W=1.1 mm  L=12 mm
        MLIN:TSO2  no2  ns2  W=1.1 mm  L=Lms
        MLIN:TLO3  n3  no3  W=1.1 mm  L=12 mm
        MLIN:TSO3  no3  ns3  W=1.1 mm  L=Lms
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Port:P2  n2  0  Num=2  Z=50 Ohm
        Port:P3  n3  0  Num=3  Z=50 Ohm
        """,
        [
            ("Lsl", "10",  "mm", "6",   "16",  "slot length, input to each output"),
            ("Ws",  "0.3", "mm", "0.2", "0.8", "slot width"),
            ("Lss", "9.8", "mm", "6",   "14",  "slot stub past each output (a quarter wave)"),
            ("Lms", "8.9", "mm", "6",   "12",  "microstrip stub past the slot (a quarter wave)"),
        ],
        [
            ("SH1",  new OffsetPlacement { Anchor = "TLI1.2", Pin = 1, Dx = "Ws/2+0.55", Dy = "0",    Unit = "mm", Angle = "180" }),
            ("TLO2", new OffsetPlacement { Anchor = "TLI1.2", Pin = 2, Dx = "0",         Dy = "-Lsl", Unit = "mm", Angle = "0" }),
            ("TLO3", new OffsetPlacement { Anchor = "TLI1.2", Pin = 2, Dx = "0",         Dy = "Lsl",  Unit = "mm", Angle = "180" }),
        ],
        []);

    /// <summary>A bench placing <paramref name="cellDir"/> once as X1 between Ports, wired by <paramref name="text"/>, with
    /// one S-parameter sweep (by EM when the cell has no circuit model).</summary>
    public static string Bench(string workspace, string cellDir, string name, string text, FrequencySpec sweep, bool byEm,
                               (string Name, string Value, string Unit)[]? x1 = null, TuningSetup? tuning = null)
    {
        var created = CellCreate.Create(workspace, name, CellViews.Schematic);
        string dir = Path.GetDirectoryName(created.SchematicPath!)!;
        var model = SchematicPersistence.LoadFromFile(created.SchematicPath!).model;
        model.SchematicDirectory = dir;
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, X = 0, Y = 0,
            CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(dir, cellDir)),
        });
        foreach (var (n, v, u) in x1 ?? [])
            model.Components[^1].Parameters.Add(new EditableParameter { Name = n, Expression = v, Unit = u, Dimension = UnitDimension.Length });
        var merged = CellNetlistMerge.Merge(model, text);
        Assert.True(merged.Schematic is not null, string.Join("\n", merged.Refusals));
        var bench = merged.Schematic!;
        bench.Analyses.Add(new SParameterAnalysis("SP1", [sweep]) { Model = byEm ? AnalysisModel.Em : AnalysisModel.Circuit });
        if (tuning is not null) bench.Tuning = tuning;
        SchematicPersistence.SaveToFile(created.SchematicPath!, bench, name);
        return created.SchematicPath!;
    }

    public static string WilkinsonBench(string ws, string cell) => Bench(ws, cell, "Wilkinson3Bench", """
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Wilkinson3:X1  n1  n2  n3  n4
        Port:P2  n2  0  Num=2  Z=50 Ohm
        Port:P3  n3  0  Num=3  Z=50 Ohm
        Port:P4  n4  0  Num=4  Z=50 Ohm
        """, new FrequencySpec("1.5", "3.5", 41, SweepKind.Linear, "GHz", "GHz"), byEm: false);

    /// <summary>The spurline's bench places X1 at the optimised Ls = 9.21 mm (the cell's own default, 8.5 mm, notches at
    /// 5.38 GHz) and carries the optimisation that found it: X1.Ls tuned by EM itself (em=direct, pattern search), S21 at
    /// 5 GHz pushed below −30 dB. The sweep's 25 MHz steps resolve the notch, which is about 50 MHz wide at −20 dB.</summary>
    public static string SpurlineBench(string ws, string cell) => Bench(ws, cell, "SpurlineBench", """
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Spurline:X1  n1  n2  Ls=9.21 mm
        Port:P2  n2  0  Num=2  Z=50 Ohm
        """, new FrequencySpec("4.6", "5.6", 41, SweepKind.Linear, "GHz", "GHz"), byEm: true, x1: [("Ls", "9.21", "mm")],
        tuning: new TuningSetup
        {
            Variables = [new TunableEntry { Key = "X1.Ls", Opt = true, Min = "7 mm", Max = "11 mm", Scale = TuneScale.Lin }],
            Goals = [new OptimizationGoal { Name = "Notch", Expression = "dB(SP1.S(2,1))", Analysis = "SP1", Type = GoalType.Le, Limit = "-30",
                                            Range = new GoalRange { Axis = "freq", Lo = "4.99 GHz", Hi = "5.01 GHz" } }],
            Optimizer = new OptimizerSettings { Algorithm = "pattern", Em = OptimizerEmMode.Direct, EmBudget = 12, Seed = 1 },
        });

    public static string SlotlineBench(string ws, string cell) => Bench(ws, cell, "SlotlineBench", """
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Slotline:X1  n1  n2  n3
        Port:P2  n2  0  Num=2  Z=50 Ohm
        Port:P3  n3  0  Num=3  Z=50 Ohm
        """, new FrequencySpec("4", "6", 11, SweepKind.Linear, "GHz", "GHz"), byEm: true);

    public static string PatchBench(string ws, string cell) => Bench(ws, cell, "PatchBench", """
        Port:P1  n1  0  Num=1  Z=50 Ohm
        DualModePatch:X1  n1  n2
        Port:P2  n2  0  Num=2  Z=50 Ohm
        """, new FrequencySpec("4.9", "5.5", 25, SweepKind.Linear, "GHz", "GHz"), byEm: true);

    /// <summary>The spec designed under <paramref name="workspace"/>, as in the window; the cell folder.</summary>
    public static string Design(string workspace, Pcd9Spec spec)
    {
        var created = PCellDesign.Create(workspace, spec.Cell);
        var vm = new PCellDesignerViewModel(created.CellDir, workspace);
        foreach (var p in spec.Parameters)
        {
            vm.AddParameter();
            var row = vm.Parameters[^1];
            (row.Name, row.Unit, row.Default, row.Minimum, row.Maximum, row.Description) = (p.Name, p.Unit, p.Default, p.Min, p.Max, p.Description);
            row.Dimension = p.Unit switch { "deg" => UnitDimension.Angle, "Ohm" => UnitDimension.Resistance, "" => UnitDimension.None, _ => UnitDimension.Length };
        }
        vm.CnlText = spec.Text;
        Assert.True(vm.ApplyText(), vm.Status);
        {
            // The text view carries no placement (PCD-0 §9): an offset is set on the part, and a part flipped in the drawing
            // (its chirality: which way a bend turns, which side a tee's branch leaves), as the schematic keeps them.
            Assert.Null(vm.Save());
            var (m, v, c) = SchematicPersistence.LoadFromFile(vm.SchematicPath);
            foreach (var (inst, offset) in spec.Offsets) m.Components.Single(k => k.InstanceName == inst).Offset = offset;
            foreach (var k in m.Components) k.MirrorX = spec.Mirrored?.Contains(k.InstanceName) == true;
            SchematicPersistence.SaveToFile(vm.SchematicPath, m, c, v.PanX, v.PanY, v.Zoom);
            vm = new PCellDesignerViewModel(created.CellDir, workspace);
            // A flip moves the part's pins off its wires: Apply the text again, which redraws the wires to its nets and keeps
            // every part's orientation and offset (D-drawing).
            vm.CnlText = spec.Text;
            Assert.True(vm.ApplyText(), vm.Status);
        }
        foreach (var (inst, moving, parameter, axis) in spec.Stretches)
            Assert.True(vm.StretchByParameter(inst, moving, parameter, axis), vm.Status);
        Assert.Null(vm.Save());
        if (spec.Sweep is not null)
        {
            // The live response's sweep, as the cell keeps it (PCD-0 §4.1).
            string ccell = Path.Combine(created.CellDir, CellFolder.CcellFileName);
            var cell = CellFolder.ReadCcell(created.CellDir)!;
            cell.PCellDesign = new CcellPCellDesign { Sweep = spec.Sweep };
            CellPersistence.SaveToFile(ccell, cell);
        }
        return created.CellDir;
    }

    /// <summary>An empty workspace on the example's microstrip board.</summary>
    public static string Workspace(string root) => Pcd8PadTee.Workspace(root);

    // ══ Probe: a spec read from a folder, designed in a scratch workspace, and every pin, plan line and finding written ══

    public sealed class ProbeFactAttribute : FactAttribute
    {
        public ProbeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_PCD9_PROBE") is not { Length: > 0 })
                Skip = "a design probe, run only with CRF_PCD9_PROBE=<folder>";
        }
    }
}

public sealed class Pcd9Probe
{
    /// <summary>CRF_PCD9_PROBE=<folder> holding cell.cnl, params.txt (name|default|unit|min|max) and offsets.txt
    /// (instance anchor pin dx dy unit angle); writes out.txt there.</summary>
    [Pcd9Cells.ProbeFact]
    public void Probe()
    {
        string dir = Environment.GetEnvironmentVariable("CRF_PCD9_PROBE")!;
        string Read(string f) => File.Exists(Path.Combine(dir, f)) ? File.ReadAllText(Path.Combine(dir, f)) : "";
        string[] Lines(string f) => [.. Read(f).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
        var spec = new Pcd9Spec(
            "Probe",
            Read("cell.cnl"),
            [.. Lines("params.txt").Select(l => l.Split('|')).Select(a => (a[0], a[1], a[2], a[3], a[4], ""))],
            [.. Lines("offsets.txt").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(a => (a[0], new OffsetPlacement
                { Anchor = a[1], Pin = int.Parse(a[2]), Dx = a[3], Dy = a[4], Unit = a[5], Angle = a[6] }))],
            [.. Lines("stretch.txt").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(a => (a[0], a[1].Split(',').Select(int.Parse).ToArray(), a[2], double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture)))],
            Lines("mirror.txt"));
        var o = new System.Text.StringBuilder();
        string ws = Pcd9Cells.Workspace(Path.Combine(dir, "ws"));
        if (Directory.Exists(Path.Combine(ws, "Probe"))) Directory.Delete(Path.Combine(ws, "Probe"), true);
        try
        {
            string cell = Pcd9Cells.Design(ws, spec);
            foreach (var f in PCellDesignCheck.Run(cell)) o.AppendLine($"check {f.Severity} {f.Id}: {f.Render()}");
            var vm = new PCellDesignerViewModel(cell, ws);
            vm.DesignAsync(0).GetAwaiter().GetResult();
            o.AppendLine("note: " + vm.LayoutNote);
            var tech = vm.Technology!;
            var at = DesignedCellLayout.At(cell, new Dictionary<string, CircuitRF.Core.Expressions.Value>(), ws, tech, vm.TechIdentity);
            foreach (var l in at.Lines) o.AppendLine($"plan {l.Severity} {l.Text}");
            double um = vm.Layout!.DbuPerMicron;
            foreach (var i in vm.Layout.Instances.Where(i => i.SchematicId is { Length: > 0 }).OrderBy(i => i.SchematicId))
                o.AppendLine($"inst {i.SchematicId} x={i.X / um:F3} y={i.Y / um:F3} rot={i.RotationDegrees:F1} mirror={i.MirrorX}");
            foreach (var (inst, pin, pose) in vm.Pins().OrderBy(p => p.Instance).ThenBy(p => p.Pin))
                o.AppendLine($"pin {inst}.{pin} x={pose.X / um:F3} y={pose.Y / um:F3} um out={pose.OutwardDeg:F2}");
            var lvsDir = cell;
            var lvs = CircuitRF.Design.Layout.Lvs.LvsRun.Run(lvsDir);
            o.AppendLine($"lvs errors={lvs.ErrorCount}");
            foreach (var f in lvs.Findings) o.AppendLine("lvs " + f.Diagnostic);
        }
        catch (Exception e) { o.AppendLine("EXCEPTION " + e); }
        if (Read("analysis.txt").Length > 0)   // the circuit model: vars.txt (globals), the cell's text, the analysis
        {
            var r = CircuitRF.Design.Circuit.CircuitEvaluation.Evaluate(
                CircuitRF.Design.Circuit.PreparedCircuit.FromText(Read("vars.txt") + "\n" + Read("cell.cnl") + "\n" + Read("analysis.txt"), null, null),
                new CircuitRF.Design.Circuit.CircuitEvaluationRequest { Em = false });
            o.AppendLine($"sim {r.Status} {r.StatusMessage}");
            if (r.Status == CircuitRF.Design.Circuit.RunStatus.Success)
            {
                var snp = RfCore.Data.DataSetBuilder.ToSnp(r.Results.First().Data);
                for (int k = 0; k < snp.Frequencies.Length; k++)
                {
                    o.Append($"f={snp.Frequencies[k] / 1e9:F3}");
                    var m = snp.Matrices[k];
                    for (int i = 0; i < m.RowCount; i++)
                        for (int j = 0; j <= i; j++)
                            o.Append($" S{i + 1}{j + 1}={20 * Math.Log10(m[i, j].Magnitude):F2}dB/{m[i, j].Phase * 180 / Math.PI:F0}");
                    o.AppendLine();
                }
            }
        }
        File.WriteAllText(Path.Combine(dir, "out.txt"), o.ToString());
    }
}
