// ================================================================
//  HarmonicaDocumentTests.cs  —  M3 of brief-harmonicarf-h4-h5
//
//  R-h45-13  the Tools menu, with harmonicaRF as its only entry, on BOTH hand-mirrored surfaces.
//  R-h45-3   markers are properties of the CIRCUIT — the same instances on both Smith panels.
//  R-h45-7   §7.3's ONE plane toggle, and §7.4's click-to-cycle X unit.
//  TIER 6    the DCIV family is computed ONCE across a termination drag.
//  end-to-end the product path: a new document solves a real frame through the real engine.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaDocumentTests(ITestOutputHelper output)
{
    // ══ R-h45-13 — the Tools menu ════════════════════════════════════════════

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static XDocument WorkspaceWindowXaml()
        => XDocument.Load(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml"));

    [Fact]
    public void ToolsMenu_ExistsOnBothSurfaces_WithTheSameEntriesInTheSameOrder()
    {
        // D10: "The Tools menu is added HERE, not at H7. §10 allocates it to H7, but a document
        // nobody can open cannot be tested through the product path."
        //
        // Asserted on BOTH surfaces because this codebase maintains the macOS NativeMenu and the
        // in-window Menu by hand, and its own history records them drifting apart when only one was
        // checked (the Technology import items ordered differently on each).
        var doc = WorkspaceWindowXaml();

        var native = doc.Descendants()
            .Where(e => e.Name.LocalName == "NativeMenuItem"
                     && (string?)e.Attribute("Header") == "Tools")
            .ToList();
        Assert.Single(native);

        var nativeEntries = native[0].Descendants()
            .Where(e => e.Name.LocalName == "NativeMenuItem")
            .Select(e => (string?)e.Attribute("Header"))
            .ToList();
        // Kept EXACT and ORDERED, which is the property that actually stops the two hand-mirrored
        // surfaces drifting — wbond.md §10's Tools entry is the second one to arrive here.
        Assert.Equal(["harmonicaRF", "wBond"], nativeEntries);
        Assert.Contains(native[0].Descendants(),
            e => ((string?)e.Attribute("Command"))?.Contains("NewHarmonicaCommand") == true);
        Assert.Contains(native[0].Descendants(),
            e => ((string?)e.Attribute("Command"))?.Contains("NewWBondCommand") == true);

        var inWindow = doc.Descendants()
            .Where(e => e.Name.LocalName == "MenuItem"
                     && (string?)e.Attribute("Header") == "_Tools")
            .ToList();
        Assert.Single(inWindow);

        var inWindowEntries = inWindow[0].Descendants()
            .Where(e => e.Name.LocalName == "MenuItem")
            .Select(e => (string?)e.Attribute("Header"))
            .ToList();
        Assert.Equal(["_harmonicaRF", "_wBond"], inWindowEntries);
        Assert.Contains(inWindow[0].Descendants(),
            e => ((string?)e.Attribute("Command"))?.Contains("NewHarmonicaCommand") == true);
        Assert.Contains(inWindow[0].Descendants(),
            e => ((string?)e.Attribute("Command"))?.Contains("NewWBondCommand") == true);
    }

    [Fact]
    public void ToolsMenu_SitsBetweenSimulateAndWindow_OnBothSurfaces()
    {
        // Menu ORDER is part of the surface. Both lists are checked so the two cannot drift.
        var doc = WorkspaceWindowXaml();

        var nativeTop = doc.Descendants()
            .Where(e => e.Name.LocalName == "NativeMenuItem" && e.Parent?.Name.LocalName == "NativeMenu")
            .Select(e => (string?)e.Attribute("Header"))
            .Where(h => h is "Simulate" or "Tools" or "Window")
            .ToList();
        Assert.Equal(["Simulate", "Tools", "Window"], nativeTop);

        var inWindowTop = doc.Descendants()
            .Where(e => e.Name.LocalName == "MenuItem" && e.Parent?.Name.LocalName == "Menu")
            .Select(e => (string?)e.Attribute("Header"))
            .Where(h => h is "_Simulate" or "_Tools" or "_Window")
            .ToList();
        Assert.Equal(["_Simulate", "_Tools", "_Window"], inWindowTop);
    }

    [Fact]
    public void HarmonicaDocument_HasADataTemplate_SoTheTabRendersRatherThanShowingItsTypeName()
    {
        // A Dock document with no DataTemplate renders as its type name — the failure mode is silent
        // and looks like the view is broken rather than absent.
        var app = XDocument.Load(System.IO.Path.Combine(RepoRoot(), "src", "Ui", "App.axaml"));
        bool wired = app.Descendants()
            .Where(e => e.Name.LocalName == "DataTemplate")
            .Any(e => ((string?)e.Attribute("DataType"))?.Contains("HarmonicaDocument") == true
                   && e.Descendants().Any(c => c.Name.LocalName == "HarmonicaView"));
        Assert.True(wired, "HarmonicaDocument has no DataTemplate in App.axaml");
    }

    // ══ R-h45-3 — a marker is a property of the CIRCUIT ══════════════════════

    [Fact]
    public void Markers_AreTheSameInstancesOnBothSmithPanels_SoAMoveLandsOnBothInOneFrame()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var p = vm.Frame.SmithPower.Markers;
        var e = vm.Frame.SmithEfficiency.Markers;

        Assert.NotEmpty(p);
        Assert.Equal(p.Count, e.Count);

        // Reference identity, not equality — "both are views of the same model object" is the claim,
        // and equal-but-distinct objects would satisfy a value comparison while still needing a
        // synchronisation step somebody has to remember to perform.
        for (int i = 0; i < p.Count; i++)
            Assert.Same(p[i], e[i]);

        // Moving one moves it on the other, with no second write anywhere.
        var l1 = p.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        var before = l1.Gamma;
        vm.SetMarkerGamma(l1, new Complex(0.42, -0.17));

        Assert.NotEqual(before, l1.Gamma);
        Assert.Same(l1, vm.Frame.SmithEfficiency.Markers.First(m => m.Name == "L1"));
        Assert.Equal(l1.Gamma, vm.Frame.SmithEfficiency.Markers.First(m => m.Name == "L1").Gamma);
    }

    [Fact]
    public void SettingAMarker_WritesTheEngineTerminationToo_SoThereIsOneSourceOfTruth()
    {
        var vm = new HarmonicaViewModel();
        var l1 = vm.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);

        vm.SetMarkerImpedance(l1, new Complex(31.4, -12.7));

        // The marker's Γ and the TerminationSet the engine reads must agree — two sources for
        // "what is band 1 terminated in" would drift the moment either was written alone.
        var z = vm.Terminations.Z(TerminationSide.Load, 1);
        Assert.Equal(31.4, z.Real,      precision: 9);
        Assert.Equal(-12.7, z.Imaginary, precision: 9);
        Assert.Equal((z - 50.0) / (z + 50.0), l1.Gamma);
    }

    // ══ R-h45-7 — the plane toggle and the X-unit cycle ══════════════════════

    [Fact]
    public void PlaneToggle_MovesTheDcivFamilyAndTheLoadlineTogether_AndTheIndicatorIsNeverAbsent()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        // §7.3: "one toggle, not two, so the two curves are always in the same plane and cannot be
        // misleadingly superimposed."
        Assert.True(vm.IntrinsicPlane);
        Assert.True(vm.Frame.Loadline.Intrinsic);
        Assert.Equal("intrinsic", vm.Frame.Loadline.PlaneLabel);

        vm.ToggleLoadlinePlaneCommand.Execute(null);

        Assert.False(vm.IntrinsicPlane);
        Assert.False(vm.Frame.Loadline.Intrinsic);
        // "A persistent subtle indicator on the panel states which plane is shown; it is NEVER absent."
        Assert.Equal("extrinsic", vm.Frame.Loadline.PlaneLabel);
        Assert.False(string.IsNullOrWhiteSpace(vm.Frame.Loadline.PlaneLabel));
    }

    [Fact]
    public void XUnitCycle_RelabelsWithoutResolving()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        int solvesAfterFirstFrame = vm.LastSolveCount;
        var poutBefore = vm.Frame.PowerSweep.PoutDbm;

        for (int i = 0; i < 4; i++) vm.CyclePowerSweepXUnitCommand.Execute(null);

        // Four steps is a full cycle (§7.4), and none of them may cost a solve: a unit change is a
        // relabel of data already in hand.
        Assert.Equal(PowerSweepXUnit.PoutDbm, vm.PowerSweepXUnit);
        Assert.Equal(solvesAfterFirstFrame, vm.LastSolveCount);
        Assert.Same(poutBefore, vm.Frame.PowerSweep.PoutDbm);
    }

    // ══ TIER 6 — the DCIV family is computed ONCE across a termination drag ══

    [Fact]
    public void Tier6_TheDcivFamilyIsComputedOnce_AcrossAWholeTerminationDrag()
    {
        // §6.8: "Tier C — the DCIV is computed once and held. It depends only on the model, its
        // parameters and the bias sweep range — NEVER on terminations."
        var vm = new HarmonicaViewModel();
        var l1 = vm.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        Assert.Equal(1, vm.DcivComputeCount);
        int curves = vm.Frame.Loadline.Dciv.Count;
        Assert.True(curves > 1, "the DCIV family should carry several curves");

        // A synthetic drag: twelve terminations along an arc, each producing a frame.
        for (int i = 0; i < 12; i++)
        {
            double a = 2 * Math.PI * i / 12;
            vm.SetMarkerGamma(l1, Complex.FromPolarCoordinates(0.45, a));
            vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        }

        output.WriteLine($"after 13 frames: DcivComputeCount = {vm.DcivComputeCount}, " +
                         $"context rebuilds = {vm.ContextRebuildCount}");

        Assert.Equal(1, vm.DcivComputeCount);
        Assert.Equal(curves, vm.Frame.Loadline.Dciv.Count);

        // And §6.1's own rule holds alongside it: a VALUE change never rebuilds the context.
        Assert.Equal(1, vm.ContextRebuildCount);
    }

    // ══ the product path, end to end ═════════════════════════════════════════

    [Fact]
    public void ANewDocument_SolvesARealFrameThroughTheRealEngine()
    {
        var doc = new HarmonicaDocument("Untitled-harmonicaRF-1", new HarmonicaDocumentViewModel());
        var vm  = doc.ViewModel.Harmonica;

        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        Assert.Null(vm.SolveError);
        Assert.True(vm.LastSolveCount > 0, "no HB solve ran");

        // Tier A: a real drive-up with real figures of merit.
        Assert.True(vm.Frame.PowerSweep.PoutDbm.Length > 1);
        Assert.True(vm.Frame.PowerSweep.GainDb.Length == vm.Frame.PowerSweep.PoutDbm.Length);
        // The steps come back in SOLVE order (a doubling bracket then a secant); a plot of them
        // unsorted would zig-zag back on itself, so the panel must present them ascending.
        var pin = vm.Frame.PowerSweep.PinAvailDbm;
        for (int i = 1; i < pin.Length; i++)
            Assert.True(pin[i] >= pin[i - 1], "the power sweep must be ascending in Pin");

        // §7.3: the loadline is closed over one RF cycle.
        Assert.True(vm.Frame.Loadline.LoadlineVds.Length > 2);
        Assert.Equal(vm.Frame.Loadline.LoadlineVds[0], vm.Frame.Loadline.LoadlineVds[^1]);

        // Tier B: a real grid with real contours and real extrema.
        Assert.Equal(13, vm.Frame.SmithPower.GridPoints.Count);     // 2 rings × 6 spokes + centre
        Assert.NotEmpty(vm.Frame.SmithPower.Contours);
        Assert.NotEmpty(vm.Frame.SmithPower.Levels);
        Assert.NotNull(vm.Frame.SmithPower.Mxp);

        // §4.5 — the glyphs are READ from Gamma_intr, so they must actually be populated.
        Assert.All(vm.Frame.Markers, m =>
            Assert.False(double.IsNaN(m.GammaIntrinsic.Real),
                $"{m.Name}'s intrinsic Γ was never stamped from the Gamma_intr cube"));

        // §7.5 — the readouts are populated, and every one carries a tooltip — EXCEPT a column
        // HEADER row (R-h9c-6: "MXP 1f0 Load", a plain "Source"/"Load" column title), which is
        // label-only by design (empty Value, empty Tooltip) rather than a readout with nothing to say.
        Assert.NotEmpty(vm.Frame.Readouts);
        Assert.All(vm.Frame.Readouts, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Label));
            bool isHeader = r.Value.Length == 0 && r.Tooltip.Length == 0;
            if (!isHeader) Assert.False(string.IsNullOrWhiteSpace(r.Tooltip));
        });

        output.WriteLine($"{vm.LastSolveCount} HB solves · " +
                         $"{vm.Frame.SmithPower.GridPoints.Count(p => p.IsHole)} holes · " +
                         $"{vm.Frame.SmithPower.Contours.Count} polylines · " +
                         $"{vm.Frame.Readouts.Count} readouts");
    }

    [Fact]
    public void ASolveThatFails_LandsInSolveError_AndLeavesThePreviousFrameOnScreen()
    {
        // A live instrument that throws on a bad parameter is not a live instrument.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        var good = vm.Frame;
        Assert.Null(vm.SolveError);

        // A grid of zero rings and zero spokes is degenerate — one point, no hull, no contours.
        // Whatever it does, it must not take the document down.
        var ex = Record.Exception(() =>
            vm.SolveFrame(new HarmonicaSolver.Options { Rings = 0, Spokes = 0 }));
        Assert.Null(ex);

        // Either it succeeded (fine) or it reported — never threw, and never blanked the document.
        Assert.NotNull(vm.Frame);
        if (vm.SolveError is not null)
        {
            Assert.Same(good, vm.Frame);
            output.WriteLine($"degenerate grid reported: {vm.SolveError}");
        }
    }

    // ══ the .charm round trip, through the view model ════════════════════════

    [Fact]
    public void CharmRoundTrip_ThroughTheViewModel_RestoresMarkersLayoutAndAppearance()
    {
        var a = new HarmonicaViewModel();
        var l1 = a.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        a.SetMarkerImpedance(l1, new Complex(72.5, 18.25));
        a.Layout = new CharmLayout
        {
            Locked = false,
            Panels = [new CharmPanelPlacement(HarmonicaPanelId.SmithPower, 0.1, 0.2, 0.3, 0.4)],
        };

        string json = a.ToCharmJson();

        var b = new HarmonicaViewModel();
        var unresolved = b.LoadCharm(json, baseDirectory: null);
        Assert.Empty(unresolved);

        // The marker came back with its own value, on the right side and band.
        var bl1 = b.Markers.First(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        var z   = b.Terminations.Z(TerminationSide.Load, 1);
        Assert.Equal(72.5,  z.Real,      precision: 9);
        Assert.Equal(18.25, z.Imaginary, precision: 9);
        Assert.Equal(l1.Gamma, bl1.Gamma);

        // §4.2 — S1 and L1 are always present, whatever the file said.
        Assert.Contains(b.Markers, m => m.Side == TerminationSideKind.Source && m.Band == 1);
        Assert.Contains(b.Markers, m => m.Side == TerminationSideKind.Load   && m.Band == 1);

        Assert.False(b.Layout.Locked);
        Assert.Equal(0.3, b.Layout.PlacementOf(HarmonicaPanelId.SmithPower).W, precision: 9);

        // And the loaded document still solves — a round trip that produces an unsolvable model
        // would be a round trip that lost something.
        b.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        Assert.Null(b.SolveError);
    }

    [Fact]
    public void Document_MirrorsDirtyFromTheViewModel_AndBulletsTheTabTitle()
    {
        var doc = new HarmonicaDocument("Untitled-harmonicaRF-1", new HarmonicaDocumentViewModel());
        Assert.False(doc.IsDirty);
        Assert.Equal("Untitled-harmonicaRF-1", doc.Title);
        Assert.True(doc.IsScratch);

        var l1 = doc.ViewModel.Harmonica.Markers.First(m => m.Name == "L1");
        doc.ViewModel.Harmonica.SetMarkerImpedance(l1, new Complex(10, 0));

        Assert.True(doc.IsDirty);
        Assert.StartsWith("• ", doc.Title, StringComparison.Ordinal);

        doc.OnSavedToPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amp.charm"));
        Assert.False(doc.IsDirty);
        Assert.False(doc.IsScratch);
        Assert.Equal("amp", doc.Title);
    }
}
