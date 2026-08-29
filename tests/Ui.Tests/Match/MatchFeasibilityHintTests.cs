// ================================================================
//  MatchFeasibilityHintTests.cs  —  match.md §18.10 in the Designer: the Fano ceiling line and its
//  tooltip, the gap-rise note on the Frequency Band card, and the loosen hints under the solutions
//  panel.
//
//  Same discipline as every earlier round: view-model, string and source-scan tests, never pixels.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchFeasibilityHintTests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static MatchDesignerViewModel Open(MatchDesign design)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        model.Components.Add(comp);

        var designer = new MatchDesignerViewModel();
        designer.SetTarget(new SchematicViewModel(model), comp);
        designer.WaitForAnalysis();
        return designer;
    }

    /// <summary>
    /// The owner's report, 2026-08-28: 100 Ω ‖ 0.125 pF into 1.25 Ω + 5 pF series over three bands
    /// that do not mirror — a correct synthesis that produced one flat wideband match.
    /// </summary>
    private static MatchDesign Owner(int order = 2) => new()
    {
        BandCount = 3,
        F1 = 2.5e9, F2 = 3.0e9, F3 = 4.5e9, F4 = 5.0e9, F5 = 9.0e9, F6 = 10.0e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(100.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 5e-12),
    };

    /// <summary>§4.9's interstage problem — a design whose ceiling is not in its way.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static string Xaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml"));
    }

    // ══ 1 — the ceiling line ════════════════════════════════════════════════

    /// <summary>The strip names the wall, which termination sets it, and that the design is on it.</summary>
    [Fact]
    public void TheStatusStrip_QuotesTheFanoCeilingAndSaysWhenTheDesignIsOnIt()
    {
        var d = Open(Owner());
        output.WriteLine(d.Status.Text);

        Assert.Equal(
            "Fano ceiling 6.4 dB (termination 2, over the bands) — at the ceiling",
            d.Status.CeilingText);
        Assert.Equal(2, d.Status.CeilingEnd);
        Assert.Equal(-6.4, d.Status.CeilingDb, 0.1);

        // Between the return-loss line and the gap lines, so the achieved number and the ceiling it
        // is being compared against sit next to each other.
        string text = d.Status.Text;
        Assert.True(text.IndexOf("worst RL", StringComparison.Ordinal)
                    < text.IndexOf("Fano ceiling", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Fano ceiling", StringComparison.Ordinal)
                    < text.IndexOf("gap ", StringComparison.Ordinal));

        d.Dispose();
    }

    /// <summary>The tooltip carries both ends, both other band sets, and the widening's own cost.</summary>
    [Fact]
    public void TheCeilingTooltip_CarriesBothEndsTheOtherBandSetsAndTheWideningCost()
    {
        var d = Open(Owner());
        output.WriteLine(d.Status.CeilingTip);

        // Both ends are quoted over the EFFECTIVE bands, which is the set the line itself is about;
        // -44.8 dB is termination 1 over the outer SPAN and belongs to a different question.
        Assert.Contains("Termination 1: -92.6 dB.", d.Status.CeilingTip, StringComparison.Ordinal);
        Assert.Contains("Termination 2: -6.4 dB.", d.Status.CeilingTip, StringComparison.Ordinal);
        Assert.Contains("Over the bands as typed: -10.7 dB.", d.Status.CeilingTip, StringComparison.Ordinal);
        Assert.Contains("Over the whole span: -3.1 dB.", d.Status.CeilingTip, StringComparison.Ordinal);
        Assert.Contains("Widening to mirror cost 4.3 dB of ceiling.", d.Status.CeilingTip,
                        StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// A design whose ceiling is nowhere near what it achieved says the number and stops there.
    /// </summary>
    [Fact]
    public void ADesignWithHeadroom_QuotesTheCeilingWithoutClaimingToBeOnIt()
    {
        var d = Open(Golden());
        output.WriteLine(d.Status.Text);

        Assert.Equal("Fano ceiling 20.8 dB (termination 2, over the bands)", d.Status.CeilingText);
        Assert.DoesNotContain("at the ceiling", d.Status.CeilingText, StringComparison.Ordinal);
        Assert.Equal("", d.Status.GapText);

        d.Dispose();
    }

    /// <summary>
    /// <b>The ceiling line survives a refusal</b> — which is exactly when it is worth the most.
    /// </summary>
    [Fact]
    public void TheCeilingLine_IsStillShownOnARefusedDesign()
    {
        // A lowpass form cannot absorb a series capacitance, so this refuses before any network
        // exists; the ceiling is arithmetic on the specification and is unaffected.
        var design = Golden();
        design.Form = NetworkForm.Lowpass;
        var d = Open(design);
        output.WriteLine(d.Status.Text);

        Assert.True(d.Status.IsRefused);
        Assert.Equal("", d.Status.ReturnLossText);
        Assert.Contains("Fano ceiling 20.8 dB (termination 2, over the bands)", d.Status.Text,
                        StringComparison.Ordinal);
        Assert.DoesNotContain("at the ceiling", d.Status.CeilingText, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>Two resistive ends have no ceiling, and the line is absent rather than infinite.</summary>
    [Fact]
    public void TwoResistiveEnds_ShowNoCeilingLine()
    {
        var d = Open(new MatchDesign
        {
            F1 = 3.3e9, F2 = 5.0e9, Order = 4,
            Term1 = Termination.Resistive(50.0),
            Term2 = Termination.Resistive(12.5),
        });

        Assert.Equal("", d.Status.CeilingText);
        Assert.Equal("", d.Status.CeilingTip);
        Assert.Equal(0, d.Status.CeilingEnd);
        Assert.DoesNotContain("Fano", d.Status.Text, StringComparison.Ordinal);

        d.Dispose();
    }

    // ══ 2 — the gap-rise note ═══════════════════════════════════════════════

    /// <summary>
    /// At order 2 the owner's tri-band prototype does not exclude the gaps, and the card says so.
    /// </summary>
    [Fact]
    public void TheGapRiseNote_SaysTheGapsAreNotExcludedAndNamesTheOrderThatWouldOpenThem()
    {
        var d = Open(Owner());
        output.WriteLine(d.GapRiseNote);

        Assert.Equal(
            "At order 2 the tri-band prototype does not exclude the gaps — this is a single-band "
            + "match over 2.25–10 GHz (ceiling -3.1 dB). The gaps open at order 4 (rise ×2.9).",
            d.GapRiseNote);

        // And the gap lines carry the factor beside the network's own gap mismatch.
        output.WriteLine(d.Status.GapText);
        Assert.Contains("prototype rise ×0.97", d.Status.GapText, StringComparison.Ordinal);
        Assert.Contains("prototype rise ×0.97", d.Status.GapText2, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>A dual-band design excludes its gap from order 1, so there is no note.</summary>
    [Fact]
    public void ADualBandDesignThatExcludesItsGap_ShowsNoGapRiseNote()
    {
        var d = Open(new MatchDesign
        {
            BandCount = 2, F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Order = 2, Response = ResponseShape.ChebyshevFano,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
            AnalysisEnd = AnalysisEndChoice.Term1,
        });

        Assert.Equal("", d.GapRiseNote);
        Assert.Contains("prototype rise ×", d.Status.GapText, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>Switching back to Single clears the gap note and keeps the ceiling line.</summary>
    [Fact]
    public void SwitchingToSingle_ClearsTheGapNoteAndKeepsTheCeilingLine()
    {
        var d = Open(Owner());
        Assert.NotEqual("", d.GapRiseNote);

        d.BandsChoice = "Single";
        d.WaitForAnalysis();

        Assert.Equal(1, d.BandCount);
        Assert.Equal("", d.GapRiseNote);
        Assert.Equal("", d.Status.GapText);
        Assert.Contains("Fano ceiling", d.Status.CeilingText, StringComparison.Ordinal);

        d.Dispose();
    }

    // ══ 3 — the loosen hints ════════════════════════════════════════════════

    /// <summary>The hint states the wall, what set it, and the four one-variable ways out.</summary>
    [Fact]
    public void TheFeasibilityHint_NamesTheWallAndTheFourRemedies()
    {
        var d = Open(Owner());
        output.WriteLine(d.FeasibilityHint);

        Assert.True(d.SolutionsComplete);
        Assert.NotEqual("", d.FeasibilityHint);

        Assert.StartsWith(
            "The best any lossless network can do here is -6.4 dB, set by termination 2 "
            + "(1.25 Ω + 5 pF series) over 2.25–3 / 4.5–5 / 7.5–10 GHz.",
            d.FeasibilityHint, StringComparison.Ordinal);

        Assert.Contains("To reach -15 dB: termination 2's capacitance at or above 11.7 pF",
                        d.FeasibilityHint, StringComparison.Ordinal);
        Assert.Contains("or band 1 starting at 2.86 GHz instead of 2.25",
                        d.FeasibilityHint, StringComparison.Ordinal);
        Assert.Contains("or without band 1 the ceiling over bands 2 and 3 is -32.1 dB",
                        d.FeasibilityHint, StringComparison.Ordinal);
        Assert.Contains(
            "or band 1 as 2.25–2.5 GHz mirrors band 3 without widening (ceiling -13.8 dB)",
            d.FeasibilityHint, StringComparison.Ordinal);

        // Four clauses, joined the one way.
        Assert.Equal(3, d.FeasibilityHint.Split("; or ").Length - 1);

        // A HINT, never a refusal: the solutions it sits beside still exist.
        Assert.NotEmpty(d.AllSolutions);
        Assert.Equal("", d.SolutionsRefusal);

        d.Dispose();
    }

    /// <summary>A design with headroom gets no hint — the ceiling is not what is stopping it.</summary>
    [Fact]
    public void ADesignWhoseCeilingIsNotInItsWay_GetsNoHint()
    {
        var d = Open(Golden());
        Assert.True(d.SolutionsComplete);
        Assert.Equal("", d.FeasibilityHint);
        d.Dispose();
    }

    // ══ 4 — the window binds all three ══════════════════════════════════════

    /// <summary>
    /// The three slots exist, in their stated places: the ceiling line between the return-loss line
    /// and the gap lines, the gap-rise note under the effective-band note, and the hint beside the
    /// solutions refusal in the same class.
    /// </summary>
    [Fact]
    public void TheWindow_BindsTheCeilingLineTheGapRiseNoteAndTheHint()
    {
        string xaml = Xaml();

        int rl = xaml.IndexOf("Status.ReturnLossText", StringComparison.Ordinal);
        int ceiling = xaml.IndexOf("Status.CeilingText", StringComparison.Ordinal);
        int gap = xaml.IndexOf("Status.GapText}", StringComparison.Ordinal);
        Assert.True(rl >= 0 && ceiling > rl && gap > ceiling,
            "the ceiling line belongs between the return-loss line and the gap lines");
        Assert.Contains("ToolTip.Tip=\"{Binding Status.CeilingTip}\"", xaml, StringComparison.Ordinal);

        int note = xaml.IndexOf("{Binding EffectiveBandNote}", StringComparison.Ordinal);
        int rise = xaml.IndexOf("{Binding GapRiseNote}", StringComparison.Ordinal);
        Assert.True(note >= 0 && rise > note, "the gap-rise note belongs under the effective-band note");
        Assert.Contains("Classes=\"note\" Text=\"{Binding GapRiseNote}\"", xaml, StringComparison.Ordinal);

        int hint = xaml.IndexOf("{Binding FeasibilityHint}", StringComparison.Ordinal);
        Assert.True(hint >= 0);
        Assert.Contains("Classes=\"warn\" Text=\"{Binding FeasibilityHint}\"", xaml,
                        StringComparison.Ordinal);
    }
}
