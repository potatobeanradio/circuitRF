// ================================================================
//  MatchRound5Tests.cs  —  the owner's 2026-08-20 round-5 list for the Match Designer.
//
//  Same discipline as rounds 1-4: view-model, geometry and projection tests, never pixels. Where the
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
using Avalonia;
using Avalonia.Controls;
using System.Reflection;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Match;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound5Tests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    /// <summary>Loads the shipped default design and stacks three transforms on it.</summary>
    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        OpenWithTransforms(int count = 3)
    {
        var opened = Open();
        for (int i = 0; i < count; i++)
        {
            var pairs = opened.Designer.AvailablePairs();
            if (pairs.Count == 0) break;
            opened.Designer.AddTransform(pairs[0]);
        }
        return opened;
    }

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
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);   // XML / AXAML
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);    // C# block
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);           // C# line + XML doc
        return raw;
    }

    private static string Xaml() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");
    private static string Code() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");
    private static string Canvas() => Src("src", "Ui", "Views", "Match", "MatchSchematicCanvas.cs");

    /// <summary>The body of one method, from its signature to the next one at the same indent.</summary>
    private static string Body(string source, string signature)
    {
        int i = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source");
        int j = source.IndexOf("\n    private ", i + signature.Length, StringComparison.Ordinal);
        int k = source.IndexOf("\n    public ", i + signature.Length, StringComparison.Ordinal);
        int end = new[] { j, k }.Where(x => x > 0).DefaultIfEmpty(source.Length).Min();
        return source[i..end];
    }

    // ══ The pane's gestures ══════════════════════════════════════════════════

    /// <summary>
    /// <b>The network pane pans on the MIDDLE button, the way a schematic page does</b> (owner:
    /// "panning in the schematic view should work using center mouse button — just like regular
    /// schematic — not the left mouse button").
    /// </summary>
    /// <remarks>
    /// Asserted against both surfaces at once, because the claim is that they AGREE: the editor's own
    /// canvas starts its pan on <c>IsMiddleButtonPressed</c>, and so must this one. Freeing the left
    /// button is what makes the double-click that opens an inline editor reachable at all.
    /// </remarks>
    [Fact]
    public void TheNetworkPane_PansOnTheMiddleButton_LikeASchematicPage()
    {
        string pressed = Body(Canvas(), "protected override void OnPointerPressed");
        output.WriteLine(pressed);

        Assert.Contains("IsMiddleButtonPressed", pressed, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLeftButtonPressed", pressed, StringComparison.Ordinal);

        // The editor's own canvas, for the same gesture — this is the thing being matched.
        string editor = Src("src", "Ui", "Controls", "SchematicCanvas.cs");
        Assert.Contains("IsMiddleButtonPressed", editor, StringComparison.Ordinal);
    }

    /// <summary>
    /// A double-click edits a label and does <b>nothing else</b> — the re-frame is a button now.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"remove the Zoom to Fit on double click of the Schematic canvas
    /// because now user has this button."</i> Asserted as an ABSENCE as well as a presence: the
    /// gesture is only interesting for what it no longer does, and a fix that left the call behind an
    /// unreachable branch would satisfy a presence-only test.
    /// </remarks>
    [Fact]
    public void ADoubleClick_EditsALabel_AndNoLongerReframes()
    {
        string tap = Body(Canvas(), "protected override void OnDoubleTapped");
        output.WriteLine(tap);

        Assert.Contains("HitLabel(", tap, StringComparison.Ordinal);
        Assert.Contains("LabelDoubleTapped?.Invoke", tap, StringComparison.Ordinal);
        Assert.DoesNotContain("ZoomToFit()", tap, StringComparison.Ordinal);
    }

    // ══ The inline editor is the schematic editor's own ══════════════════════

    /// <summary>
    /// <b>ONE inline text editor, hosted by two surfaces</b> (owner: "can you reuse the exact same
    /// inline text editor from the regular schematic? That is the preferred solution").
    /// </summary>
    /// <remarks>
    /// The claim is stronger than "both create a TextBox": the schematic page's box IS a
    /// <c>SchematicInlineEditBox</c> now, and the paddings, the ascender ratio and the width rule it
    /// positions with are that type's, not its own copies. Both halves are asserted, because either
    /// one alone would let the two drift apart while still looking shared.
    /// </remarks>
    [Fact]
    public void TheInlineEditor_IsOneControl_HostedByBothSchematicSurfaces()
    {
        string page = Src("src", "Ui", "Views", "Content", "SchematicView.axaml");
        Assert.Contains("ctrl:SchematicInlineEditBox x:Name=\"InlineEditBox\"", page, StringComparison.Ordinal);

        string pageCode = Src("src", "Ui", "Views", "Content", "SchematicView.axaml.cs");
        Assert.Contains("SchematicInlineEditBox.LeftPad", pageCode, StringComparison.Ordinal);
        Assert.Contains("SchematicInlineEditBox.TopPad", pageCode, StringComparison.Ordinal);
        Assert.Contains("SchematicInlineEditBox.AscenderRatio", pageCode, StringComparison.Ordinal);
        Assert.Contains("SchematicInlineEditBox.WidthFor", pageCode, StringComparison.Ordinal);
        // The private copies it used to carry are gone, not merely shadowed.
        Assert.DoesNotContain("MeasureAscenderRatio()", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("fontSize * 0.55", pageCode, StringComparison.Ordinal);

        string designer = Code();
        Assert.Contains("new SchematicInlineEditBox()", designer, StringComparison.Ordinal);
        Assert.Contains("NetworkSchematicHost", designer, StringComparison.Ordinal);
        Assert.Contains("Name=\"NetworkSchematicHost\"", Xaml(), StringComparison.Ordinal);

        // The three-key contract, on the Designer's own host.
        string wire = Body(designer, "private void WireSchematicInlineEditor()");
        Assert.Contains("KeyDown += OnLabelEditorKeyDown", wire, StringComparison.Ordinal);
        Assert.Contains("LostFocus", wire, StringComparison.Ordinal);
        string keys = Body(designer, "private void OnLabelEditorKeyDown");
        Assert.Contains("Key.Return", keys, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", keys, StringComparison.Ordinal);
        Assert.Contains("CommitLabelEdit()", keys, StringComparison.Ordinal);
        Assert.Contains("DismissLabelEditor()", keys, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Designer's box is placed by the SAME arithmetic the renderer draws the label with — the
    /// canvas maps the row's own baseline, and the control turns that into a margin.
    /// </summary>
    [Fact]
    public void TheLabelAnchor_ComesFromTheRenderersOwnRowGeometry()
    {
        // The geometry lives in MatchSchematicLabels — pure, so it can be asserted by calling it
        // rather than by reading it. The canvas keeps only the two things a live control knows.
        string labels = Src("src", "Ui", "Match", "MatchSchematicLabels.cs");
        Assert.Contains("SchematicComponent.LabelRowGeometry", labels, StringComparison.Ordinal);
        Assert.Contains("c.GlyphBbMaxY - c.Y", labels, StringComparison.Ordinal);
        Assert.Contains("c.Ports.Count / 2", labels, StringComparison.Ordinal);

        string canvas = Canvas();
        Assert.Contains("MatchSchematicLabels.HitTest", canvas, StringComparison.Ordinal);
        Assert.Contains("MatchSchematicLabels.Locate", canvas, StringComparison.Ordinal);
        Assert.Contains("SchematicInlineEditBox.FontSizeAt", canvas, StringComparison.Ordinal);

        // ...and it really does put the anchor on the row the renderer draws.
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);
        var term = model.Components.First(c => c.Symbol == SymbolKind.TermG);
        var located = MatchSchematicLabels.Locate(model, term.Id, MatchSchematicModel.ValueRow)!;
        var (baseX, baselineY, _, _) =
            MatchSchematicLabels.RowGeometry(term, MatchSchematicModel.ValueRow);
        Assert.Equal(baseX, located.BaseX, 6);
        Assert.Equal(baselineY, located.BaselineY, 6);
        d.Dispose();
    }

    // ══ What a label edit resolves to ════════════════════════════════════════

    /// <summary>
    /// <b>A component resolves to the one thing about it a user may set — its VALUE — whichever part
    /// of it was hit</b>, and a component with nothing to set resolves to nothing.
    /// </summary>
    /// <remarks>
    /// Which row was clicked is deliberately not an input. The type row is what the synthesis
    /// produced and the name row is the key every stored transform resolves through, so neither had a
    /// meaning of its own to compete with — and at this pane's zoom the two of them are two thirds of
    /// a 16-pixel label block sitting on top of the third that does something.
    /// </remarks>
    [Fact]
    public void AComponent_ResolvesToItsValue_AndAGroundToNothing()
    {
        var (_, _, d) = Open();
        string first = d.Ladder.Elements[0].Name;

        var element = d.ResolveInlineEdit(first);
        Assert.NotNull(element);
        Assert.Equal(MatchInlineEditKind.ElementValue, element!.Kind);
        Assert.Equal(MatchSchematicModel.ValueRow, element.Row);

        var term = d.ResolveInlineEdit("Termination 1");
        Assert.NotNull(term);
        Assert.Equal(MatchInlineEditKind.TerminationResistance, term!.Kind);
        Assert.Equal(MatchSchematicModel.ValueRow, term.Row);

        // A shunt arm's own ground carries no labels and nothing to set.
        Assert.Null(d.ResolveInlineEdit(first + MatchSchematicModel.GroundIdSuffix));
        Assert.Null(d.ResolveInlineEdit("no such component"));
        Assert.Null(d.ResolveInlineEdit(""));
        d.Dispose();
    }

    /// <summary>
    /// <b>The whole component is the target, not a 4.7-pixel strip</b> (owner-reported three times,
    /// 2026-08-20, ending "I still cannot use inline text editor on the TermG Z value").
    /// </summary>
    /// <remarks>
    /// The first two rounds fixed real defects — an overlap resolved to the wrong row, then a
    /// world-unit pick area — and neither was enough, because the thing being aimed at was still one
    /// label row. Measured off this pane's own captured figure (<c>match-designer.svg</c>, which
    /// records a real render): the pane runs at a zoom of 0.0674, so the row pitch is <b>4.85 screen
    /// pixels</b> and the value text 4.7. Above it sat two rows of the same size that did nothing.
    ///
    /// <para>So every part of a component now opens its value: any of its label rows, and its glyph —
    /// which is the schematic page's own convention (<c>SchematicView.OnComponentDoubleTapped</c>)
    /// and, at 32 pixels tall on a <c>TermG</c>, the part a user can actually hit. This asserts the
    /// glyph and all three rows, for the TermGs by name.</para>
    /// </remarks>
    [Fact]
    public void EveryPartOfAComponent_OpensItsValue_TermGsIncluded()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var terms = model.Components.Where(c => c.Symbol == SymbolKind.TermG).ToList();
        Assert.Equal(2, terms.Count);

        foreach (var c in model.Components.Where(c => c.Labels.Count > 0))
        {
            // ── the glyph, dead centre ──
            var onGlyph = MatchSchematicLabels.HitTest(
                model, (c.GlyphBbMinX + c.GlyphBbMaxX) / 2, (c.GlyphBbMinY + c.GlyphBbMaxY) / 2);
            Assert.NotNull(onGlyph);
            Assert.Equal(c.Id, onGlyph!.ComponentId);
            Assert.Equal(MatchSchematicModel.ValueRow, onGlyph.Row);
            Assert.NotNull(d.ResolveInlineEdit(onGlyph.ComponentId));

            // ── every label row, across its own text ──
            for (int row = 0; row < c.Labels.Count; row++)
            {
                var (baseX, baselineY, _, _) = MatchSchematicLabels.RowGeometry(c, row);
                var hit = MatchSchematicLabels.HitTest(
                    model, baseX + 20, baselineY - SchematicComponent.LabelWorldHeight / 2.0);
                Assert.NotNull(hit);
                Assert.Equal(c.Id, hit!.ComponentId);
                // Whichever row was hit, the component resolves to its VALUE.
                var target = d.ResolveInlineEdit(hit.ComponentId);
                Assert.NotNull(target);
                Assert.Equal(MatchSchematicModel.ValueRow, target!.Row);
            }
        }

        // ...and a shunt arm's own ground is still not a target — it has no labels, so a double-click
        // on it falls through to zoom-to-fit.
        var gnd = model.Components.First(c => c.Symbol == SymbolKind.Ground);
        Assert.Null(MatchSchematicLabels.HitTest(model, gnd.X, gnd.Y + 20));

        // Empty space is still empty space.
        Assert.Null(MatchSchematicLabels.HitTest(model, -5000, -5000));
        d.Dispose();
    }

    /// <summary>
    /// <b>A TermG's value edits the TERMINATION's R</b> (owner: "allow user to double click on the
    /// TermG… to change the R for the termination… this is another way to change the Specification of
    /// the termination"), and <b>a complex value is refused</b> ("reject any complex value, because
    /// it must be real").
    /// </summary>
    [Fact]
    public void ATermGEdit_WritesTheSpecificationsOwnR_AndRefusesAComplexValue()
    {
        var (vm, _, d) = Open();
        var target = d.ResolveInlineEdit("Termination 1")!;
        Assert.Equal(MatchInlineEditKind.TerminationResistance, target.Kind);
        Assert.Equal(1, target.End);

        double before = d.Design.Term1.R;
        foreach (string complex in new[] { "50+j10", "50 + j10", "50+10i", "10j" })
        {
            Assert.False(d.CommitInlineEdit(target, complex));
            Assert.Contains("must be real", d.InlineEditNote, StringComparison.Ordinal);
            Assert.Equal(before, d.Design.Term1.R, 9);
        }

        Assert.True(d.CommitInlineEdit(target, "75 Ω"));
        Assert.Equal(75.0, d.Design.Term1.R, 9);
        Assert.Equal("", d.InlineEditNote);

        // The Specification pane's own field is looking at the same number — one property, two doors.
        Assert.Equal(75.0, d.Term1.Resistance, 9);

        // ...and it is one undoable step on the SCHEMATIC's stack, like every other Designer edit.
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Equal(before, d.Design.Term1.R, 9);
        d.Dispose();
    }

    // ══ An element value is a target for the transform rack ══════════════════

    /// <summary>
    /// <b>A typed element value moves the TRANSFORMS, and the response does not move</b> (owner:
    /// "all other components are updated accordingly, using the available transforms (N1, N2, N3
    /// etc.) to maintain the frequency response… the N1, N2, N3 etc. sliders update automatically").
    /// </summary>
    /// <remarks>
    /// The response is checked through the rebuild's own worst in-band return loss and through
    /// <c>Π N²</c> — a Norton transform is exact, so the FIRST is the claim and the second is why it
    /// holds. Undo is checked too, because a transform written outside <c>Commit</c> would leave the
    /// schematic's stack with nothing to undo and the design would still have changed.
    /// </remarks>
    [Fact]
    public void AnElementValueEdit_MovesTheSliders_AndLeavesTheResponseWhereItWas()
    {
        var (vm, _, d) = OpenWithTransforms();
        Assert.True(d.Design.Transforms.Count >= 2, "the fixture did not stack any transforms");

        // An element a transform PRODUCED is one a transform can move; that is the case the owner is
        // describing. The others are found and reported by the same code, below.
        var movable = d.Ladder.Elements.First(e => e.Name.EndsWith("_3", StringComparison.Ordinal));
        var target = d.ResolveInlineEdit(movable.Name)!;

        double rlBefore = d.Status.WorstReturnLossDb;
        double ratioBefore = d.Status.Required;
        var nBefore = d.Design.Transforms.Select(t => t.N).ToList();

        double want = movable.Value * 1.30;
        Assert.True(d.CommitInlineEdit(
            target, MatchValueFormat.FormatWithUnit(want, target.Quantity, target.Unit, 8)));

        var now = d.Ladder.Elements.First(e => e.Name == movable.Name);
        output.WriteLine($"{movable.Name}: {movable.Value:E4} -> {now.Value:E4} (wanted {want:E4})");
        output.WriteLine("N: " + string.Join(", ", d.Design.Transforms.Select(t => t.N.ToString("F5"))));

        // Reached to the tolerance the search itself calls exact — the refinement is a ternary
        // search on a bracket, not a closed form, so the last few digits are its convergence floor.
        Assert.Equal(1.0, now.Value / want, 6);
        Assert.True(Math.Abs(now.Value / want - 1.0) <= MatchElementSolve.ExactTolerance);
        Assert.Equal("", d.InlineEditNote);                       // nothing to apologise for
        Assert.NotEqual(nBefore, d.Design.Transforms.Select(t => t.N).ToList());

        // The response is UNMOVED — which is the whole contract of a Norton transform.
        Assert.Equal(rlBefore, d.Status.WorstReturnLossDb, 9);
        Assert.Equal(ratioBefore, d.Status.Required, 9);
        Assert.True(d.Status.OnTarget, "Π N² came off target");

        // The sliders' own rows report the new N, and the edit is undoable from the schematic.
        for (int i = 0; i < d.Design.Transforms.Count; i++)
            Assert.Equal(d.Design.Transforms[i].N, d.Transforms[i].N, 12);

        vm.UndoRedo.Undo();
        Assert.Equal(nBefore, d.Design.Transforms.Select(t => t.N).ToList());
        d.Dispose();
    }

    /// <summary>
    /// <b>When no combination of transforms reaches the value, the closest is used and SAID</b>
    /// (owner: "if no combination of transforms can achieve the value entered into inline text
    /// editor, the closest value is used").
    /// </summary>
    /// <remarks>
    /// In a ladder whose transforms all act at one end, the elements at the other end are genuinely
    /// unreachable — every N gives the same value. The closest is then what it already is, so the
    /// honest outcome is to change NOTHING and say why: a rack rearranged for no gain is the worse of
    /// the two answers, and it was what an earlier version of the search did.
    /// </remarks>
    [Fact]
    public void AnUnreachableValue_LeavesTheRackAlone_AndSaysSo()
    {
        var (_, _, d) = OpenWithTransforms();

        var stuck = d.Ladder.Elements.First(e => !e.Name.Contains("_N", StringComparison.Ordinal));
        var target = d.ResolveInlineEdit(stuck.Name)!;
        var nBefore = d.Design.Transforms.Select(t => t.N).ToList();

        Assert.False(d.CommitInlineEdit(
            target, MatchValueFormat.FormatWithUnit(stuck.Value * 1.3, target.Quantity, target.Unit, 8)));

        output.WriteLine(d.InlineEditNote);
        Assert.Contains("cannot reach", d.InlineEditNote, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", d.InlineEditNote, StringComparison.Ordinal);
        Assert.Equal(nBefore, d.Design.Transforms.Select(t => t.N).ToList());

        // The element it names is still the value it always was.
        Assert.Equal(1.0, d.Ladder.Elements.First(e => e.Name == stuck.Name).Value / stuck.Value, 12);
        d.Dispose();
    }

    /// <summary>
    /// A design with no transforms at all cannot move any value, and the message says what to do
    /// about it rather than reporting a failure.
    /// </summary>
    [Fact]
    public void WithNoTransforms_AnElementEdit_PointsAtTheAddButton()
    {
        var (_, _, d) = Open();
        while (d.Design.Transforms.Count > 0) d.RemoveLastTransform();

        var e = d.Ladder.Elements[0];
        var target = d.ResolveInlineEdit(e.Name)!;
        Assert.False(d.CommitInlineEdit(
            target, MatchValueFormat.FormatWithUnit(e.Value * 1.3, target.Quantity, target.Unit, 8)));
        output.WriteLine(d.InlineEditNote);
        Assert.Contains("Add a transform", d.InlineEditNote, StringComparison.Ordinal);
        d.Dispose();
    }

    /// <summary>
    /// <b>A LOCKED transform is never moved by a value edit</b> (owner: "when searching for a
    /// transform to accommodate the inline text edit change of a component, do not allow for any
    /// locked transforms — sliders that are locked, and their transforms, cannot ever change unless
    /// they are unlocked first").
    /// </summary>
    /// <remarks>
    /// Locking EVERY row is the sharp version of the claim: there is then nothing the search may
    /// touch, so the only correct outcome is that nothing moves and the reason names the locks. The
    /// weaker "lock one and check that one" would pass even if the guard only held for the driven
    /// transform and not for the ones the linkage redistributes into.
    /// </remarks>
    [Fact]
    public void ALockedTransform_IsNeverMovedByAValueEdit()
    {
        var (_, _, d) = OpenWithTransforms();
        Assert.True(d.Design.Transforms.Count >= 2);

        var movable = d.Ladder.Elements.First(e => e.Name.EndsWith("_3", StringComparison.Ordinal));
        var target = d.ResolveInlineEdit(movable.Name)!;
        string typed = MatchValueFormat.FormatWithUnit(
            movable.Value * 1.30, target.Quantity, target.Unit, 8);

        // 1. One lock: the locked row keeps its N even though the others are re-solved around it.
        double lockedN = d.Design.Transforms[0].N;
        d.Transforms[0].Locked = true;
        d.CommitInlineEdit(target, typed);
        Assert.Equal(lockedN, d.Design.Transforms[0].N, 12);

        // 2. Every lock: nothing may move at all, and the refusal names the locks.
        var all = d.Design.Transforms.Select(t => t.N).ToList();
        for (int i = 0; i < d.Design.Transforms.Count; i++) d.Transforms[i].Locked = true;

        var again = d.ResolveInlineEdit(movable.Name)!;
        Assert.False(d.CommitInlineEdit(
            again, MatchValueFormat.FormatWithUnit(
                d.Ladder.Elements.First(e => e.Name == movable.Name).Value * 1.7,
                again.Quantity, again.Unit, 8)));

        output.WriteLine(d.InlineEditNote);
        Assert.Contains("locked", d.InlineEditNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(all, d.Design.Transforms.Select(t => t.N).ToList());
        d.Dispose();
    }

    /// <summary>
    /// The search itself, at the level it lives: a locked slot is never a driven index, and the
    /// linkage it goes through is asked for the LINK behaviour whatever the design's own setting is —
    /// that is what holds <c>Π N²</c>, which is what holds the response.
    /// </summary>
    [Fact]
    public void TheSearch_DrivesOnlyUnlockedSlots_AndAlwaysLinks()
    {
        string src = Src("src", "Core", "Match", "MatchElementSolve.cs");
        Assert.Contains("if (records[i].Locked) continue;", src, StringComparison.Ordinal);
        Assert.Contains("link: true", src, StringComparison.Ordinal);
        Assert.Contains("records[k].Locked ? records[k] :", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Re-declaring a termination moves the LADDER to it, not just the specification</b>
    /// (owner-reported, 2026-08-20: "I changed the TermG and it updated in the Termination 1
    /// specification, but did not update in the schematic — the old value was retained").
    /// </summary>
    /// <remarks>
    /// The glyph quotes <c>MatchNetwork.R1</c>/<c>R2</c>, the reference the plotted response is
    /// actually referenced to, and that is deliberate. But the FAR end's reference is the analysis
    /// end's scaled by <c>Π N²</c>, so re-declaring it does not move it until the transforms are
    /// re-solved — measured on the shipped default, setting termination 2 to 25 Ω left the ladder
    /// presenting 15 Ω while the specification pane read 25. With Link on that is exactly what the
    /// linkage exists to absorb, so a termination edit now re-drives it, the same move
    /// <c>LinkTransforms</c>'s own setter makes when it is switched on.
    /// </remarks>
    [Fact]
    public void ChangingATermination_MovesTheLadderOntoIt_AsOneUndoableStep()
    {
        var (vm, _, d) = OpenWithTransforms();
        Assert.True(d.Design.LinkTransforms);
        Assert.True(d.Design.Transforms.Count > 0);

        double before = d.Design.Term2.R;
        Assert.True(d.CommitInlineEdit(d.ResolveInlineEdit("Termination 2")!, "25 Ω"));

        Assert.Equal(25.0, d.Design.Term2.R, 9);
        Assert.Equal(25.0, d.Rebuild!.Network!.R2, 6);        // the LADDER moved, not just the spec
        Assert.True(d.Status.OnTarget);

        // ...and that is what the glyph says, with nothing appended.
        string label = TerminationLabel(d, 2);
        output.WriteLine(label);
        Assert.Contains("25", label, StringComparison.Ordinal);
        Assert.DoesNotContain("target", label, StringComparison.Ordinal);

        // ONE undo entry: the relink refreshes and commits in place of the edit's own commit.
        vm.UndoRedo.Undo();
        Assert.Equal(before, d.Design.Term2.R, 9);
        d.Dispose();
    }

    /// <summary>
    /// When the linkage cannot reach the declared value, the glyph <b>says both numbers</b> rather
    /// than silently showing one the user did not type.
    /// </summary>
    /// <remarks>
    /// Link off, every transform locked, or a target outside their ranges — in all three the ladder's
    /// own reference is the honest thing to draw, and it is also the thing that looks like a bug when
    /// drawn alone. The status strip's <c>Π N²  ✘ not reached</c> already said so, three panes away
    /// from the glyph the user was looking at.
    ///
    /// <para><b>The state is reached by a TRANSFORM edit here, not a termination edit, and that is a
    /// real change to what this test can be about.</b> A termination edit that leaves the rack short
    /// is now moved onto a solution that reaches the target (<c>MatchAutoSolveTests</c>), so it no
    /// longer produces a disagreement for the glyph to name — deliberately, because that is the state
    /// the owner did not want presented. What is left is every OTHER way to reach it: driving an N by
    /// hand with Link off, and moving the order with every transform locked. Both are below, and both
    /// are what <c>OhmsTextFor</c> exists for.</para>
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void WhenTheLadderCannotReachIt_TheGlyphNamesBothNumbers()
    {
        var (_, _, d) = OpenWithTransforms();
        double declared = d.Design.Term2.R;

        // ── Link off: one N driven by hand, and nothing re-solves the rest against it ──
        d.LinkTransforms = false;
        d.WaitForAnalysis();
        var range = d.Transforms[0].Range!;
        d.SetTransformN(0, (range.Min + range.Max) / 2);
        d.WaitForAnalysis();

        Assert.Equal(declared, d.Design.Term2.R, 9);
        Assert.NotEqual(declared, d.Rebuild!.Network!.R2, 3);     // the ladder did NOT follow
        Assert.False(d.Status.OnTarget);

        string label = TerminationLabel(d, 2);
        output.WriteLine(label);
        Assert.Contains("target", label, StringComparison.Ordinal);
        Assert.Contains(
            declared.ToString("0.###", CultureInfo.InvariantCulture), label, StringComparison.Ordinal);

        // ── Every transform locked, with Link back on: the linkage has nothing it may move ──
        //
        // Locked FIRST and Link on second, deliberately: switching Link on re-drives one unlocked
        // transform there and then (see LinkTransforms' own setter), which would put the rack back on
        // target and leave the rest of this measuring nothing.
        foreach (var row in d.Transforms) row.Locked = true;
        d.LinkTransforms = true;
        d.WaitForAnalysis();
        d.Order = d.OrderOptions.First(o => o != d.Order);
        d.WaitForAnalysis();

        Assert.False(d.Status.OnTarget);
        output.WriteLine(TerminationLabel(d, 2));
        Assert.Contains("target", TerminationLabel(d, 2), StringComparison.Ordinal);
        d.Dispose();
    }

    /// <summary>The value row the network pane actually draws for one termination glyph.</summary>
    private static string TerminationLabel(MatchDesignerViewModel d, int end)
    {
        var model = MatchSchematicModel.Build(d.Ladder);
        var term = model.Components.Single(
            c => c.Symbol == SymbolKind.TermG && c.Id == $"Termination {end}");
        return term.Labels[MatchSchematicModel.ValueRow];
    }

    // ══ The termination's reactance ══════════════════════════════════════════

    /// <summary>
    /// <b>The typed UNIT decides the reactance kind, and 0 clears it</b> (owner: "allow the user
    /// entered units to change whether the X is an L or C or none… if user enters 0, then it becomes
    /// a '–' component"), and <b>the field is live even when the kind is "–"</b>.
    /// </summary>
    [Fact]
    public void TheReactanceField_TakesItsKindFromTheTypedUnit_AndStaysLiveOnNone()
    {
        var (_, _, d) = Open();
        var t = d.Term1;

        t.ReactanceEntry = "2.2 nH";
        Assert.Equal(ReactanceKind.L, t.Kind);
        Assert.Equal(1.0, t.Reactance / 2.2e-9, 9);

        t.ReactanceEntry = "1.5 pF";
        Assert.Equal(ReactanceKind.C, t.Kind);
        Assert.Equal(1.0, t.Reactance / 1.5e-12, 9);

        // Any henry, any farad — not just the two spellings above.
        t.ReactanceEntry = "0.5 µH";
        Assert.Equal(ReactanceKind.L, t.Kind);
        t.ReactanceEntry = "47 fF";
        Assert.Equal(ReactanceKind.C, t.Kind);

        // A bare number keeps the kind that is selected — which is what makes typing over the
        // pre-selected digits safe.
        t.ReactanceEntry = "22";
        Assert.Equal(ReactanceKind.C, t.Kind);
        Assert.Equal(1.0, t.Reactance / (22 * MatchValueFormat.Scale(t.ReactanceUnitShown)), 9);

        // Zero clears it, and the field is STILL live: typing a unit brings the reactance back.
        t.ReactanceEntry = "0";
        Assert.Equal(ReactanceKind.None, t.Kind);
        Assert.False(t.HasReactance);
        Assert.StartsWith("0 ", t.ReactanceEntry, StringComparison.Ordinal);

        t.ReactanceEntry = "3.3 nH";
        Assert.Equal(ReactanceKind.L, t.Kind);
        Assert.Equal(1.0, t.Reactance / 3.3e-9, 9);

        // A unit that is neither is refused outright rather than read as one of them.
        t.ReactanceEntry = "10 Ω";
        Assert.Equal(ReactanceKind.L, t.Kind);
        Assert.Equal(1.0, t.Reactance / 3.3e-9, 9);
        d.Dispose();
    }

    /// <summary>
    /// <b>The reactance is ONE row, and the kind selector is its label</b> (owner: "replace the 'X'
    /// text in the Termination readouts with the C, L, or - selector… reduce [it] to be only 1
    /// character wide… move the Value readout up a row").
    /// </summary>
    [Fact]
    public void TheTerminationCard_HasOneReactanceRow_LabelledByTheKindSelector()
    {
        string xaml = Xaml();

        // The bare "X" label and the separate "Value" row are both gone.
        Assert.DoesNotContain("Text=\"X\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Value\"", xaml, StringComparison.Ordinal);

        // The kind selector is a single character wide, and no longer right-aligned into the value
        // column it has taken over.
        int kind = xaml.IndexOf("MatchTerminationViewModel.KindOptions", StringComparison.Ordinal);
        Assert.True(kind > 0);
        string row = xaml[Math.Max(0, kind - 400)..kind];
        Assert.Contains("Width=\"24\"", row, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", row, StringComparison.Ordinal);

        // ...and the value beside it is not gated on there being a reactance any more.
        Assert.DoesNotContain("IsEnabled=\"{Binding HasReactance}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ReactanceEntry, Mode=TwoWay}", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The value row wins the strip it SHARES with the row above</b> (owner-reported, 2026-08-20:
    /// "cannot double click on TermG value to get the inline text editor").
    /// </summary>
    /// <remarks>
    /// <c>LabelRowGeometry</c>'s hit band is 101.6 world units tall on a 72-unit row pitch, so
    /// consecutive rows overlap by 29.6 — and a hit-test that returned the first containing row gave
    /// all of that to the row ABOVE. Because a label's glyphs sit above their own baseline, the top
    /// 34% of the value TEXT resolved to the instance-name row, which is not editable, so the gesture
    /// did nothing at all and said nothing about it. This walks the value text of every labelled
    /// component from its top edge to its baseline and requires every sample to resolve to row 2.
    /// </remarks>
    [Fact]
    public void EveryValueLabel_IsHittableAcrossItsWholeHeight_IncludingTheTermGs()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        var labelled = model.Components.Where(c => c.Labels.Count > 2).ToList();
        Assert.Contains(labelled, c => c.Symbol == SymbolKind.TermG);   // the one that was reported
        Assert.True(labelled.Count >= 4);

        foreach (var c in labelled)
        {
            var (baseX, baselineY, bandTop, bandBot) = MatchSchematicLabels.RowGeometry(c, 2);
            var above = MatchSchematicLabels.RowGeometry(c, 1);
            Assert.True(above.BandBot > bandTop, "the rows no longer overlap — this test is about the overlap");

            // Down the CAP HEIGHT of the value text, which is what the user aims at.
            for (int k = 0; k <= 8; k++)
            {
                double wy = baselineY - SchematicComponent.LabelWorldHeight
                            + k * SchematicComponent.LabelWorldHeight / 8.0;
                var hit = MatchSchematicLabels.HitTest(model, baseX + 40, wy);
                output.WriteLine($"{c.Id} @ y {wy:F1} -> {hit?.ComponentId ?? "nothing"} row {hit?.Row}");
                Assert.NotNull(hit);
                Assert.Equal(c.Id, hit!.ComponentId);
                Assert.Equal(2, hit.Row);
            }

            // ...and it is the VALUE that comes back, not the whole "Z = 50 Ω" row.
            var value = MatchSchematicLabels.HitTest(model, baseX + 40, (bandTop + bandBot) / 2);
            Assert.DoesNotContain("=", value!.Text, StringComparison.Ordinal);
            Assert.True(value.PrefixWidth > 0, "the editor would open over the 'Z = ' prefix");
            Assert.NotNull(d.ResolveInlineEdit(value.ComponentId));
        }
        d.Dispose();
    }

    /// <summary>
    /// The rows above stay reachable — the fix is a re-split of a shared strip, not a landgrab by the
    /// value row. Row 1's own text still resolves to row 1.
    /// </summary>
    [Fact]
    public void TheRowsAbove_AreStillTheirOwn()
    {
        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);

        foreach (var c in model.Components.Where(c => c.Labels.Count > 2))
            for (int row = 0; row < 3; row++)
            {
                var (baseX, baselineY, _, _) = MatchSchematicLabels.RowGeometry(c, row);
                // Half a cap-height above the baseline is the middle of the glyphs.
                var hit = MatchSchematicLabels.HitTest(
                    model, baseX + 20, baselineY - SchematicComponent.LabelWorldHeight / 2.0);
                Assert.Equal(row, hit?.Row);
            }
        d.Dispose();
    }

    /// <summary>
    /// The pick area has a floor in SCREEN pixels, because a row is about nine of them at the zoom
    /// this pane frames a whole network at.
    /// </summary>
    [Fact]
    public void ThePickTolerance_IsAScreenDistance_NotAWorldOne()
    {
        string canvas = Canvas();
        Assert.Contains("PickPixels / _zoom", canvas, StringComparison.Ordinal);
        Assert.Contains("private const double PickPixels", canvas, StringComparison.Ordinal);

        var (_, _, d) = Open();
        var model = MatchSchematicModel.Build(d.Ladder);
        var term = model.Components.First(c => c.Symbol == SymbolKind.TermG);
        var (baseX, _, bandTop, bandBot) = MatchSchematicLabels.RowGeometry(term, 2);

        // Just outside the band: a miss with no tolerance, a hit with the tolerance a fit-zoom pane
        // would supply (4 px at a zoom of about 0.09 is roughly 44 world units).
        double justOutside = bandBot + 20;
        Assert.Null(MatchSchematicLabels.HitTest(model, baseX + 40, justOutside));
        Assert.Equal(2, MatchSchematicLabels.HitTest(model, baseX + 40, justOutside, tolerance: 44)?.Row);
        d.Dispose();
    }

    /// <summary>
    /// <b>Clicking away from a marker deselects it</b> (owner-reported, 2026-08-20: "clicking away
    /// from a marker needs to deselect it — even clicking in another panel or opening the inline text
    /// editor").
    /// </summary>
    /// <remarks>
    /// The Data Display does this from a control this window does not have: a press on the empty plot
    /// CANVAS background. The Designer lays its two plots out itself, so nothing ever deselected and a
    /// marker stayed selected — and stayed the target of Delete and the arrow keys — for the rest of
    /// the session. The handler is on the WINDOW and tunnels, because a press that lands on a plot, a
    /// slider or a specification field is handled there and bubbles nowhere useful.
    /// </remarks>
    [Fact]
    public void APressAwayFromAMarkersInfoBox_ClearsTheSelection()
    {
        string cs = Code();
        string body = Body(cs, "private void OnWindowPointerPressed(");
        output.WriteLine(body);

        Assert.Contains("AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel)",
                        cs, StringComparison.Ordinal);
        Assert.Contains("PlotHost.SelectOnly((MarkerInfoBoxViewModel?)null)", body, StringComparison.Ordinal);
        // The two gestures that must NOT undo themselves: selecting a box, and Ctrl/Meta-adding to it.
        Assert.Contains("MarkerInfoBoxView>(includeSelf: true)", body, StringComparison.Ordinal);
        Assert.Contains("KeyModifiers.Control", body, StringComparison.Ordinal);
        Assert.Contains("KeyModifiers.Meta", body, StringComparison.Ordinal);

        // The call it makes really does clear a selected marker.
        var (_, _, d) = Open();
        var trace = d.MagnitudePlot.Traces.FirstOrDefault();
        Assert.NotNull(trace);
        var marker = new Marker(trace!, trace.Data!.Frequencies[1],
                                isMulti: false, isDelta: false, index: 1);
        trace.Markers.Add(marker);
        d.MagnitudeContainer.OnPlotChanged(this, EventArgs.Empty);

        var box = d.PlotHost.MarkerInfoBoxes.FirstOrDefault(b => ReferenceEquals(b.Marker, marker));
        Assert.NotNull(box);
        d.PlotHost.SelectOnly(box);
        Assert.True(box!.IsSelected);
        Assert.True(d.PlotHost.HasSelectedInfoBoxes);

        d.PlotHost.SelectOnly((MarkerInfoBoxViewModel?)null);
        Assert.False(box.IsSelected);
        Assert.False(d.PlotHost.HasSelectedInfoBoxes);
        d.Dispose();
    }

    /// <summary>
    /// <b>The cascade's safety clamp must not contain the new window's SIZE</b> (owner-reported,
    /// 2026-08-20: "window placement on open is still in top left corner of my screen").
    /// </summary>
    /// <remarks>
    /// The offset was computed correctly and then thrown away. The clamp kept the whole window inside
    /// the working area, which needs the window's width in the units <c>Window.Position</c> uses —
    /// and converting a DIP width with <c>RenderScaling</c> assumes <c>Screen.WorkingArea</c> is in
    /// physical pixels. On macOS it is in points, the same space as the DIP width, so a 1360-DIP window
    /// measured 2720 against a 1728-wide area, the upper bound went negative, <c>Math.Max</c> floored
    /// it at <c>area.X</c>, and the window was pinned to <b>exactly (0, 0)</b> — indistinguishable
    /// from the offset never having been applied.
    ///
    /// <para>The regression is asserted in the shape that failed: a working area SMALLER than the
    /// window it is placing. It is not hypothetical — a 1360 x 841 window does not fit inside a
    /// Retina MacBook's own working area expressed in points, which is the machine this was reported
    /// from.</para>
    /// </remarks>
    [Theory]
    // area smaller than the window (the reported case), 1x and 2x
    [InlineData(1728, 1079, 1.0)]
    [InlineData(1728, 1079, 2.0)]
    // area comfortably larger
    [InlineData(3456, 2158, 2.0)]
    [InlineData(1920, 1080, 1.0)]
    public void TheCascade_IsNeverPinnedToTheScreenCorner(int areaW, int areaH, double scaling)
    {
        var owner = new PixelPoint(0, 0);
        var area = new PixelRect(0, 0, areaW, areaH);

        var at = MatchWindowPlacement.Cascade(owner, scaling, area);
        output.WriteLine($"area {areaW}x{areaH} @ {scaling}x -> {at}");

        int offset = (int)Math.Round(MatchWindowPlacement.CascadeOffset * scaling);
        Assert.Equal(owner.X + offset, at.X);
        Assert.Equal(owner.Y + offset, at.Y);
        Assert.True(at.X > 0 && at.Y > 0, "the window was pinned to the screen corner");
    }

    /// <summary>
    /// The clamp still does its job: an owner parked at the far bottom-right leaves enough of the
    /// Designer's corner on screen to grab it by.
    /// </summary>
    [Fact]
    public void TheCascade_KeepsTheWindowsCornerReachable()
    {
        var area = new PixelRect(0, 0, 1728, 1079);

        var at = MatchWindowPlacement.Cascade(new PixelPoint(1700, 1060), 1.0, area);
        output.WriteLine($"owner at the far corner -> {at}");
        Assert.True(at.X <= area.X + area.Width - MatchWindowPlacement.MinOnScreen);
        Assert.True(at.Y <= area.Y + area.Height - MatchWindowPlacement.MinOnScreen);

        // A screen that does not start at the origin — a second display left of or above the first.
        var offscreenLeft = new PixelRect(-1920, -200, 1920, 1080);
        var second = MatchWindowPlacement.Cascade(new PixelPoint(-1900, -180), 1.0, offscreenLeft);
        Assert.Equal(-1900 + 36, second.X);
        Assert.Equal(-180 + 36, second.Y);

        // No screen the platform can name: the offset still applies, unclamped.
        var unknown = MatchWindowPlacement.Cascade(new PixelPoint(120, 90), 1.0, null);
        Assert.Equal(new PixelPoint(156, 126), unknown);
    }

    /// <summary>
    /// <b>A subclass of a templated Avalonia control must declare the style key it is themed by, or it
    /// draws NOTHING.</b>
    /// </summary>
    /// <remarks>
    /// This is what "cannot double click on the TermG value" actually was, after two other real
    /// defects had been found and fixed on the way to it. Avalonia resolves a templated control's
    /// implicit <c>ControlTheme</c> by the control's OWN type and does not fall back to a base type;
    /// Fluent keys its theme on <c>typeof(TextBox)</c>, so <c>SchematicInlineEditBox</c> found no
    /// theme, got no <c>Template</c>, built no visual children and measured zero high — while
    /// remaining focusable, holding its text and reporting <c>IsVisible = true</c>. Nothing throws and
    /// nothing warns; the gesture simply appears to do nothing.
    ///
    /// <para>It took out the schematic PAGE's inline editor at the same time, silently, because that
    /// AXAML had started declaring the same type. So this is asserted as a RULE over every control in
    /// the assembly rather than for the one that was reported: a control that subclasses a framework
    /// templated control either overrides <c>StyleKeyOverride</c>, or ships a <c>ControlTheme</c> of
    /// its own keyed on its own type (which is what <c>IconSelectButton</c> does).</para>
    /// </remarks>
    [Fact]
    public void EverySubclassedTemplatedControl_IsThemed_OrItDrawsNothing()
    {
        var uiAssembly = typeof(MatchDesignerViewModel).Assembly;
        var templated = typeof(Avalonia.Controls.Primitives.TemplatedControl);

        string themes = string.Concat(Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src", "Ui", "Styles"), "*.axaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        var offenders = new List<string>();
        foreach (var t in uiAssembly.GetTypes())
        {
            if (t.IsAbstract || !templated.IsAssignableFrom(t)) continue;

            // UserControl and Window subclasses are NOT at risk and are not the rule's subject.
            // Fluent themes those through a Style whose selector matches by "is" — subclasses
            // included — while every templated PRIMITIVE (TextBox, Button, Slider…) is themed by a
            // ControlTheme keyed on an exact style key, which a subclass does not match. That is the
            // whole distinction, and the reason this application is full of `: UserControl` views
            // that render perfectly well without an override.
            if (typeof(UserControl).IsAssignableFrom(t) || typeof(Window).IsAssignableFrom(t)) continue;

            // Only types that subclass a FRAMEWORK control — a control deriving from one of ours
            // inherits whatever that one already resolved.
            var b = t.BaseType;
            if (b is null || b == templated || b.Assembly == uiAssembly) continue;

            var key = t.GetProperty("StyleKeyOverride",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            bool overridden = key?.DeclaringType == t;
            bool hasOwnTheme = themes.Contains($"ControlTheme TargetType=\"ctl:{t.Name}\"", StringComparison.Ordinal)
                            || themes.Contains($"ControlTheme TargetType=\"{t.Name}\"", StringComparison.Ordinal);

            output.WriteLine($"{t.Name} : {b.Name} — StyleKeyOverride={overridden} ownTheme={hasOwnTheme}");
            if (!overridden && !hasOwnTheme) offenders.Add($"{t.FullName} (subclasses {b.Name})");
        }

        Assert.True(offenders.Count == 0,
                    "these controls resolve no ControlTheme and will render nothing:\n  "
                    + string.Join("\n  ", offenders));

        // The one that was reported, pinned by name and by the value it must return.
        var box = typeof(CircuitRF.Ui.Controls.SchematicInlineEditBox)
            .GetProperty("StyleKeyOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(CircuitRF.Ui.Controls.SchematicInlineEditBox), box.DeclaringType);
        Assert.Equal(typeof(TextBox), box.GetValue(new CircuitRF.Ui.Controls.SchematicInlineEditBox()));
    }

    // ══ Layout, headings and chrome ══════════════════════════════════════════

    /// <summary>
    /// <b>The specification column is about 100 px narrower</b> (owner: "reduce the width of the
    /// Specification panel… looks like a reduction of 100 pixels or so. Careful — we don't want
    /// overlapping text").
    /// </summary>
    /// <remarks>
    /// The second half of that ask is the one worth a test, and it is arithmetic rather than pixels:
    /// the widest row in the card is the topology label plus its selector, and it has to fit inside
    /// what the pane's padding, the card's padding and the pictogram leave of the column.
    /// </remarks>
    [Fact]
    public void TheSpecificationColumn_IsNarrower_AndItsWidestRowStillFits()
    {
        string xaml = Xaml();
        var cols = Regex.Match(xaml, @"ColumnDefinitions=""(\d+),\*,380""");
        Assert.True(cols.Success, "the three-pane grid no longer declares a fixed first column");

        double column = double.Parse(cols.Groups[1].Value, CultureInfo.InvariantCulture);
        // 215 was "100 pixels or so" off 300 (owner, 2026-08-20); 285 is that WIDENED "slightly"
        // (owner, 2026-08-28) because the Solutions list moved into this pane and a solution card
        // carries a family name, an order and an Apply button. Still narrower than the 300 it started
        // at, which is the claim this test is about.
        Assert.InRange(column, 190, 295);

        const double paneMargin = 4 * 2, panePadding = 10 * 2;
        const double cardBorder = 1 * 2, cardPadding = 8 * 2;
        double cardInterior = column - paneMargin - panePadding - cardBorder - cardPadding;

        double pictogram = Width(xaml, "MatchPictogramControl");
        const double stackMargin = 6;
        double forRows = cardInterior - pictogram - stackMargin;

        // "Topology" at 10 pt in a proportional face is comfortably under 50 px.
        const double topologyLabel = 50.0;
        double selector = Width(xaml, "MatchTerminationViewModel.TopologyOptions", lookBack: true);

        output.WriteLine($"column {column}, card interior {cardInterior}, pictogram {pictogram}, "
                         + $"rows get {forRows}, widest row {topologyLabel + selector}");
        Assert.True(forRows > topologyLabel + selector,
                    $"the topology row ({topologyLabel + selector}) does not fit in {forRows}");

        static double Width(string xaml, string near, bool lookBack = false)
        {
            int i = xaml.IndexOf(near, StringComparison.Ordinal);
            Assert.True(i >= 0, $"'{near}' is not in the AXAML");
            string window = lookBack ? xaml[Math.Max(0, i - 400)..i] : xaml[i..Math.Min(xaml.Length, i + 400)];
            var m = Regex.Match(window, @"Width=""(\d+)""");
            Assert.True(m.Success, $"no Width beside '{near}'");
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The three headings the owner renamed, and the one that deliberately did not move.</summary>
    [Fact]
    public void TheRenamedHeadings_SayWhatTheyAreAbout()
    {
        string xaml = Xaml();
        Assert.Contains("Text=\"Impedance Matching Network\"", xaml, StringComparison.Ordinal);
        // Merged with Ripple on 2026-08-28 — see MatchRound9Tests. The RENAME this test is about
        // ("Band" -> "Frequency Band") is still in force; it is now the first half of one heading.
        Assert.Contains("Text=\"Frequency Band &amp; Ripple\"", xaml, StringComparison.Ordinal);
        // "Filter Response" is NOT here any more — that card was removed on 2026-08-28 with Order
        // and Options, because the Solutions list spans every family and every order and none of the
        // three is an input to it. The rename it was the subject of is preserved where it still
        // applies: the filter's own lines name the four families.
        Assert.DoesNotContain("cardhdr\" Text=\"Filter Response\"", xaml, StringComparison.Ordinal);

        // The plots pane is still "Response" — the rename was of the SPECIFICATION card below the
        // band, and this is a different heading in a different pane.
        Assert.Contains("Classes=\"panelhdr\" Text=\"Response\"", xaml, StringComparison.Ordinal);

        // The old spellings of the three that moved are gone.
        Assert.DoesNotContain("panelhdr\" Text=\"Network\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("cardhdr\" Text=\"Band\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("cardhdr\" Text=\"Response\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The title-bar buttons are icons</b> (owner: "change the Solutions, Export, Help and Close
    /// buttons to icons, similar to how the Schematic editor toolbar looks").
    /// </summary>
    /// <remarks>
    /// A glyph with no tooltip is a button nobody can identify, so what is asserted is the SWAP: the
    /// word off the face, a <c>MaterialIcon</c> on it, and the word still reachable as a tooltip.
    /// </remarks>
    [Fact]
    public void TheTitleBarButtons_AreIconsWithTooltips()
    {
        string xaml = Xaml();

        foreach (string word in new[] { "Solutions", "Settings", "Export", "Help", "Close" })
            Assert.DoesNotContain($"Content=\"{word}\"", xaml, StringComparison.Ordinal);

        // CLOSE is gone entirely rather than iconified (owner, 2026-08-20: "remove the close button
        // from the top of window") — the title bar already carries one. Asserted on BOTH surfaces:
        // a button left in the AXAML with no handler is a button that silently does nothing, and a
        // handler left wired to a button that no longer exists is dead code that reads as live.
        Assert.DoesNotContain("CloseButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseButton", Code(), StringComparison.Ordinal);

        foreach (var (name, kind, tip) in new[]
        {
            ("SettingsButton", "CogOutline",       "Settings"),
            ("ExportButton",   "ExportVariant",    "Export"),
            ("HelpButton",     "HelpCircleOutline", "Help"),
        })
        {
            int i = xaml.IndexOf($"Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(i > 0, $"{name} is not in the AXAML");
            string block = xaml[i..Math.Min(xaml.Length, i + 400)];
            Assert.Contains($"Kind=\"{kind}\"", block, StringComparison.Ordinal);
            Assert.Contains($"ToolTip.Tip=\"{tip}", block, StringComparison.Ordinal);
        }

        // The Solutions drawer's toggle is gone with the drawer (owner, 2026-08-28: the list moved
        // into the specification pane and is always out). Asserted on BOTH surfaces for the reason
        // Close is: a binding left behind names a property that no longer exists, and a property left
        // behind is state nothing reads.
        Assert.DoesNotContain("SolutionsPanelOpen", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatListBulleted", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The brace's straight vertical run is shorter again</b> (owner: "reduce the curly brace's
    /// vertical line length rendering above the N1, N2 text in the schematic view").
    /// </summary>
    [Fact]
    public void TheBraceStem_IsShorterThanTheLabelDrop_AndTheRowsStillClear()
    {
        Assert.Equal(26.25, MatchLadderLayout.BraceStem, 9);

        var stem = MatchBraceGeometry.Stem(0, 2000, MatchLadderLayout.BracketY,
                                           MatchLadderLayout.BraceCurl,
                                           MatchLadderLayout.BraceStem,
                                           MatchLadderLayout.BraceLabelDrop)!.Value;
        Assert.Equal(MatchLadderLayout.BraceStem, stem.Y1 - stem.Y0, 9);

        // A row still has to hold curl + stem + label drop + one cap height, and shortening the stem
        // only ever gives that arithmetic more room — the check is that nobody shrank the pitch too.
        double needed = MatchLadderLayout.BraceCurl + MatchLadderLayout.BraceStem
                        + MatchLadderLayout.BraceLabelDrop + SchematicComponent.LabelWorldHeight;
        output.WriteLine($"row needs {needed}, pitch is {MatchLadderLayout.BracketRowPitch}");
        Assert.True(MatchLadderLayout.BracketRowPitch > needed);
    }

    // ══ The response pane ════════════════════════════════════════════════════

    /// <summary>The two plots are named for what is drawn on them.</summary>
    [Fact]
    public void ThePlotTitles_NameTheirOwnTraces()
    {
        var (_, _, d) = Open();
        Assert.Equal("Return and Insertion Loss", d.MagnitudePlot.CustomTitle);
        Assert.Equal("Phase and Group Delay", d.PhasePlot.CustomTitle);
        Assert.True(d.MagnitudePlot.CustomTitleOn);
        Assert.True(d.PhasePlot.CustomTitleOn);
        d.Dispose();
    }

    /// <summary>
    /// <b>A double-click near a trace adds a marker there</b> (owner: "double-clicking on a plot trace
    /// does not create a marker at the spot it was clicked — this already works in a true Data Display
    /// plot").
    /// </summary>
    /// <remarks>
    /// <c>PlotControl.HandleDoubleTapAt</c> is documented as "called by the HOST on DoubleTapped", and
    /// this window is the host — it simply never called it. Asserted against the wiring, in the same
    /// method that supplies the four provider delegates, because the mechanism is a subscription and
    /// there is no live plot to click in a headless run.
    /// </remarks>
    [Fact]
    public void TheResponsePlots_ForwardTheirOwnDoubleTap()
    {
        string bind = Body(Code(), "private void WirePlotHost()");
        Assert.Contains("plot.DoubleTapped", bind, StringComparison.Ordinal);
        Assert.Contains("plot.HandleDoubleTapAt(", bind, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Delete removes a selected marker</b> (owner: "clicking &lt;delete&gt; keystroke with a
    /// marker selected does not remove the marker from the trace").
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the Data Display's <c>DeleteSelected</c>: that also removes selected plot
    /// CONTAINERS, and these two plots are declared undeletable in the same AXAML. Both halves are
    /// asserted, because binding the wrong command would look identical until somebody selected a
    /// plot.
    /// </remarks>
    [Fact]
    public void Delete_RemovesASelectedMarker_AndCannotRemoveAPlot()
    {
        string xaml = Xaml();
        Assert.Contains("Gesture=\"Delete\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DeleteSelectedMarkersCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanDeletePlot=\"False\"", xaml, StringComparison.Ordinal);

        string vmSrc = Src("src", "Ui", "Match", "MatchDesignerViewModel.Response.cs");
        string body = Body(vmSrc, "public void DeleteSelectedMarkers()");
        Assert.Contains("MarkerInfoBoxes", body, StringComparison.Ordinal);
        Assert.Contains("RemoveMarkerWithUndo", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RemovePlot", body, StringComparison.Ordinal);

        // The command exists on a live view-model and is harmless with nothing selected.
        var (_, _, d) = Open();
        d.DeleteSelectedMarkersCommand.Execute(null);
        d.Dispose();
    }

    /// <summary>
    /// <b>The band and points boxes commit on Return</b> (owner: "committing using return key on band
    /// and points textedit boxes does not update the frequency response plots").
    /// </summary>
    [Fact]
    public void TheBandAndPointsBoxes_CommitOnReturnAsWellAsOnFocusLoss()
    {
        string bind = Body(Code(), "private void BindNumericBox(");
        output.WriteLine(bind);
        Assert.Contains("box.LostFocus", bind, StringComparison.Ordinal);
        Assert.Contains("box.KeyDown", bind, StringComparison.Ordinal);
        Assert.Contains("Key.Return", bind, StringComparison.Ordinal);

        // Both boxes go through it, and both land on the one method that re-runs the response.
        Assert.Contains("BindNumericBox(", Code(), StringComparison.Ordinal);
        Assert.Contains("CommitPlotWindow(", Code(), StringComparison.Ordinal);

        var (_, _, d) = Open();
        d.CommitPlotWindow(MatchDesignerViewModel.ParseBandPercent("25%"), null);
        Assert.Equal(0.25, d.PlotBandFraction, 9);
        d.CommitPlotWindow(null, MatchDesignerViewModel.ParsePlotPoints("801"));
        Assert.Equal(801, d.PlotPoints);

        // The frequency grid — what "the plots did not update" was actually about — followed.
        var f = d.PlotFrequencies();
        Assert.Equal(801, f.Length);
        Assert.Equal(d.Design.F1 - 0.25 * (d.Design.F2 - d.Design.F1), f[0], 0);
        d.Dispose();
    }

    /// <summary>
    /// <b>The export picker spells the extension once</b> (owner-reported: "Export Touchstone file
    /// picker shows .s2p twice in its suggested file name").
    /// </summary>
    [Fact]
    public void TheExportPicker_DoesNotSpellTheExtensionTwice()
    {
        string save = Body(Code(), "private async Task SaveAsync(");
        output.WriteLine(save);
        Assert.Contains("SuggestedFileName = Vm.InstanceName", save, StringComparison.Ordinal);
        Assert.DoesNotContain("SuggestedFileName = $\"{Vm.InstanceName}.{extension}\"", save, StringComparison.Ordinal);
        // DefaultExtension stays — it is what gives a name typed WITHOUT one an extension.
        Assert.Contains("DefaultExtension = extension", save, StringComparison.Ordinal);
    }

    // ══ The window itself ════════════════════════════════════════════════════

    /// <summary>
    /// <b>The Designer is in the Window menu</b> (owner: "have it show up in the circuitRF Window
    /// menu, just like any other window").
    /// </summary>
    /// <remarks>
    /// Through an interface, not a type check in the view-model: a menu built from a list of concrete
    /// window classes is a menu the NEXT such window is silently missing from. That is the property
    /// worth pinning, so both halves are asserted — the enumeration reads
    /// <see cref="CircuitRF.Ui.ICrfMenuWindow"/>, and the Designer implements it.
    /// </remarks>
    [Fact]
    public void TheDesignerWindow_IsListedInTheWindowMenu()
    {
        string menu = Src("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs");
        Assert.Contains("OfType<ICrfMenuWindow>()", menu, StringComparison.Ordinal);
        Assert.Contains("WindowMenuHeader", menu, StringComparison.Ordinal);
        Assert.Contains("PlatformImpl is not null", menu, StringComparison.Ordinal);

        Assert.Contains("MatchDesignerWindow : Window, ICrfMenuWindow", Code(), StringComparison.Ordinal);
        Assert.Contains("public string WindowMenuHeader", Code(), StringComparison.Ordinal);

        // Non-modal, which is the other half of the same ask — and non-modal turned out not to be
        // enough. See TheDesignerWindow_GoesBehindTheWorkspace below.
        Assert.DoesNotContain("window.ShowDialog", Code(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The Designer can go BEHIND the workspace</b> (owner-reported, 2026-08-20: "the Match
    /// Designer window is always in front. I can't get back to the workspace with the designer window
    /// open").
    /// </summary>
    /// <remarks>
    /// The window was already non-modal; that was never the property in question. <c>Show(owner)</c>
    /// makes an OWNED window, which every platform pins above its owner in the z-order for its whole
    /// life — so clicking the workspace raised the workspace underneath it. The fix is to drop the
    /// owner and take on the two things it was doing: placing the window over the workspace, and
    /// closing it with the workspace. All three are asserted, because dropping the owner without
    /// either of the other two would trade this bug for a window that opens in a corner and outlives
    /// the session.
    /// </remarks>
    [Fact]
    public void TheDesignerWindow_GoesBehindTheWorkspace()
    {
        string cs = Code();

        Assert.DoesNotContain("window.Show(owner", cs, StringComparison.Ordinal);
        Assert.Contains("ShowUnowned(window, owner)", cs, StringComparison.Ordinal);

        string show = Body(cs, "private static void ShowUnowned(");
        output.WriteLine(show);
        Assert.Contains("window.Show();", show, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner =", show, StringComparison.Ordinal);

        // Placement — CenterOwner is unavailable without an Owner, so the position is computed, and
        // it is a CASCADE off the workspace's own corner (owner, 2026-08-20: "needs to open slightly
        // down and to the right of the parent window that opened it"). A centred Designer covers
        // exactly the part of the workspace a user reaches for to get back to it. The arithmetic
        // itself is MatchWindowPlacement's and is exercised directly, below.
        Assert.Contains("WindowStartupLocation.Manual", show, StringComparison.Ordinal);
        Assert.Contains("CascadedFrom(owner)", show, StringComparison.Ordinal);
        Assert.Contains("MatchWindowPlacement.Cascade", Body(cs, "private static PixelPoint CascadedFrom("),
                        StringComparison.Ordinal);

        // Assigned BEFORE Show (no visible jump) and again AFTER (a platform that ignores a Move on
        // an unshown window would otherwise place it wherever it liked).
        // Searched from the OWNED branch onwards — the no-owner early return above it has a
        // window.Show() of its own, and finding that one first would compare the wrong pair.
        int owned      = show.IndexOf("WindowStartupLocation.Manual", StringComparison.Ordinal);
        Assert.True(owned > 0);
        int beforeShow = show.IndexOf("window.Position = at;", owned, StringComparison.Ordinal);
        int atShow     = show.IndexOf("window.Show();", owned, StringComparison.Ordinal);
        int afterShow  = show.IndexOf("window.Position = at;", atShow, StringComparison.Ordinal);
        Assert.True(beforeShow > 0 && beforeShow < atShow, "the position is not set before Show()");
        Assert.True(afterShow > atShow, "the position is not re-asserted after Show()");
        // ...and clamped, so an owner parked at the far corner still leaves the Designer reachable.
        // The clamp is MatchWindowPlacement's, and TheCascade_* below exercise what it does.
        Assert.Contains("Math.Clamp", Src("src", "Ui", "Views", "Match", "MatchWindowPlacement.cs"),
                        StringComparison.Ordinal);

        // Lifetime — an owned window closed with its owner; this one has to be told to.
        Assert.Contains("owner.Closed += CloseWithOwner", show, StringComparison.Ordinal);
        Assert.Contains("owner.Closed -= CloseWithOwner", show, StringComparison.Ordinal);

        // With no owner at all there is nothing to centre on, so it centres on the screen instead —
        // never left at the platform's default corner.
        Assert.Contains("WindowStartupLocation.CenterScreen", show, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Deleting the component leaves the window open and READ-ONLY</b> (owner: "what happens if
    /// user goes back to schematic and deletes its instance? Perhaps the window becomes orphaned? I am
    /// ok with that. Just need to handle it gracefully").
    /// </summary>
    /// <remarks>
    /// The graceful part is that nothing is written to a component that is not in the drawing, and
    /// that the state is REVERSIBLE — an undo of the deletion makes the Designer live again. Both are
    /// checked, along with the one line the user sees.
    /// </remarks>
    [Fact]
    public void AnOrphanedDesigner_StopsWriting_StaysReadable_AndComesBack()
    {
        var (vm, comp, d) = Open();
        Assert.False(d.IsOrphaned);

        double before = d.Design.Term1.R;
        vm.EditModel.Components.Remove(comp);
        vm.EditModel.NotifyChanged();

        Assert.True(d.IsOrphaned);
        Assert.Contains("deleted from its schematic", d.OrphanNote, StringComparison.Ordinal);
        Assert.False(d.CanFlatten);
        Assert.False(d.Term1.CanProbe);

        // The window is still readable: the ladder and the status strip are exactly as they were.
        Assert.NotEmpty(d.Ladder.Elements);
        Assert.Equal(before, d.Design.Term1.R, 9);

        // An edit changes the working design but writes NOTHING to the component.
        d.Term1.Resistance = 90.0;
        Assert.Equal("50", comp.Parameters.First(p => p.Name == "R1").Expression);

        // ...and putting the component back makes it live again.
        vm.EditModel.Components.Add(comp);
        vm.EditModel.NotifyChanged();
        Assert.False(d.IsOrphaned);
        d.Dispose();
    }

    // ══ Label LOD ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Labels survive two more scroll-wheel clicks before they fade</b> (owner: "the schematic
    /// labels disappear when user zooms out far. That is ok, but keep the text rendering for 2 more
    /// zoom out clicks of the scroll wheel before the text is no longer rendered. This change request
    /// applies to the regular schematic too").
    /// </summary>
    /// <remarks>
    /// "Two clicks" is arithmetic, not a number somebody liked: the canvas zooms by a factor of 1.15
    /// per click, so the threshold has to fall by 1.15². Asserted as that ratio against the value the
    /// PREVIOUS such request left, so the claim stays checkable rather than becoming a literal nobody
    /// can date. Both canvases zoom by the same factor, which is why one threshold serves both.
    /// </remarks>
    [Fact]
    public void TheLabelThreshold_DropsByTwoScrollWheelClicks()
    {
        string renderer = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Renderers", "SchematicRenderer.cs"));
        var m = Regex.Match(renderer, @"SimplifiedThreshold\s*=\s*([\d.]+)");
        Assert.True(m.Success);
        double now = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

        const double previous = 16.64;   // what the 2026-06-24 round left it at
        const double click = 1.15;
        output.WriteLine($"{previous} / {click}^2 = {previous / (click * click):F3}, threshold is {now}");
        Assert.Equal(previous / (click * click), now, 2);

        // Both surfaces zoom by that factor, which is what makes ONE threshold serve both.
        Assert.Contains("ZoomFactor = 1.15", Src("src", "Ui", "Controls", "SchematicCanvas.cs"),
                        StringComparison.Ordinal);
        Assert.Contains("ZoomStep = 1.15", Canvas(), StringComparison.Ordinal);

        // The threshold is now a QUESTION the renderer answers, so the network pane's label hit-test
        // cannot offer a click target for text that is faded out. Two clicks either side of it.
        double at = now / 300.0;                       // the zoom the threshold sits at
        Assert.True(CircuitRF.Ui.Renderers.SchematicRenderer.LabelsVisibleAt(at * click));
        Assert.False(CircuitRF.Ui.Renderers.SchematicRenderer.LabelsVisibleAt(at / click));
        Assert.Contains("SchematicRenderer.LabelsVisibleAt(_zoom)", Canvas(), StringComparison.Ordinal);
    }
}
