using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// <b>Phase L9d / M4 — the Ui half: which levels are in the analysis, and that saying so
/// round-trips.</b>
/// </summary>
public sealed class EmMultiLevelSetupTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    [Fact]
    public void ACemThatNamesNoLevels_RoundTripsByteIdentically()
    {
        // D5's additivity rule, the same one PortZ0s already follows: a setup that never named its
        // levels writes no field, so every .cem written before L9d loads AND re-serialises unchanged.
        var setup = new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        string json = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("AnalysisLevelNames", json);

        var back = EmSetupPersistence.Deserialize(json);
        Assert.Empty(back.AnalysisLevelNames);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));
    }

    [Fact]
    public void NamedLevels_RoundTripInOrder()
    {
        var setup = new EmSetup
        {
            Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            AnalysisLevelNames = ["Metal1", "Metal2"],
        };
        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup));
        Assert.Equal(["Metal1", "Metal2"], back.AnalysisLevelNames);

        // …and they reach the extractor through the ONE place the two are married, so the panel and
        // the run service cannot disagree about them.
        var extraction = back.ToExtractionSettings();
        Assert.NotNull(extraction.AnalysisLevelNames);
        Assert.Equal(["Metal1", "Metal2"], extraction.AnalysisLevelNames);
    }

    [Fact]
    public void AnalysisLevelRows_ListEverySignalConductor_AndToggleCommitsUndoably()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-l9d.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => new EmLayoutSource("a.clay", new LayoutView { DbuPerMicron = Dbu }, tech, Dbu),
        };
        vm.Refresh();

        var signals = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference)
            .Select(l => l.Name).ToHashSet();
        Assert.Equal(signals, vm.AnalysisLevelRows.Select(r => r.Name).ToHashSet());
        Assert.All(vm.AnalysisLevelRows, r => Assert.False(r.IsIncluded));   // none = infer

        var row = vm.AnalysisLevelRows[0];
        row.IsIncluded = true;
        Assert.Contains(row.Name, vm.Working.AnalysisLevelNames);
        Assert.True(vm.IsDirty);

        vm.UndoCommand.Execute(null);
        Assert.DoesNotContain(row.Name, vm.Working.AnalysisLevelNames);

        vm.RedoCommand.Execute(null);
        Assert.Contains(row.Name, vm.Working.AnalysisLevelNames);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // EM-SEV R-emsev-6 — the note has to be ON SCREEN, which is not the same as being produced
    // ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // R-emsev-6 asked for one line under the level list "while the tick boxes that fix it are on
    // screen", and the line was built. It rendered nowhere: the Expander it lives in ships
    // collapsed and an Expander does not realise its content until it opens, so all the user saw
    // was the header — "Analysis levels — 1 of 3 included" — which is true and names nothing. Two
    // users' worth of silently-absent MIM capacitors later (2026-09-16), the view model opens it.
    //
    // Asserting the NOTE alone is what let this ship: it was correct the whole time.

    [Fact]
    public void ArtworkOnAnUntickedLevel_ProducesTheNote_AndOPENS_TheExpander()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(new LayerKey(1, 0), 0, 0, 40, 10));     // Metal1  — ticked
        view.Shapes.Add(Rect(new LayerKey(9, 0), 5, 2, 35, 8));      // MIM Metal — NOT ticked

        var vm = Editor(tech, view, ["Metal1"]);
        vm.Refresh();

        Assert.NotNull(vm.ExcludedArtworkNote);
        Assert.Contains("MIM Metal", vm.ExcludedArtworkNote!, StringComparison.Ordinal);

        // The half that was missing. Without this the sentence above is written to a control the
        // window never builds.
        Assert.True(vm.AnalysisLevelsExpanded,
                    "the level list stayed collapsed, so the note naming 'MIM Metal' is not on screen");
    }

    [Fact]
    public void WithNothingLeftOut_ThereIsNoNote_AndTheExpanderStaysShut()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(new LayerKey(1, 0), 0, 0, 40, 10));
        view.Shapes.Add(Rect(new LayerKey(9, 0), 5, 2, 35, 8));

        var vm = Editor(tech, view, ["Metal1", "MIM Metal"]);
        vm.Refresh();

        Assert.Null(vm.ExcludedArtworkNote);
        Assert.False(vm.AnalysisLevelsExpanded);   // it must not open on every setup
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // EM-SEV R-emsev-1 in the PANEL — the run ranked these; the dialog did not
    // ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // `EmRunService` splits the kernel's findings by class and `WorkspaceViewModel` posts errors,
    // then warnings, then notes. This panel joined every sentence into one block with
    // `extraction.Notes`, which is `EmFindings.Texts` — every finding's text, class discarded. So
    // the three warnings that said a MIM capacitor was not in the solve read as three paragraphs
    // among eighteen, in the dialog that is open while the tick box fixing them is on screen.
    //
    // The gate is BOTH halves: the warning is in the warning list, and it is NOT also in the note
    // list — taking `Notes` for one and `Findings` for the other prints every warning twice.

    [Fact]
    public void ExtractionWarnings_AreSeparatedFromTheNotes_NotJoinedIntoThem()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(new LayerKey(1, 0), 0, 0, 40, 10));     // Metal1  — ticked
        view.Shapes.Add(Rect(new LayerKey(9, 0), 5, 2, 35, 8));      // MIM Metal — NOT ticked

        var vm = Editor(tech, view, ["Metal1"]);
        vm.Refresh();

        string warned = Assert.Single(
            vm.Warnings, w => w.Contains("carry artwork but are NOT in this", StringComparison.Ordinal));
        Assert.Contains("MIM Metal", warned, StringComparison.Ordinal);

        Assert.DoesNotContain(vm.Notes, n => n.Contains("carry artwork but are NOT in this",
                                                       StringComparison.Ordinal));
        Assert.True(vm.HasWarnings);
        Assert.Contains("MIM Metal", vm.WarningsText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A structure with nothing left out ranks nothing, and the block takes no height.
    ///
    /// <para><b>All three conductors are drawn and ticked on purpose.</b> With Metal2 left out, the
    /// run reports Metal2's artwork as not in the analysis — a real and correctly-classed warning
    /// about a different thing, which would make this test pass or fail on something it is not
    /// about.</para>
    ///
    /// <para><b>And the NITRIDE is drawn, since MIM-11.</b> The shipped technology ties its MIM
    /// Dielectric to that mask rather than to the plate conductor, so a plate with no mask over it
    /// is a capacitor solved with AIR between its plates — which warns, correctly. A complete
    /// capacitor is its two plates AND its mask, which is what <c>KIT_MIMCAP</c> draws.</para>
    /// </summary>
    [Fact]
    public void WithNothingLeftOut_TheWarningBlockIsEmpty()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(new LayerKey(1, 0), 0,  0, 40, 10));
        view.Shapes.Add(Rect(new LayerKey(9, 0), 5,  2, 35,  8));
        view.Shapes.Add(Rect(new LayerKey(6, 0), 5,  2, 35,  8));   // Nitride, the film's own mask
        view.Shapes.Add(Rect(new LayerKey(2, 0), 10, 3, 30,  7));

        var vm = Editor(tech, view, ["Metal1", "MIM Metal", "Metal2"]);
        vm.Refresh();

        Assert.False(vm.HasWarnings, "warnings: " + string.Join(" || ", vm.Warnings));
        Assert.Empty(vm.Warnings);
        Assert.NotEmpty(vm.Notes);      // the run still explains itself
    }

    private static EmSetupEditorViewModel Editor(
        Technology tech, LayoutView view, string[] levels) =>
        new(Path.Combine(Path.GetTempPath(), "unused-emsev6.cem"),
            new EmSetup
            {
                Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
                AnalysisLevelNames = [.. levels],
            })
        {
            ResolveLayout = _ => new EmLayoutSource("a.clay", view, tech, Dbu),
        };

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new()
        {
            Layer = layer,
            X1 = (long)(x0 * Dbu), Y1 = (long)(y0 * Dbu),
            X2 = (long)(x1 * Dbu), Y2 = (long)(y1 * Dbu),
        };
}
