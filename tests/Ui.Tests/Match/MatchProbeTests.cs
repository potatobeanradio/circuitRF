using System;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

/// <summary>
/// MN-4's UI half (match.md §10.4, §10.5): when the Probe button is live, what its disabled tooltip
/// says, and the provenance a probed termination carries.
/// </summary>
/// <remarks>
/// <b>View-model tests against a real extraction.</b> The button's enablement is decided from the
/// testbench <c>NetExtractor</c> produces, not from the canvas, so these build a real schematic — wires
/// and all — and let the extractor answer. A test that stubbed the connectivity would pass for a
/// button that reads the wrong thing.
/// </remarks>
public class MatchProbeTests(ITestOutputHelper output)
{
    // ── schematic fixtures ────────────────────────────────────────────────────

    private static EditableComponent PlaceMatch(MatchDesign? design = null, double x = 0, double y = 0)
    {
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        if (design is not null)
            comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        return comp;
    }

    private static EditableComponent Two(SymbolKind kind, string name, double cx, double cy,
                                         string param, string value)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = param, Expression = value });
        return c;
    }

    private static EditableComponent Ground(double cx, double cy)
        => new() { Symbol = SymbolKind.Ground, X = cx, Y = cy };

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    /// <summary>
    /// The fixture the whole file leans on: a <c>Match</c> at the origin with 200 Ω ‖ 0.125 pF hung on
    /// pin 1 and 50 Ω on pin 2. Pin 1 sits at (-200, 0) and pin 2 at (200, 0).
    /// </summary>
    private static SchematicEditModel Interstage(MatchDesign? design = null)
    {
        var model = new SchematicEditModel();
        model.Components.Add(PlaceMatch(design));

        // R1 across pin 1: ports at (-200, 0) and (-200, 400).
        model.Components.Add(Two(SymbolKind.Resistor, "R1", -200, 200, "R", "200"));
        model.Components.Add(Ground(-200, 400));

        // C1 beside it, joined to pin 1 by a wire: ports at (-600, 0) and (-600, 400).
        model.Components.Add(Two(SymbolKind.Capacitor, "C1", -600, 200, "C", "0.125e-12"));
        model.Components.Add(Ground(-600, 400));
        model.Wires.Add(Wire((-600, 0), (-200, 0)));

        // R2 on pin 2.
        model.Components.Add(Two(SymbolKind.Resistor, "R2", 200, 200, "R", "50"));
        model.Components.Add(Ground(200, 400));
        return model;
    }

    private static MatchDesign Band() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        PlotPoints = 41,
        Term1 = new Termination(50, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static MatchDesignerViewModel Open(SchematicEditModel model)
    {
        var comp = model.Components.First(c => c.Symbol == SymbolKind.Match);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return designer;
    }

    private static MatchDesign StoredDesign(SchematicEditModel model)
    {
        string payload = model.Components.First(c => c.Symbol == SymbolKind.Match)
            .Parameters.First(p => p.Name == "Design").Expression;
        Assert.True(MatchEmbedding.TryDecode(payload, out var d));
        return d!;
    }

    // ── §5 / §10.4 — the button states ───────────────────────────────────────

    [Fact]
    public void AProbeableSchematicEnablesBothButtons()
    {
        var d = Open(Interstage(Band()));
        Assert.True(d.Term1.CanProbe, d.Term1.ProbeTooltip);
        Assert.True(d.Term2.CanProbe, d.Term2.ProbeTooltip);
        output.WriteLine(d.Term1.ProbeTooltip);
    }

    [Fact]
    public void APinTiedToGroundIsDisabled_AndSaysSo()
    {
        var model = Interstage(Band());
        model.Components.RemoveAll(c => c.InstanceName is "R1" or "C1");
        model.Components.RemoveAll(c => c.Symbol == SymbolKind.Ground && c.X < 0);
        model.Wires.Clear();
        model.Components.Add(Ground(-200, 0));   // pin 1 straight onto ground

        var d = Open(model);
        Assert.False(d.Term1.CanProbe);
        Assert.Equal(MatchProbeBlock.PinUnconnected, d.Term1.Availability.Block);
        Assert.Contains("sits on ground", d.Term1.ProbeTooltip);
        Assert.True(d.Term2.CanProbe, "the OTHER pin is still probeable — the reason is per-pin");
        output.WriteLine(d.Term1.ProbeTooltip);
    }

    [Fact]
    public void ANetCarryingOnlyTheMatchIsDisabled_AndSaysSo()
    {
        var model = Interstage(Band());
        // Nothing on pin 1 at all. An unwired pin still gets a net of its OWN out of extraction, so
        // what the schematic can actually tell us is that the net is empty — not that the pin is bare.
        model.Components.RemoveAll(c => c.InstanceName is "R1" or "C1");
        model.Components.RemoveAll(c => c.Symbol == SymbolKind.Ground && c.X < 0);
        model.Wires.Clear();

        var d = Open(model);
        Assert.False(d.Term1.CanProbe);
        Assert.Equal(MatchProbeBlock.NetIsBare, d.Term1.Availability.Block);
        Assert.Contains("nothing but MN1 itself", d.Term1.ProbeTooltip);
        Assert.True(d.Term2.CanProbe, "the OTHER pin is still probeable — the reason is per-pin");
        output.WriteLine(d.Term1.ProbeTooltip);
    }

    [Fact]
    public void AMatchInsideACellDefinitionIsDisabled_AndSaysSo()
    {
        var model = Interstage(Band());
        // A Pin makes this schematic a cell DEFINITION rather than a testbench.
        var pin = new EditableComponent { Symbol = SymbolKind.Pin, X = -1200, Y = 0 };
        pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" });
        model.Components.Add(pin);
        model.Wires.Add(Wire((-1100, 0), (-600, 0)));

        var d = Open(model);
        Assert.False(d.Term1.CanProbe);
        Assert.False(d.Term2.CanProbe);
        Assert.Equal(MatchProbeBlock.InsideCell, d.Term1.Availability.Block);
        Assert.Contains("cell definition", d.Term1.ProbeTooltip);
        output.WriteLine(d.Term1.ProbeTooltip);
    }

    [Fact]
    public void AnUnresolvedSchematicErrorDisablesTheButton_AndNamesTheFirstProblem()
    {
        var model = Interstage(Band());
        // Two Terms claiming Num=1 — an extraction conflict, and exactly the kind of thing that makes
        // a measurement of "the circuit you drew" not mean what it says.
        for (int i = 0; i < 2; i++)
        {
            var t = new EditableComponent
                { InstanceName = $"T{i + 1}", Symbol = SymbolKind.Term, X = -1200 - (400 * i), Y = 200 };
            t.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" });
            t.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50" });
            model.Components.Add(t);
            model.Components.Add(Ground(-1200 - (400 * i), 400));
        }

        var d = Open(model);
        Assert.False(d.Term1.CanProbe);
        Assert.Equal(MatchProbeBlock.SchematicErrors, d.Term1.Availability.Block);
        Assert.Contains("unresolved problems", d.Term1.ProbeTooltip);
        Assert.Contains("Duplicate Term Num=1", d.Term1.ProbeTooltip);
        output.WriteLine(d.Term1.ProbeTooltip);
    }

    [Fact]
    public void AnUnboundDesignerSaysThereIsNoSchematic()
    {
        var d = new MatchDesignerViewModel();
        Assert.False(d.Term1.CanProbe);
        Assert.Equal(MatchProbeBlock.NoSchematic, d.Term1.Availability.Block);
    }

    /// <summary>
    /// <b>A purely resistive termination is the SIMPLEST case, and it was the one case the fitter
    /// refused</b> (owner-reported, 2026-08-20, on a schematic whose port 2 is a bare 50 Ω Term:
    /// "the Match designer probe cannot seem to find my simple 50 ohm port 2 termination… I get a
    /// 'None of the four two-element models…' error").
    /// </summary>
    /// <remarks>
    /// Every one of the four models carries a reactance, and <c>ProbeFit.Physical</c> requires its
    /// value to be finite and strictly positive. Against a flat 50 + j0 the least squares put every
    /// reactance at its degenerate end — C = 0 or ∞, L = 0 or ∞ — so all four were non-physical, none
    /// could be applied, and the refusal named a fit problem where there was none. A
    /// <c>ReactanceKind.None</c> termination is first class everywhere else in the Designer (it is
    /// what <c>Termination.Resistive</c> makes, and the kind selector's "–"), so the fitter now
    /// offers it as a fifth model and it wins on residual.
    /// </remarks>
    [Fact]
    public async Task ProbingABarePortResistance_FindsRAlone_RatherThanRefusingAllFourModels()
    {
        var model = Interstage(Band());
        var d = Open(model);

        await d.ProbeAsync(2);

        output.WriteLine(d.Term2.ProbeError);
        foreach (var f in d.Term2.ProbeFits)
            output.WriteLine($"  {f.Name}: {f.ValuesText}  {f.ResidualText}  physical={f.IsPhysical}");

        Assert.Equal("", d.Term2.ProbeError);
        Assert.True(d.Term2.IsProbed);
        Assert.Equal(50.0, d.Design.Term2.R, 1e-6);
        Assert.Equal(ReactanceKind.None, d.Design.Term2.Kind);
        Assert.False(d.Design.Term2.HasReactance);

        // …and it wins because it FITS, not because it was special-cased: a flat 50 Ω is described
        // exactly, so the residual is zero to numerical noise.
        var best = d.Term2.ProbeFits.First();
        Assert.Equal(ReactanceKind.None, best.Fit.Kind);
        Assert.True(best.Fit.Residual < 1e-9, $"residual was {best.Fit.Residual:G6}");
    }

    /// <summary>
    /// <b>The fifth model does not steal a fit that a real reactance explains better.</b>
    /// </summary>
    [Fact]
    public async Task ARealReactiveTermination_StillFitsTheTwoElementModel()
    {
        var model = Interstage(Band());
        var d = Open(model);

        await d.ProbeAsync(1);      // 200 Ω ‖ 0.125 pF

        Assert.Equal("", d.Term1.ProbeError);
        Assert.Equal(ReactanceKind.C, d.Design.Term1.Kind);
        Assert.Equal(TerminationTopology.Parallel, d.Design.Term1.Topology);
        Assert.Equal(200.0, d.Design.Term1.R, 0.2);
    }

    // ── §6 / §10.5 — provenance ──────────────────────────────────────────────

    [Fact]
    public async Task ProbingSetsTheBadge_AndAHandEditClearsIt()
    {
        var model = Interstage(Band());
        var d = Open(model);

        Assert.False(d.Term1.IsProbed);
        await d.ProbeAsync(1);

        Assert.Equal("", d.Term1.ProbeError);
        Assert.True(d.Term1.IsProbed, "the probe did not mark the termination as probed");
        Assert.NotEqual("", d.Term1.ProbeProvenance);
        Assert.Equal(TerminationTopology.Parallel, d.Design.Term1.Topology);
        Assert.Equal(ReactanceKind.C, d.Design.Term1.Kind);
        Assert.Equal(200.0, d.Design.Term1.R, 0.2);
        Assert.Equal(0.125e-12, d.Design.Term1.Value, 0.125e-15);
        output.WriteLine($"probed: R = {d.Design.Term1.R:G6} Ω, C = {d.Design.Term1.Value:G6} F, "
                         + $"{d.Term1.ProbeProvenance}");

        // The provenance survives a save and a reload — it is on the design, not on the view-model.
        Assert.True(StoredDesign(model).Term1.Probed);

        // …and a hand edit takes it back. The user's override always wins and is never silently
        // re-probed, so the badge must not outlive the value it was describing.
        d.Term1.Resistance = 180.0;
        Assert.False(d.Term1.IsProbed);
        Assert.Equal("", d.Term1.ProbeProvenance);
        Assert.False(StoredDesign(model).Term1.Probed);
    }

    [Fact]
    public async Task EveryFitIsListedWithItsResidual()
    {
        var d = Open(Interstage(Band()));
        await d.ProbeAsync(1);

        // FIVE, not four: the four two-element models and R alone, which was added on 2026-08-20
        // because without it a bare port resistance had no representable fit at all. It is ranked by
        // the same residual as the rest and listed like the rest.
        Assert.Equal(5, d.Term1.ProbeFits.Count);
        Assert.Single(d.Term1.ProbeFits.Where(f => f.Fit.Kind == ReactanceKind.None));
        Assert.True(d.Term1.HasProbeResult);
        Assert.Single(d.Term1.ProbeFits.Where(f => f.IsBest));
        Assert.Contains(d.Term1.ProbeFits, f => !f.IsPhysical);   // shown, labelled, not applicable
        foreach (var f in d.Term1.ProbeFits)
        {
            Assert.Contains("mean |ΔΓ|", f.ResidualText);
            output.WriteLine($"{f.Name,-14} {f.ValuesText,-44} {f.ResidualText}  {f.PhysicalNote}");
        }
        Assert.Equal("", d.Term1.ProbeFlag);
    }

    [Fact]
    public async Task ASecondBestFitCanBeTakenByHand_AndIsAlsoProbedProvenance()
    {
        var d = Open(Interstage(Band()));
        await d.ProbeAsync(1);

        var second = d.Term1.ProbeFits.First(f => !f.IsBest && f.IsPhysical);
        second.ApplyCommand.Execute(null);

        Assert.Equal(second.Fit.Topology, d.Design.Term1.Topology);
        Assert.Equal(second.Fit.Kind, d.Design.Term1.Kind);
        Assert.Equal(second.Fit.R, d.Design.Term1.R, second.Fit.R * 1e-9);
        Assert.True(d.Design.Term1.Probed);
    }

    // ── §4 / §10.3 — conjugate ───────────────────────────────────────────────

    [Fact]
    public async Task TheConjugateToggleTurnsAProbedParallelRcIntoAParallelRl()
    {
        var model = Interstage(Band());
        var d = Open(model);

        d.Term1.Conjugate = true;
        Assert.True(StoredDesign(model).Term1Conjugate, "the toggle must survive a reload");

        await d.ProbeAsync(1);

        Assert.Equal(TerminationTopology.Parallel, d.Design.Term1.Topology);
        Assert.Equal(ReactanceKind.L, d.Design.Term1.Kind);
        Assert.Equal(200.0, d.Design.Term1.R, 0.2);

        // §5.1's identity: the target's C_eq at band centre is the measured capacitance.
        Assert.Equal(0.125e-12, d.Design.Term1.CeqAt(d.Design.Omega0), 0.125e-15);
        output.WriteLine($"conjugate target: R = {d.Design.Term1.R:G6} Ω, L = {d.Design.Term1.Value:G6} H");
    }

    [Fact]
    public void TheConjugateNoteIsStatedRatherThanLeftToBeRediscovered()
    {
        // match.md §10.3 asks for this sentence by name; a test rather than a comment because the one
        // thing that can quietly go missing from a UI is a sentence.
        Assert.Contains("small-signal", MatchTerminationViewModel.ConjugateNote);
        Assert.Contains("loadpull", MatchTerminationViewModel.ConjugateNote);
        Assert.Contains("Ropt", MatchTerminationViewModel.ConjugateNote);
    }

    // ── §3 / §14.5 — the threshold is a setting ──────────────────────────────

    [Fact]
    public async Task ThePoorFitWarningThresholdIsASetting()
    {
        var d = Open(Interstage(Band()));
        await d.ProbeAsync(1);
        Assert.Equal("", d.Term1.ProbeFlag);

        d.Settings.ProbeResidualWarning = 1e-18;
        await d.ProbeAsync(1);
        Assert.Contains("not well described by a two-element model", d.Term1.ProbeFlag);
        Assert.True(d.Term1.IsProbed, "a flagged result is still APPLIED — the flag is a warning");
    }
}
