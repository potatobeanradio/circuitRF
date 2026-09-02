using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for user-addable instance parameters in ParameterEditorViewModel.
/// Covers: CanonicalSort ordering, nextIndex computation, add/remove group logic, and
/// AllowsAddParameter gating per SymbolKind.
/// </summary>
public sealed class ParameterEditorAddParamTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EditableParameter P(string name, string expr = "50", string unit = "") =>
        new() { Name = name, Expression = expr, Unit = unit };

    private static IndexedParamGroup P1ToneTemplate() =>
        ComponentTypeRegistry.UserParamTemplate(SymbolKind.P1Tone)!;

    private static IndexedParamGroup ToneSourceTemplate() =>
        ComponentTypeRegistry.UserParamTemplate(SymbolKind.ToneSource)!;

    private static IndexedParamGroup VarTemplate() =>
        ComponentTypeRegistry.UserParamTemplate(SymbolKind.Var)!;

    // ── TryParseTemplateIndex ─────────────────────────────────────────────────

    [Fact]
    public void TryParseTemplateIndex_P1Tone_ParsesZ()
    {
        var tpl = P1ToneTemplate();
        var r = ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Z[3]");
        Assert.NotNull(r);
        Assert.Equal(3, r!.Value.Index);
        Assert.Equal(0, r.Value.FormatIndex);
    }

    [Fact]
    public void TryParseTemplateIndex_NonIndexedParam_ReturnsNull()
    {
        var tpl = P1ToneTemplate();
        Assert.Null(ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Pavl"));
        Assert.Null(ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Z"));     // no brackets
        Assert.Null(ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Z[1,2]")); // 2D — no match
    }

    [Fact]
    public void TryParseTemplateIndex_ToneSource_ParsesAllFormats()
    {
        var tpl = ToneSourceTemplate();
        var freq = ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Freq[2]");
        var v    = ParameterEditorViewModel.TryParseTemplateIndex(tpl, "V[2]");
        var ph   = ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Phase[2]");

        Assert.Equal((2, 0), (freq!.Value.Index, freq.Value.FormatIndex));
        Assert.Equal((2, 1), (v!.Value.Index, v.Value.FormatIndex));
        Assert.Equal((2, 2), (ph!.Value.Index, ph.Value.FormatIndex));
    }

    [Fact]
    public void TryParseTemplateIndex_Var_ParsesVar1()
    {
        var tpl = VarTemplate();
        var r = ParameterEditorViewModel.TryParseTemplateIndex(tpl, "Var1");
        Assert.NotNull(r);
        Assert.Equal((1, 0), (r!.Value.Index, r.Value.FormatIndex));
    }

    // ── ComputeNextIndex ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeNextIndex_P1Tone_EmptySkipsIndex1()
    {
        // No Z[k] present → first add gives Z[0] (DC harmonic).
        var tpl    = P1ToneTemplate();
        var existing = new List<EditableParameter> { P("Pavl"), P("Z"), P("Freq"), P("Phase") };
        Assert.Equal(0, ParameterEditorViewModel.ComputeNextIndex(tpl, existing));
    }

    [Fact]
    public void ComputeNextIndex_P1Tone_AfterZ0_SkipsZ1()
    {
        // Z[0] present → next = Z[2] (Z[1] skipped).
        var tpl    = P1ToneTemplate();
        var existing = new List<EditableParameter> { P("Pavl"), P("Z"), P("Z[0]") };
        Assert.Equal(2, ParameterEditorViewModel.ComputeNextIndex(tpl, existing));
    }

    [Fact]
    public void ComputeNextIndex_P1Tone_AfterZ0_Z2_GivesZ3()
    {
        var tpl    = P1ToneTemplate();
        var existing = new List<EditableParameter> { P("Z[0]"), P("Z[2]") };
        Assert.Equal(3, ParameterEditorViewModel.ComputeNextIndex(tpl, existing));
    }

    [Fact]
    public void ComputeNextIndex_ToneSource_NoIndexed_Gives2()
    {
        // ToneSource starts at FirstAddIndex=2.
        var tpl    = ToneSourceTemplate();
        var existing = new List<EditableParameter> { P("V"), P("Freq") };
        Assert.Equal(2, ParameterEditorViewModel.ComputeNextIndex(tpl, existing));
    }

    [Fact]
    public void ComputeNextIndex_ToneSource_After2_Gives3()
    {
        var tpl    = ToneSourceTemplate();
        var existing = new List<EditableParameter> { P("Freq[1]"), P("V[1]"), P("Freq[2]"), P("V[2]"), P("Phase[2]") };
        Assert.Equal(3, ParameterEditorViewModel.ComputeNextIndex(tpl, existing));
    }

    // ── FindTopGroupIndex ─────────────────────────────────────────────────────

    [Fact]
    public void FindTopGroupIndex_P1Tone_NoIndexedParams_ReturnsMinus1()
    {
        var tpl    = P1ToneTemplate();
        var existing = new List<EditableParameter> { P("Pavl"), P("Z"), P("Freq") };
        Assert.Equal(-1, ParameterEditorViewModel.FindTopGroupIndex(tpl, existing));
    }

    [Fact]
    public void FindTopGroupIndex_P1Tone_Z0_Z2_Returns2()
    {
        var tpl    = P1ToneTemplate();
        var existing = new List<EditableParameter> { P("Z[0]"), P("Z[2]") };
        Assert.Equal(2, ParameterEditorViewModel.FindTopGroupIndex(tpl, existing));
    }

    [Fact]
    public void FindTopGroupIndex_ToneSource_Freq2_Returns2()
    {
        var tpl    = ToneSourceTemplate();
        var existing = new List<EditableParameter> { P("Freq[1]"), P("V[1]"), P("Freq[2]"), P("V[2]") };
        Assert.Equal(2, ParameterEditorViewModel.FindTopGroupIndex(tpl, existing));
    }

    // ── CanonicalSort ─────────────────────────────────────────────────────────

    [Fact]
    public void CanonicalSort_P1Tone_NonIndexedFirst_ThenByIndex()
    {
        // Input: out-of-order Z[k] mixed with fixed params
        var tpl = P1ToneTemplate();
        var input = new List<EditableParameter>
        {
            P("Pavl"), P("Z"), P("Z[2]"), P("Z[0]"), P("Freq"), P("Phase"),
        };

        var sorted = ParameterEditorViewModel.CanonicalSort(tpl, input);

        // Non-indexed first (in original order), then Z[0], Z[2]
        Assert.Equal(["Pavl", "Z", "Freq", "Phase", "Z[0]", "Z[2]"],
                     sorted.Select(p => p.Name));
    }

    [Fact]
    public void CanonicalSort_ToneSource_GroupsInIndexOrder()
    {
        var tpl = ToneSourceTemplate();
        var input = new List<EditableParameter>
        {
            P("NumFreqs", "2"), P("Freq[2]"), P("V[2]"), P("Phase[2]"),
            P("Freq[1]"), P("V[1]"),
        };

        var sorted = ParameterEditorViewModel.CanonicalSort(tpl, input);

        // NumFreqs (non-indexed) first, then group 1, then group 2
        Assert.Equal(["NumFreqs", "Freq[1]", "V[1]", "Freq[2]", "V[2]", "Phase[2]"],
                     sorted.Select(p => p.Name));
    }

    [Fact]
    public void CanonicalSort_AlreadySorted_SameOrder()
    {
        var tpl = P1ToneTemplate();
        var input = new List<EditableParameter> { P("Pavl"), P("Z"), P("Z[0]"), P("Z[2]") };
        var sorted = ParameterEditorViewModel.CanonicalSort(tpl, input);
        Assert.Equal(input.Select(p => p.Name), sorted.Select(p => p.Name));
    }

    // ── MigrateToneSourceToIndexed ────────────────────────────────────────────

    [Fact]
    public void MigrateToneSource_ScalarToIndexed_RenamesAndAddsNumFreqs()
    {
        var input = new List<EditableParameter>
        {
            P("V", "1", "V"), P("Freq", "2", "GHz"), P("Phase", "30", "deg"),
        };

        var result = ParameterEditorViewModel.MigrateToneSourceToIndexed(input);

        Assert.Contains(result, p => p.Name == "V[1]"    && p.Expression == "1");
        Assert.Contains(result, p => p.Name == "Freq[1]" && p.Expression == "2");
        Assert.Contains(result, p => p.Name == "NumFreqs" && p.Expression == "1");
        Assert.DoesNotContain(result, p => p.Name == "V");
        Assert.DoesNotContain(result, p => p.Name == "Freq");

        // Phase migrates WITH them, unit and all: the multi-tone factory branch reads Phase[i] and
        // nothing else, so a scalar Phase left behind would be dropped the moment a second tone was
        // added — tone 1 would quietly lose its angle.
        Assert.Contains(result, p => p.Name == "Phase[1]" && p.Expression == "30" && p.Unit == "deg");
        Assert.DoesNotContain(result, p => p.Name == "Phase");
    }

    [Fact]
    public void MigrateToneSource_AlreadyIndexed_NoOp()
    {
        var input = new List<EditableParameter>
        {
            P("Freq[1]"), P("V[1]"), P("NumFreqs", "1"),
        };

        var result = ParameterEditorViewModel.MigrateToneSourceToIndexed(input);
        // Same object returned (no copy) — already indexed
        Assert.Same(input, result);
    }

    // ── CountToneGroups ───────────────────────────────────────────────────────

    [Fact]
    public void CountToneGroups_TwoFreqParams_Returns2()
    {
        var ps = new List<EditableParameter> { P("Freq[1]"), P("V[1]"), P("Freq[2]"), P("V[2]"), P("Phase[2]") };
        Assert.Equal(2, ParameterEditorViewModel.CountToneGroups(ps));
    }

    // ── AllowsAddParameter gating ─────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.P1Tone,    true)]
    [InlineData(SymbolKind.PnTone,    true)]
    [InlineData(SymbolKind.ToneSource, true)]
    // ZPort and SDD both lost the generic "+" on 2026-09-02, for different reasons: a ZNP has
    // nothing addable at all (its Z[p,q] matrix is exactly its port count, seeded at placement, and
    // the Z[n] the button used to add was read by nothing), while an SDD has plenty addable but not
    // in a shape one increasing index can express — it gets its own picker.
    [InlineData(SymbolKind.ZPort,     false)]
    [InlineData(SymbolKind.Sdd,       false)]
    [InlineData(SymbolKind.Var,       true)]
    [InlineData(SymbolKind.Resistor,  false)]
    [InlineData(SymbolKind.Inductor,  false)]
    [InlineData(SymbolKind.Capacitor, false)]
    [InlineData(SymbolKind.Vdc, false)]
    [InlineData(SymbolKind.Term,      false)]
    [InlineData(SymbolKind.Pin,       false)]
    [InlineData(SymbolKind.Ground,    false)]
    public void AllowsAddParameter_CorrectForKind(SymbolKind kind, bool expected)
        => Assert.Equal(expected, ComponentTypeRegistry.AllowsIndexedParamAdd(kind));

    /// <summary>The ZNP has no template at all — which is also what makes its row names
    /// non-editable, since a renamed Z[i,j] is read by nothing.</summary>
    [Fact]
    public void ZPort_HasNoIndexedTemplate()
        => Assert.Null(ComponentTypeRegistry.UserParamTemplate(SymbolKind.ZPort));

    /// <summary>The SDD keeps its template — it still drives row-name editing and canonical
    /// sorting — and is excluded from the "+" by the predicate above, not by losing it.</summary>
    [Fact]
    public void Sdd_KeepsItsTemplateEvenThoughThePlusButtonIsGone()
        => Assert.NotNull(ComponentTypeRegistry.UserParamTemplate(SymbolKind.Sdd));

    // ── ZNP row removal ───────────────────────────────────────────────────────
    //
    // A Z[i,j] entry is structural — its existence is the port count — so it is never removable.
    // Anything else on a ZNP is, and that is not hypothetical: it is how a design already carrying
    // an inert Z[n] from the old "+" gets rid of it, now that both the button and the row rename
    // are gone.

    [Theory]
    [InlineData("Z[1,1]", false)]
    [InlineData("Z[2,1]", false)]
    [InlineData("NumPorts", false)]
    [InlineData("Z[1]", true)]     // the stray the old "+" created
    [InlineData("Z[3]", true)]
    public void ZPort_RemovableParameters(string name, bool expected)
        => Assert.Equal(expected, ComponentTypeRegistry.IsRemovableParameter(SymbolKind.ZPort, name));

    /// <summary>Every visible SDD row is a user-authored equation or a named constant, each
    /// independent of its neighbours — so each gets its own "✕". That is also what the equation
    /// picker implies: one named slot added, one named slot removed.</summary>
    [Theory]
    [InlineData("I[1,0]")]
    [InlineData("V[2]")]
    [InlineData("H[2]")]
    [InlineData("Param1")]
    public void Sdd_EveryEquationRowIsRemovable(string name)
        => Assert.True(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.Sdd, name));
}
