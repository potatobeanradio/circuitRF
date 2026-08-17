using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-wbond-controlling-parameters (WB-G) — <b>the handles a placed wBond exposes to a sweep or an
/// optimiser</b> (<c>wbond.md</c> §5.5.1/WB44, O-10/O-11), and <b>where a placed instance's wires
/// actually come from</b> (§9.7/WB45, O-12).
///
/// <para>Before this phase a placed wBond exposed <c>Design</c>, <c>Arrays</c>, <c>SymbolPitch</c> and
/// <c>RefPin</c> — not one of them a physical quantity, so there was no handle to turn. The ENGINE half
/// already honoured <c>Temp</c>, <c>GroundPlane</c> and <c>LoopHeight</c>; nothing offered them.</para>
/// </summary>
public sealed class WBondControllingParametersTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// The measured numbers are REPORTED, not only asserted on — §6 of the brief asks for the two
    /// gate-2 inductances by name, and a number that only appears in a failure message is a number
    /// nobody has.
    /// </summary>
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public WBondControllingParametersTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), $"wbond-wbg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        WBondSymbolProvider.InvalidateAll();
    }

    public void Dispose()
    {
        WBondSymbolProvider.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private const double MilM = 2.54e-5;

    /// <summary>
    /// Arrays whose wires are BOUND to <b>one shared</b> loop profile — which is the configuration
    /// §2.1's clone-on-write exists for, and the one a naive per-array override silently gets wrong.
    /// The wires rise above the plane, because a wire lying flat in it has zero loop inductance and the
    /// array reduction is then singular.
    /// </summary>
    private static WBondDesign SharedProfileDesign(double loopHeightMil, params string[] arrayNames)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(loopHeightMil, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        double y = 0;
        foreach (string name in arrayNames)
        {
            var array = new WireArray { Name = name, Profile = profile.Name };
            for (int i = 0; i < 2; i++, y += 6.0)
                array.Wires.Add(profile.CreateWire(
                    Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
            design.Arrays.Add(array);
            y += 20.0;
        }
        return design;
    }

    private static Dictionary<string, Value> Params(params (string Name, object Value)[] entries)
    {
        var d = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var (name, value) in entries)
            d[name] = value switch
            {
                double r => new Value(r),
                string s => new Value(s),
                bool b   => new Value(b),
                _        => throw new ArgumentException($"unhandled fixture value {value}"),
            };
        return d;
    }

    /// <summary>The design as the factory reshaped it — the controlling-parameter layer's own output.</summary>
    private static WBondDesign Elaborate(WBondDesign design, params (string Name, object Value)[] overrides)
    {
        var p = Params(overrides);
        p["Design"] = new Value(WBondEmbedding.Encode(design));

        var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", p)!;
        return model.Design;
    }

    private static long HeightOf(WBondDesign design, string arrayName, int wire) =>
        design.Arrays.First(a => a.Name == arrayName).Wires[wire].LoopHeightNm;

    // ── The run path ──────────────────────────────────────────────────────────

    private SchematicEditModel NewSchematic(string cellName = "Amp")
    {
        string dir = Path.Combine(_root, cellName, "schematic");
        Directory.CreateDirectory(dir);
        return new SchematicEditModel { SchematicDirectory = dir };
    }

    private RunResult RunPlaced(SchematicEditModel model)
    {
        var result = NetExtractor.Extract(model, "tb");
        Assert.Empty(result.Conflicts);

        string cnlPath = Path.Combine(_root, "netlist.cnl");
        File.WriteAllText(cnlPath, CnlWriter.Write(result.TestBench, result.Library));

        return SchematicRunService.RunNetlist(cnlPath, baseDirectory: _root);
    }

    /// <summary>Two Terms across the FIRST array, REF grounded — the smallest thing that solves.</summary>
    private SchematicEditModel Testbench(WBondDesign design, params Analysis[] analyses)
    {
        var model = NewSchematic();

        var comp = WBondPlacement.BuildCarrying(design, "W1");
        comp.X = 0; comp.Y = 0;
        comp.Parameters.First(p => p.Name == "RefPin").Expression = "true";
        model.Components.Add(comp);

        var (render, _) = model.BuildRenderModel();
        var ports = render.Components.Single().Ports;

        (double X, double Y) World(int i) => SchematicGeometry.LocalToWorld(
            ports[i].LocalX, ports[i].LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);

        var (ix, iy) = World(0);
        var t1 = new EditableComponent { InstanceName = "T1", Symbol = SymbolKind.Term, X = ix, Y = iy + 200 };
        t1.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" });
        t1.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50" });

        var (ox, oy) = World(1);
        var t2 = new EditableComponent { InstanceName = "T2", Symbol = SymbolKind.Term, X = ox, Y = oy + 200 };
        t2.Parameters.Add(new EditableParameter { Name = "Num", Expression = "2" });
        t2.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50" });

        model.Components.Add(t1);
        model.Components.Add(t2);

        // Every remaining pin — the other arrays' terminals and REF — goes to ground, so the netlist
        // is complete however many arrays the fixture declares.
        for (int i = 2; i < ports.Count; i++)
        {
            var (px, py) = World(i);
            model.Components.Add(new EditableComponent
                { InstanceName = $"GND_P{i}", Symbol = SymbolKind.Ground, X = px, Y = py });
        }

        foreach (var t in new[] { t1, t2 })
            model.Components.Add(new EditableComponent
                { InstanceName = $"GND_{t.InstanceName}", Symbol = SymbolKind.Ground, X = t.X, Y = t.Y + 200 });

        foreach (var a in analyses) model.Analyses.Add(a);
        return model;
    }

    private static SParameterAnalysis SpAt5GHz() =>
        new("SP1", new FrequencySpec("5", "5", "1", SweepKind.Linear, "GHz", "GHz", "GHz"));

    /// <summary>
    /// The effective series inductance a two-port series element presents, recovered from its own
    /// published <c>S21</c>: <c>Z = 2·Z0·(1/S21 − 1)</c>, <c>L = Im(Z)/ω</c>. Derived from the
    /// definition rather than read out of the model, so it measures what the run actually published.
    /// </summary>
    private static double SeriesL(Complex s21, double freqHz, double z0 = 50.0) =>
        (2 * z0 * (Complex.One / s21 - Complex.One)).Imaginary / (2 * Math.PI * freqHz);

    /// <summary>…and its series resistance, the same way.</summary>
    private static double SeriesR(Complex s21, double z0 = 50.0) =>
        (2 * z0 * (Complex.One / s21 - Complex.One)).Real;

    private static Complex S21Of(RunResult run)
    {
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var s = Assert.Single(run.DataSets)["S"];
        int nf = s.Axes[0].Values.Length;
        int np = s.Axes[1].Values.Length;
        return s.ComplexValues[((0 * nf + 0) * np + 1) * np + 0];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gate 1 — nothing changes for an existing design
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1 — the one that catches a leaked default (§2.2).</b> A placed wBond with no controlling
    /// parameter SET produces bit-identical S-parameters whether the parameters are declared-but-blank
    /// (what ships now) or absent entirely (every schematic written before this phase).
    ///
    /// <para>A wBond shipping <c>LoopHeight = 20 mil</c> among its DEFAULTS would silently regenerate
    /// every existing design's wires to 20 mil on its next run — a wrong answer that converges, plots
    /// and looks entirely reasonable. That is why the declaration is the dangerous half of M1 and this
    /// is the gate that guards it.</para>
    /// </summary>
    [Fact]
    public void Gate1_AnExistingDesign_AnswersBitIdentically_WithTheParametersDeclaredButUnset()
    {
        var withDeclarations = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());

        // The same schematic as it would have been written BEFORE this phase: every controlling
        // parameter removed from the instance rather than merely left blank.
        var asBefore = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
        var legacy = asBefore.Components.First(c => c.Symbol == SymbolKind.WBond);
        foreach (string name in new[] { "LoopHeight", "Diameter", "Material", "Source", "File" })
            legacy.Parameters.Remove(legacy.Parameters.First(p => p.Name == name));

        var now = S21Of(RunPlaced(withDeclarations));
        var before = S21Of(RunPlaced(asBefore));

        // Bit-identical, not "close": a leaked default moves the answer by percent, and a tolerance
        // here would be a place for one to hide.
        Assert.Equal(before.Real, now.Real);
        Assert.Equal(before.Imaginary, now.Imaginary);
    }

    /// <summary>
    /// The same claim at the layer the leak would happen in: with every controlling parameter absent,
    /// the decoded design is untouched — same loop height, same diameter, same metal, same profile
    /// list (no clone), same z-coordinates.
    /// </summary>
    [Fact]
    public void Gate1_WithNoControllingParameterSet_TheDecodedDesignIsUntouched()
    {
        var original = SharedProfileDesign(20.0, "G1", "G2");
        var after = Elaborate(SharedProfileDesign(20.0, "G1", "G2"));

        Assert.Equal(original.Profiles.Count, after.Profiles.Count);

        foreach (var array in original.Arrays)
            for (int w = 0; w < array.Wires.Count; w++)
            {
                var a = array.Wires[w];
                var b = after.Arrays.First(x => x.Name == array.Name).Wires[w];

                Assert.Equal(a.DiameterNm, b.DiameterNm);
                Assert.Equal(a.Material, b.Material);
                Assert.Equal(a.Points, b.Points);
            }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gate 2 — a loop-height sweep runs from a PLACED component, in mil, and moves
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 2.</b> The controlling parameter is a real handle: two distinct loop heights give two
    /// distinct inductances, driven from a placed component through the product path.
    ///
    /// <para>A FLAT curve is the exact failure mode §1 warns about, and it has been real once — before
    /// 2026-08-07 a length-dimensioned global could not be swept at all (the unit table had no symbol
    /// for the metre, <c>"m"</c> being the SI prefix <i>milli</i>), so a mil-declared loop-height sweep
    /// clamped to the wire's own foot drop and drew a perfectly plausible flat curve. <b>If a length
    /// sweep looks flat now, it is this phase's bug and not the units table's.</b></para>
    ///
    /// <para>The two measured inductances are REPORTED in the assertion messages, which is what
    /// §6 asks be recorded.</para>
    /// </summary>
    [Fact]
    public void Gate2_ALoopHeightSweepFromAPlacedComponent_MovesTheInductance()
    {
        double At(double mils)
        {
            var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
            var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);
            comp.Parameters.First(p => p.Name == "LoopHeight").Expression =
                mils.ToString("G17", CultureInfo.InvariantCulture);
            comp.Parameters.First(p => p.Name == "LoopHeight").Unit = "mil";

            return SeriesL(S21Of(RunPlaced(model)), 5e9);
        }

        double l10 = At(10.0);
        double l45 = At(45.0);

        _output.WriteLine($"gate 2: LoopHeight 10 mil → {l10 * 1e12:F1} pH, 45 mil → {l45 * 1e12:F1} pH");

        Assert.True(l45 > l10 * 1.2,
            $"a 45 mil loop must be materially more inductive than a 10 mil one; got " +
            $"{l45 * 1e12:F1} pH against {l10 * 1e12:F1} pH.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gate 3 — a per-array override reaches ONE array only
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 3 — §2.1's clone-on-write, and the oracle is G2's WIRE GEOMETRY, not a message.</b>
    ///
    /// <para>Two arrays share one <c>LoopProfile</c>. <c>LoopHeight_G1</c> must give G1 its own copy
    /// rather than dragging G2 with it: cloning is free (the override is per-elaboration and never
    /// persisted) and skipping it produces a wrong answer that looks right — G2's wires quietly
    /// regenerated to a height nobody asked for, with every number still finite and plausible.</para>
    /// </summary>
    [Fact]
    public void Gate3_APerArrayLoopHeight_ReachesThatArrayOnly()
    {
        var untouched = SharedProfileDesign(20.0, "G1", "G2");
        var after = Elaborate(SharedProfileDesign(20.0, "G1", "G2"),
            ("LoopHeight_G1", 40.0 * MilM));

        // G1 moved to 40 mil…
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), HeightOf(after, "G1", 0));
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), HeightOf(after, "G1", 1));

        // …and G2 is untouched, asserted on the z-coordinates themselves rather than on the derived
        // loop height, so a regeneration that happened to land on the same measured height would
        // still be caught.
        for (int w = 0; w < 2; w++)
            Assert.Equal(untouched.Arrays[1].Wires[w].Points, after.Arrays[1].Wires[w].Points);
    }

    /// <summary>
    /// The mechanism behind gate 3, stated on its own so a regression names itself — and it is
    /// <b>the opposite of what it used to be</b>.
    ///
    /// <para>A per-array override once wrote the bound <c>LoopProfile</c>'s height and regenerated the
    /// wires from it, which meant a profile shared by two arrays had to be CLONED so overriding one did
    /// not drag the other. Since the owner's 2026-08-17 decision — <i>"the ball/wedge profile setting
    /// should never affect the geometry that the user authors"</i> — a loop height rescales each wire's
    /// own rise instead, so <b>no profile is written at all</b> and there is nothing for a shared one to
    /// be dragged away from. The clone is gone, and its absence is the assertion.</para>
    /// </summary>
    [Fact]
    public void APerArrayLoopHeight_WritesNoProfileAtAll()
    {
        var untouched = SharedProfileDesign(20.0, "G1", "G2");
        var after = Elaborate(SharedProfileDesign(20.0, "G1", "G2"),
            ("LoopHeight_G1", 40.0 * MilM));

        // One profile, still named and still stating what it always stated.
        Assert.Single(after.Profiles);
        Assert.Equal(untouched.Profiles[0].Name, after.Profiles[0].Name);
        Assert.Equal(untouched.Profiles[0].LoopHeightNm, after.Profiles[0].LoopHeightNm);

        // Both arrays keep their binding — nothing was re-pointed at a copy.
        foreach (var array in after.Arrays)
            foreach (var wire in array.Wires)
                Assert.Equal(untouched.Profiles[0].Name, wire.ProfileBinding);
    }

    /// <summary>
    /// A GLOBAL <c>LoopHeight</c> needs no clone — every array is being set to the same value, so there
    /// is nothing for a shared profile to be dragged away from. Cloning anyway would work and would
    /// leave a design carrying one profile per array for no reason.
    /// </summary>
    [Fact]
    public void AGlobalLoopHeight_ReachesEveryArray_WithoutCloning()
    {
        var after = Elaborate(SharedProfileDesign(20.0, "G1", "G2"), ("LoopHeight", 40.0 * MilM));

        Assert.Single(after.Profiles);
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), HeightOf(after, "G1", 0));
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), HeightOf(after, "G2", 0));
    }

    /// <summary>
    /// §2.1 — <c>LoopHeight_&lt;profile&gt;</c> keeps working for a hand-authored <c>.cnl</c>, and a
    /// name that is BOTH an array and a profile resolves as the ARRAY, with the collision reported.
    /// The schematic user's namespace is the one on the symbol: array names ARE the pin names, and a
    /// <c>LoopProfile</c> is an editor-internal sharing mechanism they never see.
    /// </summary>
    [Fact]
    public void TheProfileSpelling_StillResolves_AndAnArrayOfTheSameNameWins()
    {
        // The profile is named "ball"; no array is. The legacy spelling must still reach it.
        var byProfile = Elaborate(SharedProfileDesign(20.0, "G1"), ("LoopHeight_ball", 40.0 * MilM));
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), HeightOf(byProfile, "G1", 0));

        // Now make the collision real: one array named exactly what the profile is named.
        var collided = SharedProfileDesign(20.0, "ball");
        var after = Elaborate(collided, ("LoopHeight_ball", 40.0 * MilM));
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), HeightOf(after, "ball", 0));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gate 3a — a detached wire is skipped, and SAID to be skipped
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The inverse of what gate 3a used to assert, by owner decision (2026-08-17).</b>
    ///
    /// <para>A wire dragged loose from its profile once kept its drawn loop height and the run reported
    /// how many had been skipped (§2.0). That asymmetry existed only because the override worked by
    /// re-generating wires FROM the profile, which a detached wire is by definition not following. Now
    /// that a loop height rescales the wire's own rise, <b>there is no such thing as a wire the
    /// parameter cannot reach</b>: loop height is a property of the wire —
    /// <c>Wire.LoopHeightNm</c> is defined as its own max z minus min z — not of its generator.</para>
    ///
    /// <para>So both wires land on the requested height, the §2.0 report is retired, and the confusing
    /// "two wires in the same array respond differently to the same parameter" case is gone rather than
    /// merely explained.</para>
    /// </summary>
    [Fact]
    public void ADetachedWire_IsReachedToo_AndNothingIsReported()
    {
        var design = SharedProfileDesign(20.0, "G1");
        design.Arrays[0].Wires[1].ProfileBinding = null;

        var p = Params(("LoopHeight_G1", 40.0 * MilM));
        p["Design"] = new Value(WBondEmbedding.Encode(design));

        var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", p)!;

        foreach (var wire in model.Design.Arrays[0].Wires)
            Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), wire.LoopHeightNm);

        Assert.Null(model.Design.Arrays[0].Wires[1].ProfileBinding);   // still detached, still reached
        Assert.Empty(DrainOf(model));
    }

    /// <summary>
    /// <b>The authored path survives — the owner's own requirement.</b> A wire routed by hand around
    /// something keeps every X and Y it was given; only the z rise is rescaled, and both feet stay
    /// bit-exact.
    ///
    /// <para>The fixture is the shape from the owner's own workspace: a wire whose interior points
    /// wander well off the straight line between its feet. Applying a loop height through
    /// <c>LoopProfile.ApplyTo</c> — which writes X and Y by linear interpolation — would have come back
    /// as a plain planar arc and thrown that routing away.</para>
    /// </summary>
    [Fact]
    public void ALoopHeightOverride_KeepsEveryXAndY_AndBothFeet()
    {
        var design = SharedProfileDesign(20.0, "G1");
        var wire = design.Arrays[0].Wires[0];

        // Route it by hand, off the chord in XY.
        wire.Points[2] = wire.Points[2] with { X = wire.Points[2].X - WBondUnits.ToNm(8, WBondUnit.Mil) };
        wire.Points[3] = wire.Points[3] with { X = wire.Points[3].X + WBondUnits.ToNm(12, WBondUnit.Mil) };

        var authored = wire.Points.Select(pt => (pt.X, pt.Y)).ToList();
        var feet = (First: wire.Points[0], Last: wire.Points[^1]);

        var after = Elaborate(design, ("LoopHeight_G1", 40.0 * MilM));
        var moved = after.Arrays[0].Wires[0];

        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), moved.LoopHeightNm);
        Assert.Equal(authored, moved.Points.Select(pt => (pt.X, pt.Y)).ToList());
        Assert.Equal(feet.First, moved.Points[0]);
        Assert.Equal(feet.Last, moved.Points[^1]);
    }

    /// <summary>
    /// The first row of §2.0's precedence table, which is right and needs nothing: <b>a foot the user
    /// moved in the layout survives a schematic override</b>, because the override regenerates BETWEEN
    /// the feet it finds and never moves them.
    /// </summary>
    [Fact]
    public void AMovedFoot_SurvivesALoopHeightOverride()
    {
        var design = SharedProfileDesign(20.0, "G1");
        var moved = Point3.Mils(140, 3, 7);
        design.Arrays[0].Wires[0].Points[^1] = moved;

        var after = Elaborate(design, ("LoopHeight_G1", 40.0 * MilM));

        var wire = after.Arrays[0].Wires[0];
        Assert.Equal(moved, wire.Points[^1]);
        Assert.Equal(WBondUnits.ToNm(40.0, WBondUnit.Mil), wire.LoopHeightNm);
    }

    private static IReadOnlyList<string> DrainOf(WBondModel model)
    {
        // The notes are phrased with the instance path at the first Stamp — the first moment an
        // ElaboratedComponent is in hand — so a bare Create has nothing queued yet. This drives the
        // same path the engine does, without a solve.
        var ec = new CircuitRF.Core.Elaboration.ElaboratedComponent(
            "wBond", "W1", Enumerable.Repeat(0, model.PortCount).ToArray(),
            new Dictionary<string, Value>(), model);

        model.Stamp(new CountingMna(), ec, 2 * Math.PI * 5e9);
        return [.. model.DrainWarnings().Select(w => w.Message)];
    }

    /// <summary>An MNA context that records nothing — the notes are queued during Stamp, not by it.</summary>
    private sealed class CountingMna : CircuitRF.Core.IMnaContext
    {
        private int _branches;

        public void AddAdmittance(int nodeA, int nodeB, Complex y) { }
        public void AddBlockAdmittance(int rowNode, int colNode, Complex y) { }
        public int AddBranch() => _branches++;
        public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo) { }
        public void AddConstraint(int branch, int node, Complex coeff) { }
        public void AddNodeBranchCoupling(int node, int branch, Complex coeff) { }
        public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) { }
        public void AddCurrentInjection(int node, Complex j) { }
        public void AddSourceValue(int branch, Complex value) { }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gate 4 — diameter and material change the answer in the right direction
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4, first half.</b> A thicker wire is LESS inductive at the same geometry — the
    /// self-inductance term carries <c>ln(2ℓ/r)</c>, so raising <c>r</c> lowers it.
    /// </summary>
    [Fact]
    public void Gate4_AThickerWire_IsLessInductive()
    {
        double At(double diameterMils)
        {
            var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
            var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);
            var d = comp.Parameters.First(p => p.Name == "Diameter");
            d.Expression = diameterMils.ToString("G17", CultureInfo.InvariantCulture);
            d.Unit = "mil";

            return SeriesL(S21Of(RunPlaced(model)), 5e9);
        }

        double thin = At(0.7);
        double thick = At(2.0);

        _output.WriteLine($"gate 4a: Diameter 0.7 mil → {thin * 1e12:F1} pH, 2.0 mil → {thick * 1e12:F1} pH");

        Assert.True(thick < thin,
            $"a 2 mil wire must be less inductive than a 0.7 mil one; got {thick * 1e12:F1} pH " +
            $"against {thin * 1e12:F1} pH.");
    }

    /// <summary>
    /// <b>Gate 4, second half — the loss check, cross-referenced against <c>InternalImpedance</c>.</b>
    ///
    /// <para>Aluminium's conductivity is 3.77e7 S/m against gold's 4.10e7, so the same geometry in
    /// aluminium is more resistive. The comparison is on the run's own published <b>R</b>, at 5 GHz
    /// where the skin-effect tier is active — asserting only on <c>|S21|</c> would let an inductance
    /// change masquerade as a loss change.</para>
    /// </summary>
    [Fact]
    public void Gate4_AluminiumIsMoreLossyThanGold_AtTheSameGeometry()
    {
        double At(string material)
        {
            var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
            var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);
            comp.Parameters.First(p => p.Name == "Material").Expression = material;

            return SeriesR(S21Of(RunPlaced(model)));
        }

        double gold = At("Gold");
        double aluminium = At("Aluminium");

        _output.WriteLine($"gate 4b: Material Gold → {gold * 1e3:F3} mΩ, Aluminium → {aluminium * 1e3:F3} mΩ");

        Assert.True(aluminium > gold,
            $"aluminium must be more resistive than gold at the same geometry; got " +
            $"{aluminium * 1e3:F3} mΩ against {gold * 1e3:F3} mΩ.");

        // Cross-checked against InternalImpedance at the SAME frequency, where the R-tier is active —
        // the brief asks for this rather than a bare |S21| comparison, which an inductance change
        // could masquerade as. Both metals at the fixture's own 0.5 mil radius and 85 °C default.
        double radiusM = WBondUnits.ToMetres(WBondUnits.ToNm(0.5, WBondUnit.Mil));
        double tempC = WireMaterials.DefaultOperatingTempC;

        var (rAu, _) = InternalImpedance.PerMetre(5e9, radiusM, WireMaterials.Gold.SigmaAt(tempC));
        var (rAl, _) = InternalImpedance.PerMetre(5e9, radiusM, WireMaterials.Aluminium.SigmaAt(tempC));

        Assert.True(rAl > rAu,
            $"InternalImpedance must agree: Al {rAl:G4} Ω/m against Au {rAu:G4} Ω/m.");

        // The skin-effect tier is genuinely engaged at this frequency — q ≫ 1 — so this is not a
        // DC-resistance comparison wearing a 5 GHz label.
        Assert.True(InternalImpedance.QParameter(5e9, radiusM, WireMaterials.Gold.SigmaAt(tempC)) > 3.0,
            "the R-tier must be skin-effect-active at the frequency this gate measures at.");
    }

    /// <summary>
    /// §5's owner question, answered the way the brief proposes: an unknown metal is <b>refused by
    /// name at elaboration</b> rather than restricting the dropdown to the built-in four.
    /// <c>WBondDesign.Materials</c> is user-extensible, so a design's own metal must stay nameable
    /// from the schematic — and a typo falling back to gold is a wrong answer that looks right.
    /// </summary>
    [Fact]
    public void AnUnknownMaterial_IsRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Elaborate(SharedProfileDesign(20.0, "G1"), ("Material", "Unobtainium")));

        Assert.Contains("Unobtainium", ex.Message);
        Assert.Contains("Gold", ex.Message);   // names what IS available
    }

    /// <summary>
    /// <b>Unset must be distinct from zero (§2.3).</b> An empty field means "as drawn"; <c>0</c> is a
    /// mistake worth naming, for diameter exactly as for loop height. A zero-diameter wire is not a
    /// wire, and a zero loop height is a wire flattened onto the ground plane.
    /// </summary>
    [Theory]
    [InlineData("LoopHeight")]
    [InlineData("Diameter")]
    [InlineData("LoopHeight_G1")]
    [InlineData("Diameter_G1")]
    public void ZeroIsRefused_AndTheMessageSaysBlankMeansAsDrawn(string parameter)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Elaborate(SharedProfileDesign(20.0, "G1"), (parameter, 0.0)));

        Assert.Contains("positive", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Diameter and material reach a DETACHED wire, unlike loop height — and that asymmetry is
    /// deliberate rather than an oversight. Detachment (WB2/WB24) is about the loop SHAPE: a wire
    /// dragged loose from its profile still has a diameter and a metal, and there is nothing to
    /// regenerate to apply them.
    /// </summary>
    [Fact]
    public void DiameterAndMaterial_ReachADetachedWire()
    {
        var design = SharedProfileDesign(20.0, "G1");
        design.Arrays[0].Wires[1].ProfileBinding = null;

        var after = Elaborate(design, ("Diameter_G1", 2.0 * MilM), ("Material_G1", "Aluminium"));

        foreach (var wire in after.Arrays[0].Wires)
        {
            Assert.Equal(WBondUnits.ToNm(2.0, WBondUnit.Mil), wire.DiameterNm);
            Assert.Equal("Aluminium", wire.Material);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gate 5 — a sweep mutates nothing
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5 — WB44 property 1, which is the property the whole design rests on.</b> After a
    /// 5-point loop-height sweep the instance's <c>Design</c> payload is byte-identical to what it was
    /// before. A controlling parameter is an override layer applied to the DECODED design on its way
    /// to the solver, never an edit — which is also why these survive §9.6's Layout → Schematic, whose
    /// replacement of the base geometry the override still sits on top of.
    /// </summary>
    [Fact]
    public void Gate5_AFivePointSweep_MutatesTheStoredDesignZeroTimes()
    {
        var sweep = new ParametricSweepAnalysis("SW1", "loopH",
            new SweepSpec(10, 45, 5, SweepAxisMode.PointCount, SweepKind.Linear, "mil"), "SP1");

        var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz(), sweep);

        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);
        comp.Parameters.First(p => p.Name == "LoopHeight").Expression = "loopH";

        var varComp = new EditableComponent
            { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = -800, Y = -800 };
        varComp.Parameters.Add(new EditableParameter
            { Name = "loopH", Expression = "10", Unit = "mil" });
        model.Components.Add(varComp);

        string before = comp.Parameters.First(p => p.Name == "Design").Expression;

        var run = RunPlaced(model);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);

        string after = comp.Parameters.First(p => p.Name == "Design").Expression;
        Assert.Equal(before, after);

        // …and the sweep genuinely regenerated geometry rather than drawing a flat curve.
        var s = Assert.Single(run.DataSets)["S"];
        Assert.Equal(5, s.Axes[0].Values.Length);

        int nFreq = s.Axes[1].Values.Length;
        int nPort = s.Axes[2].Values.Length;
        int S21(int h) => ((h * nFreq + 0) * nPort + 1) * nPort + 0;

        Assert.True(s.ComplexValues[S21(4)].Magnitude < s.ComplexValues[S21(0)].Magnitude,
            "a 45 mil loop must pass less than a 10 mil one across the sweep.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Gates 6 & 7 — Carried or Linked (WB45)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>WB45a — the flip happens at a moment the user can see, and is announced there.</b>
    /// A freshly placed wBond is Carried by construction; the file comes into existence when Update
    /// Layout from Schematic runs, and <i>that command</i> is where the instance becomes Linked.
    ///
    /// <para>The stored path is relative to the SCHEMATIC, which is what makes it survive the cell
    /// folder being moved or checked out somewhere else — an absolute path breaks on every other
    /// machine.</para>
    /// </summary>
    [Fact]
    public void Gate6_UpdateLayoutFromSchematic_FlipsTheInstanceToLinked_AndSaysSo()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        Assert.Equal(WBondPlacement.WireSource.Carried, WBondPlacement.SourceOf(comp));

        string cellDir = Path.Combine(_root, "Amp");
        var seeded = WBondCellSeeding.Seed(model, cellDir, "Amp");

        Assert.Equal(WBondCellSeeding.Outcome.Created, seeded.Outcome);
        Assert.Equal(WBondPlacement.WireSource.Linked, WBondPlacement.SourceOf(comp));

        // Relative, and pointing back out of schematic/ into layout/ — the WB40 attachment location.
        string stored = WBondPlacement.LinkedPathOf(comp)!;
        Assert.False(Path.IsPathRooted(stored));
        Assert.Contains("layout/Amp.wBond", stored.Replace('\\', '/'));

        // Announced, with the consequence and the way back.
        string said = string.Join("\n", seeded.Messages);
        Assert.Contains("LINKED", said);
        Assert.Contains("Carried", said);
    }

    /// <summary>
    /// <b>Owner report, 2026-08-17 — the bug this section exists for.</b> <i>"I placed a wBond into the
    /// schematic, added 2 more arrays, changed their loop heights to 30, 20 and 15 mil. Then I did an
    /// Update Layout from Schematic, but all 3 arrays had a loop height of 20 mil."</i>
    ///
    /// <para>20 mil is <c>WBondEmbedding.DefaultWire.LoopHeightMils</c> — the drawn default. The seeding
    /// wrote the raw <c>Design</c> payload and never applied the controlling parameters, which are an
    /// override layer that had only ever been applied on the way to the SOLVER.</para>
    ///
    /// <para>Driven through the real panel commands, not by setting parameters by hand, because the
    /// sequence is the report: add arrays with the panel's own button (which shares ONE
    /// <c>LoopProfile</c> across all three), then set three different per-array heights on it.</para>
    /// </summary>
    [Fact]
    public void UpdateLayout_WritesTheHeightsTheSchematicAsksFor_NotTheDrawnDefault()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(null, "W1");   // the palette default: one array, 20 mil
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);

        editor.AddWBondArrayCommand.Execute(null);
        editor.AddWBondArrayCommand.Execute(null);
        Assert.Equal(3, editor.WBondControls.Count);

        string[] mils = ["30", "20", "15"];
        for (int i = 0; i < 3; i++)
        {
            editor.WBondControls[i].LoopHeight = mils[i];
            editor.WBondControls[i].CommitLoopHeight();
        }

        var seeded = WBondCellSeeding.Seed(model, Path.Combine(_root, "Amp"), "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, seeded.Outcome);

        var onDisk = WBondIo.ReadFile(seeded.Path!);

        Assert.Equal(3, onDisk.Arrays.Count);
        double[] expected = [30.0, 20.0, 15.0];
        for (int i = 0; i < 3; i++)
            foreach (var wire in onDisk.Arrays[i].Wires)
                Assert.Equal(WBondUnits.ToNm(expected[i], WBondUnit.Mil), wire.LoopHeightNm);
    }

    /// <summary>
    /// Seeding bakes the geometry into the FILE and leaves the instance's own payload alone — WB44
    /// property 1 is unaffected by the fix above. Update Layout is a command the user ran; it is not a
    /// sweep, and it must still not edit the schematic's stored wires.
    /// </summary>
    [Fact]
    public void UpdateLayout_BakesTheFile_AndLeavesTheCarriedPayloadUntouched()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        comp.Parameters.First(p => p.Name == "LoopHeight").Expression = "45";
        comp.Parameters.First(p => p.Name == "LoopHeight").Unit = "mil";

        string before = comp.Parameters.First(p => p.Name == "Design").Expression;

        var seeded = WBondCellSeeding.Seed(model, Path.Combine(_root, "Amp"), "Amp");

        Assert.Equal(before, comp.Parameters.First(p => p.Name == "Design").Expression);
        Assert.Equal(WBondUnits.ToNm(45.0, WBondUnit.Mil),
            WBondIo.ReadFile(seeded.Path!).Arrays[0].Wires[0].LoopHeightNm);
    }

    /// <summary>
    /// <b>Applying a controlling parameter twice is the identity, and the fix above depends on it.</b>
    ///
    /// <para>Update Layout bakes the value into the file and then flips the instance to Linked — so the
    /// next Run reads the already-baked file and applies the same parameter to it AGAIN. That is only
    /// safe because every controlling parameter sets an ABSOLUTE value (a height, a diameter, a metal)
    /// rather than a delta or a factor. The claim is gated here rather than argued: the run's answer
    /// must be the same whether the parameter is still on the instance or has since been cleared.</para>
    ///
    /// <para>This is the same property that made <c>Span</c> — which scales by FACTOR (WB24c) — the one
    /// of the six that had to be deferred, so the gate is worth keeping if Span is ever revisited.</para>
    /// </summary>
    [Fact]
    public void ApplyingAControllingParameterTwice_IsTheIdentity()
    {
        var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);

        var lh = comp.Parameters.First(p => p.Name == "LoopHeight");
        lh.Expression = "45";
        lh.Unit = "mil";

        var seeded = WBondCellSeeding.Seed(model, Path.Combine(_root, "Amp"), "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, seeded.Outcome);
        Assert.Equal(WBondPlacement.WireSource.Linked, WBondPlacement.SourceOf(comp));

        // The parameter is still set, so the run applies it on top of the file it just baked.
        double applied = SeriesL(S21Of(RunPlaced(model)), 5e9);

        // Now clear it: the file already holds 45 mil, so nothing should move.
        lh.Expression = "";
        double baked = SeriesL(S21Of(RunPlaced(model)), 5e9);

        Assert.Equal(baked, applied, 12);
    }

    /// <summary>
    /// A <c>VAR</c> reference has no single value to draw — that is the whole point of it being the
    /// handle a sweep turns — so the wires are written AS DRAWN and the parameter is named. Inventing a
    /// number for it would put geometry in the layout that no run ever produces.
    /// </summary>
    [Fact]
    public void UpdateLayout_NamesAnExpressionItCannotBake_AndWritesThoseWiresAsDrawn()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        comp.Parameters.First(p => p.Name == "LoopHeight").Expression = "loopH";

        var seeded = WBondCellSeeding.Seed(model, Path.Combine(_root, "Amp"), "Amp");

        Assert.Equal(WBondUnits.ToNm(20.0, WBondUnit.Mil),
            WBondIo.ReadFile(seeded.Path!).Arrays[0].Wires[0].LoopHeightNm);

        string said = string.Join("\n", seeded.Messages);
        Assert.Contains("loopH", said);
        Assert.Contains("as drawn", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Three arrays sharing one profile, at three different heights, write ONE profile — the heights
    /// live on the wires. This used to assert three cloned profiles named after their arrays; the
    /// owner's 2026-08-17 decision retired the whole mechanism, and the file the user then opens in the
    /// wBond editor is the simpler for it.
    /// </summary>
    [Fact]
    public void UpdateLayout_WritesThreeHeightsWithoutInventingThreeProfiles()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1", "G2", "G3"), "W1");
        model.Components.Add(comp);

        foreach (var (array, mils) in new[] { ("G1", "30"), ("G2", "20"), ("G3", "15") })
            comp.Parameters.Add(new EditableParameter
                { Name = $"LoopHeight_{array}", Expression = mils, Unit = "mil" });

        var seeded = WBondCellSeeding.Seed(model, Path.Combine(_root, "Amp"), "Amp");
        var onDisk = WBondIo.ReadFile(seeded.Path!);

        // ONE profile still, untouched — three arrays at three heights no longer need three copies of
        // a shape, because the height lives on each wire rather than on its generator.
        Assert.Single(onDisk.Profiles);

        double[] expected = [30.0, 20.0, 15.0];
        for (int i = 0; i < 3; i++)
            foreach (var wire in onDisk.Arrays[i].Wires)
                Assert.Equal(WBondUnits.ToNm(expected[i], WBondUnit.Mil), wire.LoopHeightNm);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §9.6/WB42 — Update Schematic from Layout brings the wires back
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Owner report, 2026-08-17.</b> <i>"If I change loop height in layout editor, the component in
    /// the schematic is not updated. Even if I use Update Schematic from Layout command."</i>
    ///
    /// <para><c>LayoutToSchematicGenerator</c> walks a layout's <c>LayoutInstance</c>s and knew nothing
    /// about the wire layer — and no wire is ever a <c>LayoutInstance</c> (WB23: no wire enters a
    /// <c>.clay</c>). So the wire half of the command simply did not exist, while two shipped messages
    /// named it as the remedy.</para>
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_BringsALoopHeightEditBackIntoTheComponent()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        // What the layout editor holds after the user drags the loop taller.
        var edited = SharedProfileDesign(45.0, "G1");

        var result = WBondSchematicReconcile.Run(model, edited);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.True(WBondEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var back));
        Assert.Equal(WBondUnits.ToNm(45.0, WBondUnit.Mil), back!.Arrays[0].Wires[0].LoopHeightNm);

        Assert.Contains("updated from the layout", string.Join("\n", result.Messages));
        Assert.False(result.ArraysMoved);   // geometry only — no pin moved, nothing to check
    }

    /// <summary>
    /// The second half of the same report: <i>"Same is true for deleting a whole group of wires in
    /// layout — the deletion is not respected in schematic."</i>
    ///
    /// <para>This is the case that matters even under <c>Linked</c>. A placed wBond's <b>pins come from
    /// its carried payload</b>, so a deleted array leaves the symbol still showing that array's two
    /// terminals — still wired to whatever the user connected them to — while the model behind it has
    /// one branch fewer. The <c>Arrays</c> record moves with the payload, and the pin count follows.</para>
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_RespectsAnArrayDeleted_AndSaysThePinsMoved()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1", "G2"), "W1");
        model.Components.Add(comp);

        Assert.Equal("G1|G2", comp.Parameters.First(p => p.Name == "Arrays").Expression);
        Assert.Equal(4, WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null).Symbol!.Pins.Count);

        // The user deleted G2 in the layout.
        var result = WBondSchematicReconcile.Run(model, SharedProfileDesign(20.0, "G1"));
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.Equal("G1", comp.Parameters.First(p => p.Name == "Arrays").Expression);
        Assert.Equal(2, WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null).Symbol!.Pins.Count);

        // Pins moved, so this is a warning with the wiring named — not a quiet success.
        Assert.True(result.ArraysMoved);
        Assert.Contains("Check the wiring", string.Join("\n", result.Messages));
    }

    /// <summary>
    /// <b>The reconcile reaches the RENDER MODEL of the live view model</b>, not just the edit model —
    /// owner, 2026-08-17: <i>"wBond symbol parameter rendering in the schematic needs to be redrawn
    /// when doing Update Schematic from Layout."</i>
    ///
    /// <para>The chain is <c>SetParametersCommand.Execute</c> → <c>EditModel.NotifyChanged</c> →
    /// <c>SchematicViewModel.RebuildRenderModel</c>, and this drives all of it: the symbol's PIN COUNT
    /// after an array is deleted, and the parameter LABELS. Nothing below the view model is reachable
    /// from a test — if the on-screen canvas still does not repaint after this passes, the cause is the
    /// view's own invalidation and not this path.</para>
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_RefreshesTheLiveRenderModel()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1", "G2"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G2", Expression = "30", Unit = "mil" });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);

        Assert.Equal(4, vm.RenderModel.Components.Single().Ports.Count);   // G1.i/o, G2.i/o (RefPin off)
        Assert.Contains(vm.RenderModel.Components.Single().Labels, l => l.StartsWith("LoopHeight_G2"));

        // The user deleted G2 in the layout.
        var result = WBondSchematicReconcile.Run(model, SharedProfileDesign(20.0, "G1"));
        vm.Execute(result.Command!);

        // The symbol lost its pin pair…
        Assert.Equal(2, vm.RenderModel.Components.Single().Ports.Count);

        // …and G2's now-orphaned override is gone with it, rather than drawing a label for a pin pair
        // that is no longer on the symbol.
        Assert.DoesNotContain(vm.RenderModel.Components.Single().Labels,
            l => l.StartsWith("LoopHeight_G2", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Owner report, 2026-08-17.</b> <i>"I changed the loop height in layout using the Array
    /// Inductance double-click on loop height. Then I did an Update Schematic from Layout, but the loop
    /// height was not updated in the schematic."</i>
    ///
    /// <para>The reconcile brought the GEOMETRY back into the payload and left <c>LoopHeight_G1</c>
    /// stating the old number — so the dialog went on showing it, <b>and the next Run applied that old
    /// number straight back over the wires that had just been imported</b>, silently undoing the
    /// command. The override is the schematic's statement of the loop height; after this command it has
    /// to state what the layout actually has.</para>
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_WritesTheLayoutsLoopHeightBackIntoTheOverride()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G1", Expression = "10", Unit = "mil" });
        model.Components.Add(comp);

        // The layout, after the inductance panel's double-click set 15 mil.
        var edited = SharedProfileDesign(15.0, "G1");

        var result = WBondSchematicReconcile.Run(model, edited);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        // The override now states what the layout has — in the row's OWN unit, so the dialog reads
        // "15" rather than 0.000381.
        Assert.Equal("15", comp.Parameters.First(p => p.Name == "LoopHeight_G1").Expression);
        Assert.Equal("mil", comp.Parameters.First(p => p.Name == "LoopHeight_G1").Unit);

        // …and re-applying it is therefore the identity: the next Run reproduces the layout rather
        // than reverting it.
        var after = Elaborate(edited, ("LoopHeight_G1", 15.0 * MilM));
        Assert.Equal(WBondUnits.ToNm(15.0, WBondUnit.Mil), after.Arrays[0].Wires[0].LoopHeightNm);
    }

    /// <summary>
    /// The write-back reaches diameter and material too, and <b>only parameters that are already
    /// SET</b>. Blank means "as drawn", and the payload now carries what was drawn — writing a number
    /// into every blank row on every reconcile would invent overrides the user never asked for.
    /// </summary>
    [Fact]
    public void TheWriteBack_ReachesDiameterAndMaterial_AndLeavesUnsetRowsUnset()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "Diameter_G1", Expression = "1", Unit = "mil" });
        comp.Parameters.Add(new EditableParameter { Name = "Material_G1", Expression = "Gold" });
        model.Components.Add(comp);

        var edited = SharedProfileDesign(20.0, "G1");
        foreach (var wire in edited.Arrays[0].Wires)
        {
            wire.DiameterNm = WBondUnits.ToNm(2.0, WBondUnit.Mil);
            wire.Material = "Aluminium";
        }

        WBondSchematicReconcile.Run(model, edited).Command!.Execute();

        Assert.Equal("2", comp.Parameters.First(p => p.Name == "Diameter_G1").Expression);
        Assert.Equal("Aluminium", comp.Parameters.First(p => p.Name == "Material_G1").Expression);

        // The unsuffixed rows were never set, and stay that way.
        Assert.Equal("", comp.Parameters.First(p => p.Name == "LoopHeight").Expression);
        Assert.Equal("", comp.Parameters.First(p => p.Name == "Diameter").Expression);
    }

    /// <summary>
    /// <b>An expression is never overwritten.</b> <c>LoopHeight_G1 = loopH</c> is the handle a sweep
    /// turns; replacing it with a literal would silently retire the sweep. It is reported with the
    /// measured value instead, so the decision stays the user's.
    /// </summary>
    [Fact]
    public void TheWriteBack_ReportsAnExpressionRatherThanReplacingIt()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G1", Expression = "loopH", Unit = "mil" });
        model.Components.Add(comp);

        var result = WBondSchematicReconcile.Run(model, SharedProfileDesign(15.0, "G1"));
        result.Command?.Execute();

        Assert.Equal("loopH", comp.Parameters.First(p => p.Name == "LoopHeight_G1").Expression);

        string said = string.Join("\n", result.Messages);
        Assert.Contains("loopH", said);
        Assert.Contains("15 mil", said);
    }

    /// <summary>
    /// Wires that disagree are reported, not averaged — an individually dragged wire can leave an
    /// array with no single loop height, and inventing one would state something about the layout that
    /// is not true of it.
    /// </summary>
    [Fact]
    public void TheWriteBack_ReportsWiresThatNoLongerAgree()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G1", Expression = "10", Unit = "mil" });
        model.Components.Add(comp);

        var edited = SharedProfileDesign(15.0, "G1");
        WireEdits.SetLoopHeightPreservingPath(edited.Arrays[0].Wires[1], WBondUnits.ToNm(25.0, WBondUnit.Mil));

        var result = WBondSchematicReconcile.Run(model, edited);
        result.Command?.Execute();

        Assert.Equal("10", comp.Parameters.First(p => p.Name == "LoopHeight_G1").Expression);
        Assert.Contains("no longer share one value", string.Join("\n", result.Messages));
    }

    /// <summary>
    /// <b>The hole the owner actually fell into.</b> "Nothing changed" was decided on the <c>Design</c>
    /// payload alone, so a layout whose geometry already matched the payload returned "already
    /// identical" and left a stale override in place — to be applied again at the next Run. Whatever
    /// this command would write is what decides whether it has anything to do.
    /// </summary>
    [Fact]
    public void TheUnchangedCheck_LooksAtTheOverridesToo_NotJustThePayload()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G1", Expression = "10", Unit = "mil" });
        model.Components.Add(comp);

        // The payload and the layout agree on geometry; only the override is stale.
        var result = WBondSchematicReconcile.Run(model, SharedProfileDesign(20.0, "G1"));

        Assert.NotNull(result.Command);
        result.Command!.Execute();
        Assert.Equal("20", comp.Parameters.First(p => p.Name == "LoopHeight_G1").Expression);
    }

    /// <summary>
    /// An agreeing payload says nothing at all. "The wires already match" is not news to someone who
    /// just ran this on a cell they have been editing, and reporting it trains people to skim the pane.
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_IsSilentWhenTheWiresAlreadyAgree()
    {
        var model = NewSchematic();
        model.Components.Add(WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1"));

        var result = WBondSchematicReconcile.Run(model, SharedProfileDesign(20.0, "G1"));

        Assert.Null(result.Command);
        Assert.Empty(result.Messages);
    }

    /// <summary>
    /// Wires in the layout with no component to put them on is reported, not silently dropped — the
    /// user drew them, and "nothing happened" is the least useful answer available.
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_NamesWiresWithNoComponentToBringThemInto()
    {
        var result = WBondSchematicReconcile.Run(NewSchematic(), SharedProfileDesign(20.0, "G1"));

        Assert.Null(result.Command);
        Assert.Contains("no wBond component", string.Join("\n", result.Messages));
    }

    /// <summary>
    /// A layout with no wires says nothing — the ordinary case for most cells, and this command runs on
    /// every one of them.
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_SaysNothingForALayoutWithNoWires()
    {
        var result = WBondSchematicReconcile.Run(NewSchematic(), null);

        Assert.Null(result.Command);
        Assert.Empty(result.Messages);
    }

    /// <summary>
    /// Reconciling does <b>not</b> touch <c>Source</c>. It makes the payload agree with the layout; it
    /// does not decide which of them the next Run reads, and quietly flipping that is precisely what
    /// WB45a forbids.
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_LeavesTheWireSourceAlone()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        string layoutDir = Path.Combine(_root, "Amp", "layout");
        Directory.CreateDirectory(layoutDir);
        string wbondPath = Path.Combine(layoutDir, "Amp.wBond");
        WBondIo.WriteFile(wbondPath, SharedProfileDesign(45.0, "G1"));
        WBondPlacement.LinkTo(comp, wbondPath, model.SchematicDirectory);

        var result = WBondSchematicReconcile.Run(model, SharedProfileDesign(45.0, "G1"));
        result.Command!.Execute();

        Assert.Equal(WBondPlacement.WireSource.Linked, WBondPlacement.SourceOf(comp));
        Assert.Equal("../layout/Amp.wBond", WBondPlacement.LinkedPathOf(comp));
    }

    /// <summary>
    /// <b>Owner report, 2026-08-17.</b> <i>"If I do an Update Layout from Schematic, then go back to the
    /// schematic Component Parameters and add another array, then do another Update Layout from
    /// Schematic, the new array that I created in schematic does not show up in the layout."</i>
    ///
    /// <para>The sidecar was created ONCE and thereafter left entirely alone (WB41) — a rule that exists
    /// to stop a re-run regenerating over wires the user has moved in the layout. It is right about
    /// EXISTING arrays and wrong about a NEW one: adding an array touches no wire that is already
    /// there, so refusing to add it protects nothing and silently drops the thing the command was just
    /// asked to do.</para>
    /// </summary>
    [Fact]
    public void UpdateLayout_AddsAnArrayCreatedInTheSchematic_WithoutDisturbingTheOnesAlreadyDrawn()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        string cellDir = Path.Combine(_root, "Amp");
        var first = WBondCellSeeding.Seed(model, cellDir, "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, first.Outcome);

        // The user then moves G1's wires about in the layout — the edits WB41 exists to protect.
        var drawn = WBondIo.ReadFile(first.Path!);
        foreach (var wire in drawn.Arrays[0].Wires)
            for (int i = 0; i < wire.Points.Count; i++)
                wire.Points[i] = wire.Points[i] with { Y = wire.Points[i].Y + WBondUnits.ToNm(40, WBondUnit.Mil) };
        WBondIo.WriteFile(first.Path!, drawn);
        var moved = drawn.Arrays[0].Wires.Select(w => w.Points.ToList()).ToList();

        // …and adds a second array in the Component Parameters dialog.
        WBondPlacement.ApplyDesign(comp, SharedProfileDesign(20.0, "G1", "G2"));

        var second = WBondCellSeeding.Seed(model, cellDir, "Amp");
        var onDisk = WBondIo.ReadFile(second.Path!);

        // The new array arrived…
        Assert.Equal(["G1", "G2"], onDisk.Arrays.Select(a => a.Name));
        Assert.NotEmpty(onDisk.Arrays[1].Wires);

        // …and G1's moved wires are exactly where the user left them.
        for (int w = 0; w < moved.Count; w++)
            Assert.Equal(moved[w], onDisk.Arrays[0].Wires[w].Points);
    }

    /// <summary>
    /// The owner's sequence in full, through the real panel commands: seed, add an array in the dialog,
    /// seed again, add a third, seed again. Each round's array must arrive, the pin count must follow,
    /// and the instance must stay <c>Linked</c> — the WB45a flip belongs on the FIRST write only, and a
    /// merge changes what is drawn rather than which source the next Run reads.
    /// </summary>
    [Fact]
    public void UpdateLayout_RunTwice_KeepsBringingNewArraysAcross()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);

        string cellDir = Path.Combine(_root, "Amp");
        string sidecar = Path.Combine(cellDir, "layout", "Amp.wBond");

        Assert.Equal(WBondCellSeeding.Outcome.Created, WBondCellSeeding.Seed(model, cellDir, "Amp").Outcome);
        Assert.Equal(WBondPlacement.WireSource.Linked, WBondPlacement.SourceOf(comp));

        for (int round = 2; round <= 3; round++)
        {
            editor.AddWBondArrayCommand.Execute(null);

            var seeded = WBondCellSeeding.Seed(model, cellDir, "Amp");
            Assert.Equal(WBondCellSeeding.Outcome.Merged, seeded.Outcome);

            var onDisk = WBondIo.ReadFile(sidecar);
            Assert.Equal(round, onDisk.Arrays.Count);
            Assert.Equal($"G{round}", onDisk.Arrays[^1].Name);
            Assert.NotEmpty(onDisk.Arrays[^1].Wires);

            // The symbol grew with it, and every array still has a profile it can resolve.
            Assert.Equal(round * 2, vm.RenderModel.Components.Single().Ports.Count);
            foreach (var array in onDisk.Arrays)
                foreach (var wire in array.Wires)
                    Assert.NotNull(onDisk.ProfileByName(wire.ProfileBinding!));
        }

        // A merge never re-decides the wire source.
        Assert.Equal(WBondPlacement.WireSource.Linked, WBondPlacement.SourceOf(comp));
    }

    /// <summary>
    /// <b>Owner report, 2026-08-17, with the workspace attached.</b> <i>"Not fixed — schematic and
    /// layout don't agree right after I did an Update Layout from Schematic. I don't see any G2
    /// wires."</i> The `.wBond` on disk held G1 <b>and</b> G2, with real distinct geometry, while the
    /// layout on screen showed only G1.
    ///
    /// <para>The merge was reading and writing the FILE while a layout editor had the cell open. An
    /// open <c>LayoutEditorViewModel</c> holds its own <see cref="WBondDesign"/> object and mutates it
    /// in place, so the file write changed nothing on screen — <b>and the live design would have been
    /// written back over it on the next save, deleting G2 again.</b> A stale view was the visible half
    /// of a lost-edit bug.</para>
    ///
    /// <para>The oracle is the LIVE design object, which is the thing the canvas draws and the thing
    /// the layout's own save path persists.</para>
    /// </summary>
    [Fact]
    public void UpdateLayout_WithTheLayoutOpen_MergesIntoTheLiveDesign_NotBehindIt()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        string cellDir = Path.Combine(_root, "Amp");
        var first = WBondCellSeeding.Seed(model, cellDir, "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, first.Outcome);

        // What an open layout editor holds: the design read from the file, mutated in place from then
        // on. Passing this object IS the fix — Seed must merge into it rather than through the file.
        var live = WBondIo.ReadFile(first.Path!);
        Assert.Single(live.Arrays);

        WBondPlacement.ApplyDesign(comp, SharedProfileDesign(20.0, "G1", "G2"));

        var second = WBondCellSeeding.Seed(model, cellDir, "Amp", only: null, liveDesign: live);

        Assert.Equal(WBondCellSeeding.Outcome.Merged, second.Outcome);
        Assert.True(second.LiveDesignChanged,
            "the caller has to be told, or the editor never repaints and never marks itself dirty.");

        // The array reached the object the canvas draws…
        Assert.Equal(["G1", "G2"], live.Arrays.Select(a => a.Name));
        Assert.NotEmpty(live.Arrays[1].Wires);
        Assert.NotNull(live.ProfileByName(live.Arrays[1].Wires[0].ProfileBinding!));

        // …and the FILE was deliberately left alone: the open editor owns writing it, and a second
        // writer behind its back is what produced the report. The layout is dirty until saved, exactly
        // as it is after any other edit to an open document.
        Assert.Single(WBondIo.ReadFile(first.Path!).Arrays);
    }

    /// <summary>
    /// <b>Owner report, 2026-08-17, same workspace.</b> <i>"I changed the G1 loop height to 10 mil in
    /// schematic, then did an Update Layout from Schematic, but the loop height still looks like it's
    /// 20 mil."</i>
    ///
    /// <para>A re-run applied the controlling parameters only to arrays it was ADDING, and deliberately
    /// left arrays already drawn alone — WB41's "never overwrite wires the user has moved", too coarse
    /// in the same way the never-write-the-file-again rule had been. Two things settled it: the wBond
    /// editor's own "set this array's loop height" command does exactly this, so there is no new
    /// destruction here that the editor would not also do; and the application is now path-preserving,
    /// so what WB41 was actually defending — the route and the feet — survives it.</para>
    ///
    /// <para>The fixture reproduces the owner's own file: G1 hand-routed in the layout and still bound,
    /// with the schematic asking for 10 mil.</para>
    /// </summary>
    [Fact]
    public void UpdateLayout_AppliesALoopHeightChangeToAnArrayAlreadyDrawn()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        string cellDir = Path.Combine(_root, "Amp");
        var first = WBondCellSeeding.Seed(model, cellDir, "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, first.Outcome);

        // The layout, as the user has since routed it: an interior point taken off the chord in XY.
        var live = WBondIo.ReadFile(first.Path!);
        var wire = live.Arrays[0].Wires[0];
        wire.Points[3] = wire.Points[3] with { X = wire.Points[3].X - WBondUnits.ToNm(8, WBondUnit.Mil) };

        var authored = wire.Points.Select(pt => (pt.X, pt.Y)).ToList();
        var feet = (First: wire.Points[0], Last: wire.Points[^1]);
        Assert.Equal(WBondUnits.ToNm(20.0, WBondUnit.Mil), wire.LoopHeightNm);

        // The schematic asks for 10 mil.
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G1", Expression = "10", Unit = "mil" });

        var second = WBondCellSeeding.Seed(model, cellDir, "Amp", only: null, liveDesign: live);

        Assert.Equal(WBondCellSeeding.Outcome.Merged, second.Outcome);
        Assert.True(second.LiveDesignChanged);

        // The height the schematic asked for…
        Assert.Equal(WBondUnits.ToNm(10.0, WBondUnit.Mil), live.Arrays[0].Wires[0].LoopHeightNm);

        // …and the route the user authored, and both feet, exactly as they were.
        Assert.Equal(authored, live.Arrays[0].Wires[0].Points.Select(pt => (pt.X, pt.Y)).ToList());
        Assert.Equal(feet.First, live.Arrays[0].Wires[0].Points[0]);
        Assert.Equal(feet.Last, live.Arrays[0].Wires[0].Points[^1]);

        Assert.Contains("route and both its feet are unchanged",
            string.Join("\n", second.Messages), StringComparison.Ordinal);
    }

    /// <summary>
    /// A re-run with an override that is ALREADY satisfied changes nothing and says nothing — otherwise
    /// Update Layout from Schematic would leave an unsaved document behind every single time it ran.
    /// </summary>
    [Fact]
    public void UpdateLayout_WithNothingLeftToApply_IsSilentAndLeavesTheLayoutClean()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        comp.Parameters.Add(new EditableParameter
            { Name = "LoopHeight_G1", Expression = "10", Unit = "mil" });
        model.Components.Add(comp);

        string cellDir = Path.Combine(_root, "Amp");
        var live = WBondIo.ReadFile(WBondCellSeeding.Seed(model, cellDir, "Amp").Path!);

        var second = WBondCellSeeding.Seed(model, cellDir, "Amp", only: null, liveDesign: live);

        Assert.Equal(WBondCellSeeding.Outcome.KeptExisting, second.Outcome);
        Assert.False(second.LiveDesignChanged);
        Assert.Empty(second.Messages);
    }

    /// <summary>
    /// §3.0 — <b>a Carried instance whose cell already has a <c>.wBond</c> is a legitimate state, not
    /// an error.</b> It is someone who deliberately kept the portable payload, and re-running Update
    /// Layout must not auto-convert it: the flip belongs on <c>Created</c> alone, because a flip on a
    /// later scan noticing the file exists would change which wires simulate with nothing on screen.
    /// </summary>
    [Fact]
    public void ACarriedInstance_WhoseCellAlreadyHasAWBondFile_IsNotAutoConverted()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(SharedProfileDesign(20.0, "G1"), "W1");
        model.Components.Add(comp);

        string cellDir = Path.Combine(_root, "Amp");
        string layoutDir = Path.Combine(cellDir, "layout");
        Directory.CreateDirectory(layoutDir);
        WBondIo.WriteFile(Path.Combine(layoutDir, "Amp.wBond"), SharedProfileDesign(20.0, "G1"));

        var seeded = WBondCellSeeding.Seed(model, cellDir, "Amp");

        Assert.Equal(WBondCellSeeding.Outcome.KeptExisting, seeded.Outcome);
        Assert.Equal(WBondPlacement.WireSource.Carried, WBondPlacement.SourceOf(comp));
    }

    /// <summary>
    /// <b>Gate 6 — a linked instance runs from the FILE, and survives the schematic being moved.</b>
    ///
    /// <para>The move is real: the whole cell folder is renamed, so an absolute stored path would be
    /// dangling and a workspace-relative one would be wrong. A schematic-relative one still resolves,
    /// exactly as §4 of <c>workspace-and-project-tree.md</c> resolves a cell reference.</para>
    ///
    /// <para>The oracle is that the wires that RUN are the file's and not the payload's: the file is
    /// written at a different loop height from the one the component carries, so the two produce
    /// measurably different inductances.</para>
    /// </summary>
    [Fact]
    public void Gate6_ALinkedInstance_RunsFromTheFile_AndSurvivesAMoveOfTheSchematic()
    {
        var model = Testbench(SharedProfileDesign(10.0, "G1"), SpAt5GHz());
        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);

        // The file holds a much TALLER loop than the payload does, so "which one ran" is measurable.
        string cellDir = Path.Combine(_root, "Amp");
        string layoutDir = Path.Combine(cellDir, "layout");
        Directory.CreateDirectory(layoutDir);
        string wbondPath = Path.Combine(layoutDir, "Amp.wBond");
        WBondIo.WriteFile(wbondPath, SharedProfileDesign(45.0, "G1"));

        double carried = SeriesL(S21Of(RunPlaced(model)), 5e9);

        WBondPlacement.LinkTo(comp, wbondPath, model.SchematicDirectory);
        double linked = SeriesL(S21Of(RunPlaced(model)), 5e9);

        Assert.True(linked > carried * 1.2,
            $"a linked instance must simulate the FILE's 45 mil wires, not the payload's 10 mil ones; " +
            $"got {linked * 1e12:F1} pH against {carried * 1e12:F1} pH.");

        // Now move the whole cell folder. The stored value is relative to the schematic, so it is the
        // SAME string and it still resolves.
        string movedCell = Path.Combine(_root, "Amp2");
        Directory.Move(cellDir, movedCell);
        model.SchematicDirectory = Path.Combine(movedCell, "schematic");

        double afterMove = SeriesL(S21Of(RunPlaced(model)), 5e9);
        Assert.Equal(linked, afterMove, 12);
    }

    /// <summary>
    /// <b>Gate 6, the other half — it refuses LEGIBLY when the file is gone, with the path in the
    /// message.</b> §5.0/WB17b's argument against referencing a design was exactly that it
    /// reintroduces a "Not Found" state; WB45 accepts that cost for the Linked case, so the refusal
    /// has to read like the cell-reference one the user already knows.
    /// </summary>
    [Fact]
    public void Gate6_ALinkedInstance_RefusesLegiblyWhenTheFileIsGone()
    {
        var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);

        string layoutDir = Path.Combine(_root, "Amp", "layout");
        Directory.CreateDirectory(layoutDir);
        string wbondPath = Path.Combine(layoutDir, "Amp.wBond");
        WBondIo.WriteFile(wbondPath, SharedProfileDesign(20.0, "G1"));

        WBondPlacement.LinkTo(comp, wbondPath, model.SchematicDirectory);
        File.Delete(wbondPath);

        var run = RunPlaced(model);

        Assert.NotEqual(RunStatus.Success, run.Status);
        Assert.Contains("Amp.wBond", run.StatusMessage);
        Assert.Contains("Carried", run.StatusMessage);
    }

    /// <summary>
    /// <b>Gate 7 — a linked instance whose <c>.wBond</c> had its arrays REORDERED is reported, not
    /// silently re-pointed (§3.2/WB35a).</b>
    ///
    /// <para>This is the consequence that had to ship WITH linking rather than after it. Carried drift
    /// is introduced by an explicit re-import, so it can be reported at that moment; linked drift
    /// arrives the instant someone reorders arrays in the file, changing the symbol's pin order live
    /// beneath an already-wired schematic. Pin order IS array order, so every pin keeps its position
    /// while its NAME moves to a different row. Without this check, linking would be strictly more
    /// dangerous than carrying on that one axis.</para>
    ///
    /// <para>The reorder is CONSTRUCTED rather than made by a real edit, so the fixture states exactly
    /// the condition being tested.</para>
    /// </summary>
    [Fact]
    public void Gate7_ALinkedFileWithReorderedArrays_IsReported()
    {
        var p = Params(
            ("Arrays", "G1|G2"),
            ("Design", WBondEmbedding.Encode(SharedProfileDesign(20.0, "G2", "G1"))));

        var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", p)!;

        string report = string.Join("\n", DrainOf(model));
        Assert.Contains("REORDERED", report);
        Assert.Contains("G1|G2", report);
        Assert.Contains("G2|G1", report);
        Assert.Contains("Check the wiring", report);
    }

    /// <summary>
    /// <b>Gate 7 through the whole product path</b> — extract → <c>.cnl</c> → elaborate → engine →
    /// <c>RunResult.Warnings</c>, which is what reaches the Messages pane.
    ///
    /// <para>This also pins something the direct-factory gate above cannot see: the <c>Arrays</c>
    /// record is written into the netlist as <c>G1|G2</c>, and a <c>.cnl</c> parameter value is read as
    /// raw text up to the next whitespace. If the <c>|</c> ever stopped surviving that round trip the
    /// drift check would silently never fire again — the record would arrive blank, and a blank record
    /// is (correctly) treated as "nothing is known about what this was wired against".</para>
    /// </summary>
    [Fact]
    public void Gate7_TheReorderReachesTheRunAsAWarning()
    {
        var model = Testbench(SharedProfileDesign(20.0, "G1", "G2"), SpAt5GHz());
        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);

        Assert.Equal("G1|G2", comp.Parameters.First(p => p.Name == "Arrays").Expression);

        // The FILE reorders them. Same set, same wires, different order — so every pin keeps its
        // position on the symbol while its name moves to a different row.
        string layoutDir = Path.Combine(_root, "Amp", "layout");
        Directory.CreateDirectory(layoutDir);
        string wbondPath = Path.Combine(layoutDir, "Amp.wBond");
        WBondIo.WriteFile(wbondPath, SharedProfileDesign(20.0, "G2", "G1"));

        WBondPlacement.LinkTo(comp, wbondPath, model.SchematicDirectory);

        var run = RunPlaced(model);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);

        string warnings = string.Join("\n", run.Warnings);
        Assert.Contains("REORDERED", warnings);
        Assert.Contains("W1", warnings);
    }

    /// <summary>An agreeing array list says nothing — the check must not be noise on every run.</summary>
    [Fact]
    public void AnAgreeingArrayList_IsSilent()
    {
        var p = Params(
            ("Arrays", "G1|G2"),
            ("Design", WBondEmbedding.Encode(SharedProfileDesign(20.0, "G1", "G2"))));

        var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", p)!;
        Assert.Empty(DrainOf(model));
    }

    /// <summary>
    /// A CARRIED instance's netlist carries the payload and no <c>Arrays</c> record — its payload
    /// cannot drift against itself, so the check would only ever be noise there. A LINKED one carries
    /// the path and the record, and NOT the payload: one copy of the wires is the whole point.
    /// </summary>
    [Fact]
    public void TheNetlistNamesExactlyOneSource()
    {
        var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);

        var carried = NetExtractor.Extract(model, "tb").TestBench.Instances
            .First(i => i.InstanceName == "W1");

        Assert.Contains(carried.Overrides, o => o.Name == "Design");
        Assert.DoesNotContain(carried.Overrides, o => o.Name is "File" or "Arrays" or "Source");

        string layoutDir = Path.Combine(_root, "Amp", "layout");
        Directory.CreateDirectory(layoutDir);
        string wbondPath = Path.Combine(layoutDir, "Amp.wBond");
        WBondIo.WriteFile(wbondPath, SharedProfileDesign(20.0, "G1"));
        WBondPlacement.LinkTo(comp, wbondPath, model.SchematicDirectory);

        var linked = NetExtractor.Extract(model, "tb").TestBench.Instances
            .First(i => i.InstanceName == "W1");

        Assert.DoesNotContain(linked.Overrides, o => o.Name == "Design");
        Assert.Contains(linked.Overrides, o => o.Name == "Arrays");

        // Absolute in the NETLIST, relative in the DOCUMENT: the netlist is a generated intermediate
        // written wherever the run writes it, and the extractor is where the schematic's own directory
        // is known.
        var file = linked.Overrides.First(o => o.Name == "File");
        Assert.True(Path.IsPathRooted(file.Expression));
    }

    /// <summary>
    /// A controlling parameter reaches a LINKED instance exactly as it reaches a carried one — it is
    /// applied to the decoded design and cannot tell where that design came from (§2, WB45's "what
    /// does NOT differ").
    /// </summary>
    [Fact]
    public void AControllingParameter_ReachesALinkedInstanceToo()
    {
        var model = Testbench(SharedProfileDesign(20.0, "G1"), SpAt5GHz());
        var comp = model.Components.First(c => c.Symbol == SymbolKind.WBond);

        string layoutDir = Path.Combine(_root, "Amp", "layout");
        Directory.CreateDirectory(layoutDir);
        string wbondPath = Path.Combine(layoutDir, "Amp.wBond");
        WBondIo.WriteFile(wbondPath, SharedProfileDesign(10.0, "G1"));
        WBondPlacement.LinkTo(comp, wbondPath, model.SchematicDirectory);

        double asDrawn = SeriesL(S21Of(RunPlaced(model)), 5e9);

        var lh = comp.Parameters.First(p => p.Name == "LoopHeight");
        lh.Expression = "45";
        lh.Unit = "mil";

        double overridden = SeriesL(S21Of(RunPlaced(model)), 5e9);

        Assert.True(overridden > asDrawn * 1.2,
            $"the override must reach the linked design too; got {overridden * 1e12:F1} pH " +
            $"against {asDrawn * 1e12:F1} pH.");
    }
}
