// ================================================================
//  MatchRound7Tests.cs  —  the owner's 2026-08-20 round-7 list for the Match Designer, plus the
//  three application-wide defects it turned up (the project-tree dirty indicator, the
//  IconSelectButton popup crash, and base64 in a .csch).
//
//  Same discipline as rounds 1-6: view-model, geometry and source-scan tests, never pixels. Where the
//  ask is about layout declared in AXAML, or about a wiring path that needs a live Avalonia
//  application, the assertion is made against the source the mechanism is written in and NAMES that
//  mechanism — a scan for "the file mentions the word" would pass over a broken fix.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound7Tests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign? design = null)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        if (design is not null)
            comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);

        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    /// <summary>
    /// The owner's own reported case: 50 Ω into 5 Ω ∥ 1 pF over 1.8-2.2 GHz.
    /// </summary>
    private static MatchDesign OwnersProblem(int order = 3) => new()
    {
        F1 = 1.8e9,
        F2 = 2.2e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
        Term2 = Termination.Resistive(50.0),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One source file with its COMMENTS STRIPPED — every scan below is about what the code
    /// does, and a comment that quotes the thing it replaced is what a naive scan trips over.</summary>
    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);
        return raw;
    }

    private static string Xaml() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");
    private static string Code() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");

    /// <summary>The body of the method whose signature line is <paramref name="signature"/>.</summary>
    private static string Between(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source any more");
        int open = src.IndexOf('{', i);
        int depth = 0;
        for (int k = open; k < src.Length; k++)
        {
            if (src[k] == '{') depth++;
            else if (src[k] == '}' && --depth == 0) return src[open..(k + 1)];
        }
        Assert.Fail($"'{signature}' has no closing brace");
        return "";
    }

    // ══ 1 — the grid area's error message goes away ══════════════════════════

    /// <summary>
    /// <b>Owner-reported:</b> "sometimes the error messages in the grid area (below schematic) don't
    /// go away. For example, I will change a termination value and the error is still there."
    /// </summary>
    /// <remarks>
    /// The note was cleared only on the way INTO the next inline edit, so a refusal about an element
    /// outlived the ladder it was about. Asserted through a REAL refusal and a REAL unrelated edit
    /// rather than by poking the property, because the claim is about the two being connected.
    /// </remarks>
    [Fact]
    public void AnInlineEditNote_IsClearedByTheNextChangeToTheDesign()
    {
        var (_, _, d) = Open();

        // A value no transform in this rack can reach, on an element the synthesis owns.
        var target = d.ResolveInlineEdit(d.Ladder.Elements.First(e => e.Role == MatchElementRole.Normal).Name);
        Assert.NotNull(target);
        Assert.False(d.CommitInlineEdit(target!, "not a number"));
        Assert.NotEmpty(d.InlineEditNote);
        output.WriteLine("note: " + d.InlineEditNote);

        // Any other edit — the owner's own example is a termination value.
        d.Term2.Resistance = 12.0;
        Assert.Equal("", d.InlineEditNote);

        d.Dispose();
    }

    /// <summary>
    /// ...and the ONE note that is set after its own refresh still survives it: an element value the
    /// rack moved as close as it could is reported, and reporting it is not undone by the rebuild that
    /// produced it.
    /// </summary>
    [Fact]
    public void TheClear_DoesNotEatTheNoteItsOwnRefreshProduces()
    {
        // RefreshCore, not Refresh: the public entry point is now a one-line lock around it (see
        // MatchDesignerViewModel._refreshGate). The body that clears the note is the same body.
        string body = Between(
            Src("src", "Ui", "Match", "MatchDesignerViewModel.cs"), "private void RefreshCore(bool specChanged)");
        Assert.Contains("InlineEditNote = \"\";", body, StringComparison.Ordinal);

        // SetElementValue sets the note AFTER its Refresh, which is what makes the clear safe. The
        // BODY is SetElementValueCore since 2026-08-28 — the public method is a one-line hold against
        // the analysis landings around it (MatchDesignerViewModel.AsOneEdit), exactly as Refresh is a
        // one-line lock around RefreshCore above. The body that matters is the same body.
        string solve = Between(
            Src("src", "Ui", "Match", "MatchDesignerViewModel.InlineEdit.cs"),
            "private bool SetElementValueCore(string name, double target)");
        int refresh = solve.IndexOf("Refresh(specChanged: false);", StringComparison.Ordinal);
        int note = solve.LastIndexOf("InlineEditNote =", StringComparison.Ordinal);
        Assert.True(refresh > 0 && note > refresh,
            "SetElementValue must write its note after the Refresh that clears it");
    }

    // ══ 2 — the R/L/C units follow the Settings flyout ═══════════════════════

    /// <summary>
    /// <b>Owner-reported:</b> "after a probe, the units in the R, L and C do not match the units set
    /// in the Match Designer settings."
    /// </summary>
    [Fact]
    public void TheTerminationFields_RenderInTheSettingsUnits()
    {
        var (_, _, d) = Open();
        d.Settings.CapacitanceUnit = "fF";
        d.Settings.ResistanceUnit = "kΩ";

        d.Term1.Kind = ReactanceKind.C;
        d.Term1.Reactance = 1e-12;

        Assert.Equal("kΩ", d.Term1.ResistanceUnit);
        Assert.Equal("fF", d.Term1.ReactanceUnit);
        Assert.EndsWith("fF", d.Term1.ReactanceEntry, StringComparison.Ordinal);
        Assert.EndsWith("kΩ", d.Term1.ResistanceEntry, StringComparison.Ordinal);

        // An inductive end reads the INDUCTANCE setting, not the capacitance one.
        d.Settings.InductanceUnit = "nH";
        d.Term1.Kind = ReactanceKind.L;
        Assert.Equal("nH", d.Term1.ReactanceUnit);

        d.Dispose();
    }

    /// <summary>A unit the user TYPES still pins the field — the settings unit is the default, not a
    /// override of the person.</summary>
    [Fact]
    public void ATypedUnit_StillPinsTheField_AndAProbeUnpinsIt()
    {
        var (_, _, d) = Open();
        d.Settings.CapacitanceUnit = "pF";
        d.Term1.Kind = ReactanceKind.C;

        d.Term1.ReactanceEntry = "330 fF";
        Assert.Equal("fF", d.Term1.ReactanceUnit);
        Assert.Equal(330e-15, d.Term1.Reactance, 1e-18);

        // A probe writes a value nobody typed, so the typed unit has nothing to say about it.
        d.Term1.ResetDisplayUnits();
        Assert.Equal("pF", d.Term1.ReactanceUnit);

        d.Dispose();
    }

    /// <summary>Both probe-application paths drop the pinned units before they write.</summary>
    [Fact]
    public void BothProbePaths_ResetTheDisplayUnitsBeforeApplying()
    {
        string src = Src("src", "Ui", "Match", "MatchTerminationViewModel.cs");

        foreach (string signature in
                 new[] { "internal void ApplyProbeFit(MatchProbeFitRowViewModel row)",
                         "internal void ShowProbeResult(TerminationProbe.ProbeResult result)" })
        {
            string body = Between(src, signature);
            int reset = body.IndexOf("ResetDisplayUnits();", StringComparison.Ordinal);
            int apply = body.IndexOf("ApplyProbedTermination", StringComparison.Ordinal);
            Assert.True(reset > 0, $"{signature} does not reset the display units");
            Assert.True(apply > reset, $"{signature} applies before it resets");
        }
    }

    // ══ 3 — the grid's value column is an editor ═════════════════════════════

    /// <summary>
    /// <b>Owner:</b> "give the component values in the grid view (below the schematic) the same inline
    /// text editor option as what we use for the schematic (ie. the ability for user to change them,
    /// with the exact same value validator as what is used in the schematic)."
    /// </summary>
    [Fact]
    public void TheGridValue_EditsThroughTheSchematicsOwnValidator()
    {
        string xaml = Xaml();
        Assert.Contains("<ctl:InlineEditText Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ValueEntry, Mode=TwoWay}", xaml, StringComparison.Ordinal);

        // "The exact same validator" is taken literally — there is no second one.
        string row = Src("src", "Ui", "Match", "MatchElementRowViewModel.cs");
        Assert.Contains("ResolveInlineEdit(Name)", row, StringComparison.Ordinal);
        Assert.Contains("CommitInlineEdit(target, value)", row, StringComparison.Ordinal);
    }

    /// <summary>A refused grid edit changes nothing and says why, exactly as the canvas does.</summary>
    [Fact]
    public void ARefusedGridEdit_LeavesTheDesignAloneAndReports()
    {
        var (_, _, d) = Open();
        var row = d.Elements.First(r => r.Role == MatchElementRole.Normal);
        double before = row.Value;

        row.ValueEntry = "50+j10";
        Assert.Equal(before, d.Elements.First(r => r.Name == row.Name).Value);
        Assert.Contains("real", d.InlineEditNote, StringComparison.OrdinalIgnoreCase);

        d.Dispose();
    }

    /// <summary>
    /// The grid's rows are UPDATED, never replaced — an editor cannot live in a container the model
    /// destroys under it, and committing a value is precisely what rebuilds the grid.
    /// </summary>
    [Fact]
    public void TheGridRows_AreReusedAcrossARebuild()
    {
        var (_, _, d) = Open();
        var first = d.Elements[0];
        int count = d.Elements.Count;

        d.Refresh(specChanged: false);

        Assert.Equal(count, d.Elements.Count);
        Assert.Same(first, d.Elements[0]);
        d.Dispose();
    }

    // ══ 4 — the window is 100 px shorter, at the golden ratio ════════════════

    /// <summary>
    /// <b>Owner:</b> "reduce the Match Designer window height by ~100 pixels. Reduce width to maintain
    /// the Golden ratio."
    /// </summary>
    [Fact]
    public void TheWindow_Is100PxShorter_AndStillGolden()
    {
        var size = Regex.Match(Xaml(), @"Width=""(\d+)""\s+Height=""(\d+)""");
        Assert.True(size.Success);
        double w = double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        double h = double.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);

        Assert.Equal(741, h);                       // 841 - 100
        Assert.Equal(1.618, w / h, 3);
        Assert.True(h > 700, "the opening height must clear MinHeight, or the window opens resized");
    }

    // ══ 5 — a readable .csch, a bare .cnl token ══════════════════════════════

    /// <summary>
    /// <b>Owner-reported:</b> "the .csch is showing a Match component instance with Expression all
    /// crazy text. Recall that all circuitRF file formats are supposed to be human readable."
    /// </summary>
    /// <remarks>
    /// Both halves in one test, because the point is that they are two formats and not two payloads:
    /// the component parameter — which is what a <c>.csch</c> stores verbatim — is readable JSON, and
    /// the <c>.cnl</c> writer converts it to a bare token on the way out because that format cannot
    /// hold a quote. Then it is read back and elaborated, so "readable" is not bought with a file that
    /// no longer loads.
    /// </remarks>
    [Fact]
    public void TheStoredDesign_IsReadableJson_AndStillSurvivesTheNetlistFile()
    {
        var (schematic, comp, d) = Open(OwnersProblem());

        string stored = comp.Parameters.First(p => p.Name == "Design").Expression;
        Assert.StartsWith("{", stored, StringComparison.Ordinal);
        Assert.Contains("\"F1\":1800000000", stored, StringComparison.Ordinal);
        Assert.Contains("\"Order\":3", stored, StringComparison.Ordinal);

        var extracted = NetExtractor.Extract(schematic.EditModel);
        string cnl = CnlWriter.Write(extracted.TestBench, extracted.Library, "round7");
        string line = cnl.Split('\n').Single(l => l.TrimStart().StartsWith("Match:", StringComparison.Ordinal));
        output.WriteLine(line);

        // One bare token on that line: no quote, no space inside the payload, no trailing '='.
        string token = line.Split("Design=", StringSplitOptions.None)[1].Split(' ')[0];
        Assert.DoesNotContain('"', token);
        Assert.False(token.EndsWith('='), "a padded token makes CnlReader swallow the next parameter");
        Assert.True(MatchEmbedding.TryDecode(token, out var back) && back is not null);
        Assert.Equal(1.8e9, back!.F1);
        Assert.Equal(3, back.Order);

        d.Dispose();
    }

    // ══ 6 — 50 Ω into 5 Ω ∥ 1 pF: the panel OFFERS what reaches it ══════════

    /// <summary>
    /// <b>Owner-reported:</b> "I find it hard to believe that the Match component cannot match a 50
    /// ohm termination to a parallel RC of 5 ohms // 1pF at 2 GHz. Am I doing something wrong?"
    /// </summary>
    /// <remarks>
    /// <b>It can, and since 2026-08-28 the Designer simply shows how, with nothing to set.</b>
    ///
    /// <para>The first answer to this (2026-08-20) was a refusal that NAMED the orders that reach it,
    /// because the panel showed one order in one family and the user had to go and pick another.
    /// That was the repository's standing lesson about refusals — a remedy is only a remedy if it
    /// BINDS — and this round takes it one step further: the list spans every permitted order and
    /// family, so opening the problem at order 3 puts the networks that reach it on screen as rows.
    /// A remedy you can click beats one you have to go and set.</para>
    ///
    /// <para><b>Order 3 reaches it too</b>, which the old search could not see. Every cell is now
    /// searched with negative components permitted, and <c>MatchSolutionSearch.FindQAdjust</c>
    /// therefore finds the Q-adjust that completes at order 3 — an all-positive, buildable network at
    /// about 37 dB return loss. Under the old flag it bisected inside a clamped rack, failed, and
    /// offered nothing. So this test asserts what the user gets rather than which order they have to
    /// use: buildable rows exist, and applying one matches.</para>
    /// </remarks>
    [Fact]
    public void TheOwnersProblem_IsOfferedAsRows_AtEveryOrderThatReachesIt()
    {
        var (_, _, d) = Open(OwnersProblem(order: 3));
        d.WaitForAnalysis();

        var buildable = d.AllSolutions
            .Where(r => !r.HasNegativeComponents && !r.Solution.ImplausibleValues)
            .ToList();
        Assert.NotEmpty(buildable);
        output.WriteLine($"{d.AllSolutions.Count} solutions listed, {buildable.Count} of them buildable: "
                         + string.Join(", ", buildable.Select(r => r.TitleText).Distinct()));

        // Order 4 was the one the old refusal named, and it is still there — now as a row.
        var atFour = buildable.Where(r => r.Order == 4).ToList();
        Assert.NotEmpty(atFour);

        atFour[0].Apply();
        d.WaitForAnalysis();

        Assert.Equal(4, d.Design.Order);
        double worst = d.Status.WorstReturnLossDb;
        output.WriteLine($"order 4 worst in-band return loss: {worst:F2} dB");
        Assert.True(worst < -20.0, $"order 4 should match this pair; got {worst:F2} dB");
        Assert.True(d.Status.OnTarget, "Π N² should be on target once a solution is applied");
        d.Dispose();
    }

    // ══ 7 — the "(target …)" annotation, and what the editor opens with ══════

    /// <summary>
    /// <b>Owner-reported:</b> "Terminal 2 is matched and I get no warnings, but the schematic instance
    /// still says 'Z = 50 Ω (target 50 Ω)'. It should just say 'Z = 50 Ω'."
    /// </summary>
    /// <remarks>
    /// Driven through a design whose declared resistance is the 49.999999999999993 a PROBE writes —
    /// the exact shape of the owner's own file — because the bug was a numeric tolerance sitting
    /// under a rendered label, and a test on a round 50 Ω could never see it.
    /// </remarks>
    [Fact]
    public void AMatchedTermination_DoesNotAnnounceATargetItAlreadyMeets()
    {
        var design = OwnersProblem(order: 4);
        design.Term2 = Termination.Resistive(49.999999999999993);

        var (_, _, d) = Open(design);
        d.WaitForAnalysis();
        d.Solutions.First(s => !s.Solution.ImplausibleValues).Apply();

        string label = d.Ladder.Terminations.Single(t => t.End == 2).ResistanceText;
        output.WriteLine("termination 2 label: " + label);
        Assert.DoesNotContain("target", label, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>...and a real disagreement is still stated.</summary>
    [Fact]
    public void AnUnreachedTermination_StillAnnouncesItsTarget()
    {
        var (_, _, d) = Open(OwnersProblem(order: 3));     // no solutions: Π N² stays at 1
        string label = d.Ladder.Terminations.Single(t => t.End == 2).ResistanceText;
        output.WriteLine("termination 2 label: " + label);
        Assert.Contains("target 50", label, StringComparison.Ordinal);
        d.Dispose();
    }

    /// <summary>
    /// <b>Owner-reported:</b> "when I use inline text editor on a TermG schematic component that is
    /// not matched and has a 'target …' suffix, the inline editor includes the '(target…' suffix. This
    /// makes it hard to change the TermG value because anything with that suffix gets rejected."
    /// </summary>
    [Fact]
    public void TheInlineEditor_OpensOnTheValueAlone_NeverTheAnnotation()
    {
        var (_, _, d) = Open(OwnersProblem(order: 3));

        var label = d.Ladder.Terminations.Single(t => t.End == 2).ResistanceText;
        Assert.Contains("target", label, StringComparison.Ordinal);      // the drawing does annotate

        var target = d.ResolveInlineEdit("Termination 2");
        Assert.NotNull(target);
        Assert.DoesNotContain("target", target!.SeedText, StringComparison.Ordinal);
        Assert.Equal("50 Ω", target.SeedText);

        // ...and what it opens with is a string its own commit accepts unchanged — the round trip the
        // suffix was breaking.
        Assert.False(d.CommitInlineEdit(target, target.SeedText));       // unchanged is not an edit
        Assert.Equal("", d.InlineEditNote);                              // and is NOT a refusal
        Assert.True(d.CommitInlineEdit(target, "75 Ω"));
        Assert.Equal(75.0, d.Design.Term2.R);

        d.Dispose();
    }

    /// <summary>The window seeds the box from the target, not from what the canvas drew.</summary>
    [Fact]
    public void TheWindow_SeedsTheEditorFromTheTarget()
    {
        string body = Between(Code(), "private void OnSchematicLabelDoubleTapped(");
        Assert.Contains("_labelEditor.Open(target.SeedText,", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_labelEditor.Open(at.Text,", body, StringComparison.Ordinal);
    }

    // ══ 8 — an absorbed L or C is a specification input and is editable ══════

    /// <summary>
    /// <b>Owner-reported:</b> "I could not change the C in the schematic that was part of the
    /// specification. Any L or C that is part of the specification should always be editable using the
    /// inline text editor (just as the TermG component is today)."
    /// </summary>
    [Fact]
    public void AnAbsorbedElement_EditsTheTerminationItBelongsTo()
    {
        var (_, _, d) = Open(OwnersProblem());

        var absorbed = d.Ladder.Elements.Single(e => e.Role == MatchElementRole.Absorbed);
        Assert.Equal(1, absorbed.AbsorbedEnd);

        var target = d.ResolveInlineEdit(absorbed.Name);
        Assert.NotNull(target);
        Assert.Equal(MatchInlineEditKind.TerminationReactance, target!.Kind);
        Assert.Equal(1, target.End);
        Assert.Equal("1 pF", target.SeedText);

        Assert.True(d.CommitInlineEdit(target, "2 pF"));
        Assert.Equal(2e-12, d.Design.Term1.Value, 1e-24);
        Assert.Equal("", d.InlineEditNote);           // never "add a transform" — nothing was refused

        d.Dispose();
    }

    /// <summary>
    /// The reactance tolerance is RELATIVE. An absolute floor of 1e-12 — which is what a resistance's
    /// "1e-12 × max(1, R)" becomes when the quantity is farads — swallowed 1 pF → 2 pF whole.
    /// </summary>
    [Fact]
    public void ASubPicofaradEdit_IsNotRoundedAwayAsNoChange()
    {
        var (_, _, d) = Open(OwnersProblem());
        var target = d.ResolveInlineEdit(
            d.Ladder.Elements.Single(e => e.Role == MatchElementRole.Absorbed).Name);

        Assert.True(d.CommitInlineEdit(target!, "1.2 pF"));
        Assert.Equal(1.2e-12, d.Design.Term1.Value, 1e-24);
        d.Dispose();
    }

    // ══ 8b — a specification change re-solves the rack (2026-08-20, later the same day) ══

    /// <summary>
    /// <b>Owner-reported:</b> "when I change the Filter Response, sometimes the Termination indicates
    /// unsatisfied, but if I tweak a slider transformer, the termination becomes satisfied — even when
    /// I slide it past the transform value it was before."
    /// </summary>
    /// <remarks>
    /// Every response family asks for a DIFFERENT Π N². The rack was left where it was, so the far
    /// termination stopped presenting the declared resistance until the user happened to touch a
    /// slider — at which point <c>SetTransformN</c>'s own linkage quietly did the work the spec edit
    /// should have done, which is why the same N could arrive at two different verdicts.
    ///
    /// <para>Driven on all three axes that move the target, and each one asserts the SECOND half too:
    /// re-driving a transform at the N it already has must now change nothing, because there is
    /// nothing left for it to absorb.</para>
    /// </remarks>
    [Theory]
    [InlineData("response")]
    [InlineData("order")]
    [InlineData("band")]
    public void ASpecificationChange_ReSolvesTheRack_SoNoSliderNudgeIsNeeded(string axis)
    {
        var start = OwnersProblem(order: 4);
        start.LinkTransforms = true;

        var (_, _, d) = Open(start);
        d.WaitForAnalysis();
        d.Solutions.First(s => !s.Solution.ImplausibleValues).Apply();
        Assert.True(d.Status.OnTarget, "the fixture must start matched, or it proves nothing");

        switch (axis)
        {
            case "response": d.Response = ResponseShape.ChebyshevTwoEnded; break;
            case "order":    d.Order = 6; break;
            default:         d.F2 = 2.6e9; break;
        }

        output.WriteLine($"{axis}: {d.Status.RatioText}");
        Assert.True(d.Status.OnTarget, $"a {axis} change left Π N² off target: {d.Status.RatioText}");
        Assert.DoesNotContain(
            "target", d.Ladder.Terminations.Single(t => t.End == 2).ResistanceText, StringComparison.Ordinal);

        // ...and the slider nudge that used to be the fix is now a no-op.
        var before = d.Design.Transforms.Select(t => t.N).ToArray();
        d.SetTransformN(0, d.Design.Transforms[0].N);
        Assert.Equal(before, d.Design.Transforms.Select(t => t.N).ToArray());

        d.Dispose();
    }

    /// <summary>
    /// The relink is a no-op when there is nothing to absorb — otherwise every specification edit
    /// would put a transform write on the schematic's undo stack for a value nobody changed.
    /// </summary>
    [Fact]
    public void ASpecChangeThatDoesNotMoveTheTarget_WritesNoTransform()
    {
        var start = OwnersProblem(order: 4);
        start.LinkTransforms = true;

        var (schematic, _, d) = Open(start);
        d.WaitForAnalysis();
        d.Solutions.First(s => !s.Solution.ImplausibleValues).Apply();

        var before = d.Design.Transforms.Select(t => t.N).ToArray();
        d.AllowNegativeComponents = !d.AllowNegativeComponents;   // permits more; requires the same

        Assert.True(d.Status.OnTarget);
        Assert.Equal(before, d.Design.Transforms.Select(t => t.N).ToArray());
        d.Dispose();
    }

    /// <summary>
    /// <b>Every specification setter goes through the one door</b>, so the next one added cannot
    /// reintroduce this. <c>RelinkAfterSpecChange</c> had a single caller for exactly that reason.
    /// </summary>
    [Fact]
    public void EverySpecificationSetter_CommitsThroughTheSharedEntryPoint()
    {
        string src = Src("src", "Ui", "Match", "MatchDesignerViewModel.cs");

        foreach (string field in new[]
                 {
                     "_design.Order = value;", "_design.Response = value;", "_design.RippleDb = value;",
                     "_design.QAdjust = value;", "_design.AnalysisEnd = value;",
                     "_design.F1 = value;", "_design.F2 = value;",
                     "_design.AllowNegativeComponents = value;",
                 })
        {
            int i = src.IndexOf(field, StringComparison.Ordinal);
            Assert.True(i > 0, $"{field} is no longer in the source");
            string tail = src[i..Math.Min(src.Length, i + 260)];
            Assert.Contains("CommitSpecChange();", tail, StringComparison.Ordinal);
        }

        // SetTermination shares it now rather than owning a copy.
        Assert.Contains("CommitSpecChange();",
                        Between(src, "internal void SetTermination("), StringComparison.Ordinal);

        // ...and LOADING a design still must not rewrite it.
        foreach (string signature in new[]
                 {
                     "public void SetTarget(SchematicViewModel schematicVm, EditableComponent comp)",
                     "private void OnModelChanged(object? sender, EventArgs e)",
                     "public void Revert()",
                 })
            Assert.DoesNotContain("CommitSpecChange();", Between(src, signature), StringComparison.Ordinal);
    }

    // ══ 8c — the Filter Response card's height, and the window title ════════

    /// <summary>
    /// <b>Owner:</b> "reduce the text below the Filter Response (or else delete it and add it back as
    /// a tooltip addition in the combobox). There is not enough height space for the current amount of
    /// text."
    /// </summary>
    /// <remarks>
    /// The paragraph moved, it was not dropped: deleting it outright would put back the round-6 bug it
    /// was written for — a disabled <c>InlineEditText</c> rests as a bare <c>TextBlock</c>, Avalonia
    /// does not dim one, and the row then reads as live and swallows the double-click. So the visible
    /// line still names the end and still says "reactance", and §6.6 is one hover away.
    /// </remarks>
    [Fact]
    public void TheRippleExplanation_IsOneLineOnScreen_AndAParagraphInTheTooltip()
    {
        var (_, _, d) = Open();
        d.SetTermination(2, new Termination(10.0, ReactanceKind.L, TerminationTopology.Series, 1.5e-9));

        Assert.False(d.RippleEnabled);
        output.WriteLine($"note   : {d.RippleNote}");
        output.WriteLine($"tooltip: {d.RippleTooltip}");

        Assert.True(d.RippleNote.Length < 60, $"the note grew back: \"{d.RippleNote}\"");
        Assert.Contains("Termination 2", d.RippleNote, StringComparison.Ordinal);
        Assert.Contains("reactance", d.RippleNote, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("6.6", d.RippleTooltip, StringComparison.Ordinal);
        Assert.True(d.RippleTooltip.Length > d.RippleNote.Length * 3,
            "the explanation did not move anywhere, it just got shorter");

        d.Dispose();
    }

    /// <summary>
    /// The selected family still explains itself — <b>the combo it used to explain itself IN is gone</b>.
    /// </summary>
    /// <remarks>
    /// The Filter Response card was removed on 2026-08-28 along with Order and Options. What that
    /// round changed is where the choice is MADE, not whether the four families are still described:
    /// each one's line and each one's refusal are what the solutions list is built out of, and
    /// <c>ResponseTooltip</c> is still the selected family's own. So the surface assertion goes and
    /// the behaviour it was guarding stays.
    /// </remarks>
    [Fact]
    public void TheSelectedResponse_StillCarriesItsOwnLine()
    {
        var (_, _, d) = Open();
        Assert.DoesNotContain("ToolTip.Tip=\"{Binding ResponseTooltip}\"", Xaml(), StringComparison.Ordinal);
        Assert.Equal(d.SelectedResponseOption!.Tooltip, d.ResponseTooltip);
        Assert.NotEmpty(d.ResponseTooltip);
        d.Dispose();
    }

    /// <summary>
    /// <b>Owner:</b> "add the name of the schematic (in addition to the MN instance name) to the
    /// window title and the text in the top left of the window. For example
    /// 'Match — MN1 — schematic_name.csch'."
    /// </summary>
    /// <remarks>
    /// One string for all three surfaces — the OS window title, the pane's own title bar, and the
    /// Window menu entry — so the AXAML claim is that both bindings read <c>Title</c> and the
    /// view-model claim is what <c>Title</c> says.
    /// </remarks>
    [Fact]
    public void TheTitle_NamesTheSchematicAsWellAsTheInstance()
    {
        var (schematic, _, d) = Open();

        // With no document behind the session — a Designer on a schematic that has no file yet.
        Assert.Equal("Match — MN1", d.Title);

        schematic.DocumentName = "matchedRFTest.csch";
        d.Refresh(specChanged: false);       // any refresh re-reads it; so does a model change
        Assert.Equal("Match — MN1 — matchedRFTest.csch", d.Title);
        d.Dispose();

        // A standalone Designer has neither, and says so.
        var standalone = new MatchDesignerViewModel();
        standalone.SetStandalone(null);
        Assert.Equal("Match Designer", standalone.Title);
        standalone.Dispose();

        // Both surfaces read the one string, and so does the Window menu entry.
        string xaml = Xaml();
        Assert.Contains("Title=\"{Binding Title}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TextBlock Text=\"{Binding Title}\" FontSize=\"14\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowMenuHeader => Vm?.Title", Code(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The name is written where every path-backed session is registered, so a Save As moves it with
    /// the file rather than leaving the Designer naming the old one.
    /// </summary>
    [Fact]
    public void TheDocumentName_IsSetWhereASessionIsRegistered()
    {
        string body = Between(
            Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"),
            "private SchematicViewModel RegisterSession(string absNormalizedPath, SchematicViewModel vm)");
        Assert.Contains("vm.DocumentName = Path.GetFileName(absNormalizedPath);", body, StringComparison.Ordinal);
    }

    // ══ 8d — the probe left a Q-adjust the terminations had overtaken ═══════

    /// <summary>
    /// <b>Owner-reported:</b> "I press the probe button on Terminal 2 and the parasitic updates to
    /// 1000 pH, but the schematic keeps rendering as 2000 pH."
    /// </summary>
    /// <remarks>
    /// <b>Not stale rendering.</b> <c>QAdjust</c> was 2 — legitimately set while that end's Q was
    /// lower — and the probe made termination 2 a 1 nH parallel L whose own Q is 3.999. The synthesis
    /// then built the end arm for Q = 2 (a 1999 pH shunt inductor) and <c>WithEndSplits</c> skips its
    /// split whenever <c>qSynth &lt;= qActual</c>, so the element kept the SYNTHESIS's value while
    /// still marked absorbed. The drawing labelled 1999 pH as supplied by a termination that supplies
    /// 1000 pH, and the response was computed from an inductor the circuit does not contain.
    ///
    /// <para>The tell, and what this test leads with: the ladder was completely INSENSITIVE to the
    /// termination — 1 nH and 2 nH produced element-for-element identical networks.</para>
    /// </remarks>
    [Fact]
    public void AProbedParasitic_IsWhatTheLadderDraws()
    {
        var start = OwnersProblem(order: 3);
        start.QAdjust = 2.0;
        start.Term2 = new Termination(50.0, ReactanceKind.None, TerminationTopology.Parallel, 0);

        var (_, _, d) = Open(start);

        // The probe's own write path, with the value the owner's own probe measured.
        d.ApplyProbedTermination(2, new Termination(
            50.0, ReactanceKind.L, TerminationTopology.Parallel, 1e-9, true, DateTime.UtcNow));

        var absorbed = d.Ladder.Elements.Single(e => e.AbsorbedEnd == 2);
        output.WriteLine($"absorbed at end 2: {absorbed.Name} = {absorbed.ValueText}");
        output.WriteLine($"QAdjust note     : {d.QAdjustNote}");

        Assert.Equal(1e-9, absorbed.Value, 1e-15);
        Assert.Equal(d.Design.Term2.Value, absorbed.Value, 1e-15);

        // The stale Q-adjust was cleared, and the clearing SAYS SO — a control that silently changes
        // another control is worse than one that explains itself.
        Assert.Equal(0.0, d.Design.QAdjust);
        Assert.Contains("Q-adjust", d.QAdjustNote, StringComparison.Ordinal);
        Assert.Contains("Termination 2", d.QAdjustNote, StringComparison.Ordinal);

        d.Dispose();
    }

    /// <summary>
    /// The invariant underneath it, at the level it belongs: an absorbed element IS the termination's
    /// own reactance, and a Q-adjust below that end's Q is refused rather than quietly redrawing it.
    /// </summary>
    [Fact]
    public void AQAdjustBelowTheAnalysisEndsOwnQ_IsRefusedWithBothNumbers()
    {
        var design = OwnersProblem(order: 3);
        design.Term2 = new Termination(50.0, ReactanceKind.L, TerminationTopology.Parallel, 1e-9);

        // Legal: no adjustment at all, and the absorbed element is exactly what the end supplies.
        design.QAdjust = 0;
        var ok = MatchSynthesis.Synthesize(design);
        Assert.True(ok.Ok);
        Assert.Equal(1e-9, ok.Network!.Elements.Single(e => e.AbsorbedEnd == 2).Value, 1e-15);

        // Illegal: below that end's own Q of 3.999.
        design.QAdjust = 2.0;
        var refused = MatchSynthesis.Synthesize(design);
        Assert.False(refused.Ok);
        Assert.Equal(MatchRefusalKind.AnalysisEndNotAbsorbable, refused.Refusal!.Kind);
        output.WriteLine(refused.Refusal.Message);
        Assert.Equal(2, refused.Refusal.End);
        Assert.Equal(2.0, refused.Refusal.Numbers["qAdjust"]);
        Assert.Equal(3.999, refused.Refusal.Numbers["qActual"], 2);

        // Legal again once it is genuinely an INFLATION, which is what §4.6 says it is.
        design.QAdjust = 6.0;
        Assert.True(MatchSynthesis.Synthesize(design).Ok);
    }

    // ══ 8e — the design is a nested OBJECT in the .csch ═════════════════════

    /// <summary>
    /// <b>Owner:</b> "how much work is it to support the expression as a nested object in the .csch
    /// json? If it won't break anything else or conflict with another philosophy, then please do it."
    /// </summary>
    /// <remarks>
    /// The last step of making an embedded design readable: JSON inside a JSON STRING is escaped, so
    /// every quote is two characters and the whole design is one very long line. Written as an object
    /// under <c>WriteIndented</c> it is one field per line with no escapes at all.
    ///
    /// <para>The rule is general rather than a Match special case — any expression beginning with
    /// <c>{</c>, which is exactly the set that cannot be an expression, since circuitRF's expression
    /// language has no brace token. Same discriminator <c>MatchEmbedding.TryDecode</c> and
    /// <c>CnlWriter</c> already branch on.</para>
    /// </remarks>
    [Fact]
    public void TheDesign_IsANestedObjectInTheCsch_AndRoundTrips()
    {
        var (schematic, _, d) = Open(OwnersProblem());
        string json = SchematicPersistence.Serialize(schematic.EditModel, "matchedRFTest");

        // The payload is an object, and nothing in the file is an escaped JSON document.
        Assert.Contains("\"Value\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"F1\": 1800000000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"F1\\\"", json, StringComparison.Ordinal);

        // ...and the payload parameter writes NO empty Expression beside its own document.
        int at = json.IndexOf("\"Name\": \"Design\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.DoesNotContain("\"Expression\"", json[at..(at + 200)], StringComparison.Ordinal);

        var (back, _, _) = SchematicPersistence.Deserialize(json);
        string expr = back.Components.Single().Parameters.First(p => p.Name == "Design").Expression;
        Assert.True(MatchEmbedding.TryDecode(expr, out var design) && design is not null);
        Assert.Equal(1.8e9, design!.F1);
        Assert.Equal(3, design.Order);
        Assert.Equal(1e-12, design.Term1.Value, 1e-24);

        d.Dispose();
    }

    /// <summary>An ordinary expression is untouched — and one that merely LOOKS like a document,
    /// but is not valid JSON, survives verbatim rather than being lost to an exception on save.</summary>
    [Fact]
    public void OnlyARealDocument_TakesTheNestedForm()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        comp.Parameters.Add(new EditableParameter { Name = "R", Expression = "50*k", Unit = "Ohm" });
        comp.Parameters.Add(new EditableParameter { Name = "Odd", Expression = "{not json" });
        model.Components.Add(comp);

        var (back, _, _) = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(model));
        var ps = back.Components.Single().Parameters;
        Assert.Equal("50*k", ps.First(p => p.Name == "R").Expression);
        Assert.Equal("{not json", ps.First(p => p.Name == "Odd").Expression);
    }

    // ══ 8f — a hand-edited payload leaves the echoes behind ═════════════════

    /// <summary>
    /// <b>Owner:</b> "why is there an 'F1' and 'F2' parameter for a Match component, but also a
    /// nested 'F1' and 'F2'? Why are we carrying two versions of these 2 variables?"
    /// </summary>
    /// <remarks>
    /// They are ECHOES — drawn beside the symbol, written into the <c>.cnl</c> line, and never read
    /// back. The design is authoritative. What is new is that the payload is now readable and so
    /// hand-editable, which makes an echo that has fallen behind reachable for the first time; this
    /// test pins that the Designer SAYS SO rather than either rewriting it on load (an undo entry
    /// nobody made, and a document dirty the moment it opens) or letting the schematic draw a band
    /// the design does not have.
    /// </remarks>
    [Fact]
    public void AnEchoThatHasFallenBehindTheDesign_IsReportedNotRewritten()
    {
        var (_, comp, d) = Open();
        Assert.Equal("", d.EchoNote);
        d.Dispose();

        // A hand edit to the payload — exactly what a readable .csch invites.
        var edited = MatchEmbedding.DefaultDesign();
        edited.F1 = 2.4e9;
        edited.F2 = 2.6e9;
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(edited);

        var d2 = new MatchDesignerViewModel();
        d2.SetTarget(new SchematicViewModel(new SchematicEditModel()), comp);
        output.WriteLine(d2.EchoNote);

        Assert.Contains("F1", d2.EchoNote, StringComparison.Ordinal);
        Assert.Contains("F2", d2.EchoNote, StringComparison.Ordinal);
        // Reported, not rewritten: nothing was written to the component on load.
        Assert.Equal("1.8", comp.Parameters.First(p => p.Name == "F1").Expression);

        // ...and the next edit here does refresh them, which is what the note promises.
        d2.Order = d2.OrderOptions.First(o => o != d2.Order);
        Assert.Equal("2.4", comp.Parameters.First(p => p.Name == "F1").Expression);
        Assert.Equal("", d2.EchoNote);
        d2.Dispose();
    }

    // ══ 9 — the IconSelectButton popup ═══════════════════════════════════════

    /// <summary>
    /// <b>Owner-reported crash:</b> an unhandled <c>NullReferenceException</c> inside
    /// <c>Avalonia.Controls.Primitives.Popup.RootTemplateApplied</c>, reached from
    /// <c>IconSelectButton.OnButtonClick</c>'s <c>IsOpen = true</c>, after changing an input control
    /// in the Specification panel.
    /// </summary>
    /// <remarks>
    /// Ui.Tests has no live Avalonia application, so the popup cannot be opened here. What CAN be
    /// checked exactly is the property the fix rests on, and it is a property about ORDER: the popup
    /// is closed before the selection is published, and the publish is posted rather than run inside
    /// the ListBox's own <c>SelectionChanged</c>. That is what stops a consumer — the Designer's Order
    /// selector rebuilds the very collection the open popup is bound to — from mutating a live popup
    /// from inside its own event.
    /// </remarks>
    [Fact]
    public void TheSelectorPopup_IsClosedBeforeTheChoiceIsPublished()
    {
        string src = Src("src", "Ui", "DataDisplay", "Controls", "IconSelectButton.cs");
        string body = Between(src, "private void OnListBoxSelectionChanged(");

        int close = body.IndexOf("IsOpen = false", StringComparison.Ordinal);
        int publish = body.IndexOf("SelectedItem = item", StringComparison.Ordinal);
        Assert.True(close > 0, "the handler no longer closes the popup");
        Assert.True(publish > close, "the choice is published while the popup is still open");
        Assert.Contains("Dispatcher.UIThread.Post", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A replaced item list re-syncs the ListBox's selection. The Designer's Order selector is the one
    /// <c>IconSelectButton</c> whose items are rebuilt by the act of choosing from it
    /// (<c>RefreshOrderChoices</c>), so it was the one that opened next with nothing highlighted.
    /// </summary>
    [Fact]
    public void AReplacedItemList_ReSyncsTheSelection()
    {
        string src = Src("src", "Ui", "DataDisplay", "Controls", "IconSelectButton.cs");
        string body = Between(src, "protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)");
        Assert.Contains("change.Property == ItemsSourceProperty", body, StringComparison.Ordinal);
        Assert.Contains("SyncListBoxSelection()", body, StringComparison.Ordinal);

        // And the collection really is rebuilt, which is what makes the above load-bearing.
        string vm = Src("src", "Ui", "Match", "MatchDesignerViewModel.cs");
        Assert.Contains("OrderChoices.Clear();", vm, StringComparison.Ordinal);
    }

    // ══ 10 — the project tree keeps its dirty marks across a rescan ══════════

    /// <summary>
    /// <b>Owner-reported:</b> "when I closed my Match Designer, the schematic document became dirty,
    /// but the cell in the project tree indicated that it was not dirty. As soon as I closed the Match
    /// Designer window, the indicator on the cell in the project tree changed from dirty to not
    /// dirty."
    /// </summary>
    /// <remarks>
    /// Nothing about the Match Designer did that — closing ANY window did. The tree re-scans on the
    /// workspace window's <c>Activated</c>, the rebuild throws every node away, and the dirty flag
    /// lives only on the node. Asserted as a source claim because <c>ProjectTreeTool.RebuildVmTree</c>
    /// needs a scanned workspace and an <c>ITreeActions</c> host, neither of which this project can
    /// stand up headlessly — but the claim is exact: the rebuild asks <c>IsNodeDirty</c>, which reads
    /// the session registry and the open documents, both of which survive the rescan.
    /// </remarks>
    [Fact]
    public void ATreeRebuild_RestoresEveryNodesDirtyMark()
    {
        string src = Src("src", "Ui", "ViewModels", "Dock", "ProjectTreeTool.cs");

        string rebuild = Between(src, "private void RebuildVmTree(HashSet<string> expandedPaths)");
        Assert.Contains("RestoreDirtyFlags(root);", rebuild, StringComparison.Ordinal);

        string restore = Between(src, "private void RestoreDirtyFlags(ProjectTreeNodeViewModel node)");
        Assert.Contains("_actions.IsNodeDirty(node)", restore, StringComparison.Ordinal);
        Assert.Contains("foreach (var child in node.Children)", restore, StringComparison.Ordinal);

        // Refresh() is the path a window Activated takes, and it still reaches the rebuild — via
        // ApplyScan since 2026-08-25, which skips the rebuild entirely when the rescan finds the tree
        // unchanged. The skip cannot lose a mark: with no rebuild there is nothing to restore, and the
        // marks live on node VMs that were never replaced. When something DID change, the rebuild runs
        // and RestoreDirtyFlags with it, which is what the two assertions above pin.
        Assert.Contains("ApplyScan(", Between(src, "public void Refresh()"), StringComparison.Ordinal);
        Assert.Contains("RebuildVmTree(expandedPaths);",
                        Between(src, "private void ApplyScan(ProjectTreeNode scanned)"), StringComparison.Ordinal);
    }
}
