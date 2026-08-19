using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Theming;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The built-in assembly rule set (2026-08-19, owner): a wirebond design that references no `.wasm`
/// is checked against circuitRF's OWN rules rather than told that nothing ran, its first rule is a
/// wire-to-wire clearance defaulting to half a mil, and every surface that reports a check names
/// which of the two rule sets it used.
///
/// <para>Also the two routing faults that made the rule unreachable in practice: the DRC panel was
/// emptied for a wBond document (the one editor whose subject IS wires), and the panel's own Check
/// button posted nothing at all to the Messages panel.</para>
///
/// <para>The view-hosted halves are pinned by SOURCE SCAN — this project's standing fallback for
/// anything living in a <c>Window</c> or a <c>UserControl</c>, which cannot be constructed
/// headlessly. Everything reachable from a view model is driven directly.</para>
/// </summary>
public class WBondDrcBuiltInRulesTests : IDisposable
{
    public WBondDrcBuiltInRulesTests() => WBondWireClearance.TestOverrideActive = true;

    public void Dispose()
    {
        WBondWireClearance.TestOverrideActive = false;
        WBondWireClearance.TestOverrideStore  = null;
        GC.SuppressFinalize(this);
    }

    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text, string? FilePath)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text, filePath));
        public void Clear() => Posted.Clear();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    // ── The clearance value ──────────────────────────────────────────────────

    [Fact]
    public void TheDefaultClearance_IsHalfAMil_SurfaceToSurface()
    {
        Assert.Equal(0.5, WBondBuiltInRules.DefaultClearanceNm / WBondUnits.NmPerUnit(WBondUnit.Mil), 9);
        Assert.Equal(0.5, WBondWireClearance.Mil, 9);            // nothing stored → the default
    }

    /// <summary>
    /// Zero is a real setting — "report only what actually collides" — and it must not degenerate
    /// into "report nothing that touches exactly", which is what a literal zero limit would do.
    /// </summary>
    [Fact]
    public void AZeroClearance_IsHonoured_ButHeldAtTheFloorSoExactContactStaysReportable()
    {
        Assert.Equal(WBondBuiltInRules.MinimumClearanceNm, WBondWireClearance.Sanitise(0));
        Assert.True(WBondWireClearance.Sanitise(0) > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableStoredClearance_ReadsAsTheDefault_RatherThanAsNoRule(double? stored)
    {
        Assert.Equal(WBondBuiltInRules.DefaultClearanceNm, WBondWireClearance.Sanitise(stored));
    }

    [Fact]
    public void TheClearance_RoundTripsInMil_AndIsOmittedFromPreferencesWhenUnset()
    {
        WBondWireClearance.Mil = 1.25;
        Assert.Equal(1.25, WBondWireClearance.Mil, 9);
        Assert.Equal(WBondUnits.ToNm(1.25, WBondUnit.Mil), WBondWireClearance.Nm, 3);

        Assert.DoesNotContain("wire_clearance_mil", JsonSerializer.Serialize(new AppPreferences()));
        Assert.Contains("wire_clearance_mil",
                        JsonSerializer.Serialize(new AppPreferences { WireClearanceMil = 1.25 }));
    }

    /// <summary>The Settings box edits the same value the check reads — not a second copy of it.</summary>
    [Fact]
    public void TheSettingsBox_WritesThroughTheSameAccessorTheCheckReads()
    {
        string view = Read("src/Ui/Views/Dialogs/SettingsView.axaml.cs");
        Assert.Contains("WireClearanceUpDown.Value = (decimal)WBondWireClearance.Mil;", view);
        Assert.Contains("WBondWireClearance.Mil = (double)mil;", view);
        Assert.Contains("Name=\"WireClearanceUpDown\"", Read("src/Ui/Views/Dialogs/SettingsView.axaml"));
    }

    // ── What the panel says about which rules ran ────────────────────────────

    [Fact]
    public void TheDrcPanel_NamesTheBuiltInSetAndItsClearance_WhenNoWasmIsReferenced()
    {
        var vm = new LayoutEditorViewModel(new LayoutView()) { WireDesign = new WBondDesign() };

        string text = vm.DrcAssemblyText;

        Assert.Contains(WBondBuiltInRules.SetName, text);
        Assert.Contains("0.5 mil", text);

        // …and it says what it does NOT cover, which is the whole reason the line exists: a clean
        // result against one geometry rule must not read like a clean result against a house's forty.
        Assert.Contains("no .wasm referenced", text);
    }

    [Fact]
    public void ALayoutWithNoWires_SaysNothingAboutAssemblyRulesAtAll()
    {
        var vm = new LayoutEditorViewModel(new LayoutView());
        Assert.Equal("", vm.DrcAssemblyText);
    }

    // ── The Messages report ──────────────────────────────────────────────────

    [Fact]
    public void ARunReport_PostsItsDiagnosticsFirst_AndItsVerdictLast()
    {
        var sink = new FakeMessageSink();

        DrcRunReport.Post(sink, new DrcRunResult([], 3, 1, 7, "Acme", ["something to say"]));

        Assert.Equal(2, sink.Posted.Count);
        Assert.Equal(MessageLevel.Warning, sink.Posted[0].Level);
        Assert.Contains("something to say", sink.Posted[0].Text);

        // The verdict is the ANSWER, so it goes last — above its own footnotes it reads as belonging
        // to the run before it.
        Assert.Equal(MessageLevel.Success, sink.Posted[1].Level);
        Assert.Contains("no violations", sink.Posted[1].Text);
        Assert.Contains("\"Acme\"", sink.Posted[1].Text);
        Assert.Contains("3 rule(s)", sink.Posted[1].Text);
    }

    [Fact]
    public void ARunReport_WithViolations_IsAWarningAndPointsAtThePanel()
    {
        var sink = new FakeMessageSink();
        var v = new DrcViolation("R", DrcRuleKind.MinSpacing, DrcSeverity.Error, null, 0, [], Bbox.Empty,
                                 null, null, "k");

        DrcRunReport.Post(sink, new DrcRunResult([v], 1, 1, 1, null, []));

        var only = Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Warning, only.Level);
        Assert.Contains("see the DRC panel", only.Text);
    }

    [Fact]
    public void ARunReport_WithNoSink_IsANoOp_SoATornOffWindowIsNotAnError()
    {
        DrcRunReport.Post(null, DrcRunResult.Empty());
    }

    /// <summary>
    /// Three entry points, one sentence. The panel's own Check button used to post NOTHING — it ran
    /// the check, filled the list, and left the Messages panel silent about which rule set had
    /// answered.
    /// </summary>
    [Fact]
    public void EveryDrcEntryPoint_ReportsThroughTheOneReporter()
    {
        Assert.Contains("DrcRunReport.Post(Messages, result)",
                        Read("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        Assert.Contains("DrcRunReport.Post(vm.MessageSink, result)",
                        Read("src/Ui/Views/Layout/LayoutEditorView.axaml.cs"));
        Assert.Contains("DrcRunReport.Post(workspace?.Messages ?? vm.MessageSink, result)",
                        Read("src/Ui/Views/Drc/DrcToolView.axaml.cs"));
    }

    // ── Reachability: a wBond document can be checked ────────────────────────

    /// <summary>
    /// The routing fault, at its source. A wBond document's wires live on its REFERENCE LAYOUT, and
    /// the panel was being pointed at null for exactly that document type — so the one editor whose
    /// entire subject is bond wires was the one place they could not be checked.
    /// </summary>
    [Fact]
    public void TheDrcPanel_FollowsAWBondDocumentsReferenceLayout_RatherThanBeingEmptied()
    {
        string ws = Read("src/Ui/ViewModels/WorkspaceViewModel.cs");

        Assert.Contains("_factory.DrcTool?.SetActiveLayout(doc.ViewModel.ReferenceLayout);", ws);

        // …and Design ▸ Check Design Rules resolves the same layout, so the menu item, the shortcut
        // and the toolbar button are live in a wBond document too.
        Assert.Contains("WBondDocument  wdoc => wdoc.ViewModel.ReferenceLayout,", ws);
        Assert.Contains("CanExecute = nameof(IsCheckableDocumentActive)", ws);
    }

    /// <summary>
    /// A reference layout is what the check runs THROUGH, and a `.wBond` opened from disk with no
    /// embedded geometry had none — so the fix above would have worked for a new document and
    /// silently not for an opened one.
    /// </summary>
    [Fact]
    public void EveryWBondDocument_GetsAReferenceLayout_NotOnlyANewlyCreatedOne()
    {
        string ws = Read("src/Ui/ViewModels/WorkspaceViewModel.cs");

        int at = ws.IndexOf("private void TrackNewWBond(WBondDocument doc)", StringComparison.Ordinal);
        Assert.True(at > 0);

        string body = ws[at..(at + 1200)];
        Assert.Contains("EnsureReferenceLayout", body);

        // One place, so no entry point can be added without it: the creation path's own call is gone.
        Assert.Equal(1, ws.Split("doc.ViewModel.EnsureReferenceLayout(").Length - 1);
    }

    // ── The Touchstone export's last line ────────────────────────────────────

    /// <summary>
    /// A write's final Messages line has to carry the PATH — the panel renders one as a
    /// reveal-in-file-manager link, and that link is the point of the line. The layout-hosted entry
    /// point then re-posted the same outcome WITHOUT a path, so the last thing in the panel after an
    /// export was a linkless duplicate and the line with the link sat above it (owner, 2026-08-19).
    /// </summary>
    [Fact]
    public void TheTouchstoneExport_PostsTheWrittenFileLast_WithItsPath()
    {
        string cmds = Read("src/Ui/WBond/WBondPublishCommands.cs");

        Assert.Contains("messages?.Success(written, result.WrittenPaths[0]);", cmds);
        Assert.Contains("return new Outcome(written, false, Posted: messages is not null);", cmds);

        // The line names the file, so the panel says WHAT was written and not only where.
        Assert.Contains("$\"Exported {Path.GetFileName(result.WrittenPaths[0])}", cmds);

        // The old terse line is gone rather than merely joined by a better one.
        Assert.DoesNotContain("Success(\"Wrote s-parameters\"", cmds);
    }

    [Fact]
    public void TheLayoutHost_DoesNotRepostAnOutcomeTheCommandAlreadyPosted()
    {
        Assert.Contains("if (outcome.Posted) return;",
                        Read("src/Ui/Views/Layout/LayoutEditorView.axaml.cs"));
    }

    // ── End to end, through the editor a user actually presses Check in ──────

    /// <summary>
    /// The whole point, driven through the real path: a design with no `.wasm`, no technology and no
    /// artwork — which is exactly what a wBond document's reference layout is — still produces an
    /// ERROR row in the panel for two wires that overlap.
    /// </summary>
    [Fact]
    public void TwoOverlappingWires_AreAnErrorInThePanel_WithNoWasmAndNoTechnologyAtAll()
    {
        var vm = new LayoutEditorViewModel(new LayoutView()) { WireDesign = OverlappingPair() };

        var result = vm.RunDrc();

        var row = Assert.Single(vm.DrcViolations);
        Assert.Equal(WBondBuiltInRules.WireClearanceRuleName, row.Violation.RuleName);
        Assert.Equal(DrcSeverity.Error, row.Violation.Severity);
        Assert.Equal(1, result.ErrorCount);
        Assert.False(result.IsClean);

        // The rule is counted, so the summary cannot read as "checked against nothing".
        Assert.Equal(1, result.RulesEvaluated);
        Assert.Contains(WBondBuiltInRules.SetName, string.Join(" ", result.Diagnostics));
    }

    [Fact]
    public void AWireViolation_Waives_ThroughTheSameStoreADieSideOneDoes()
    {
        var vm = new LayoutEditorViewModel(new LayoutView()) { WireDesign = OverlappingPair() };
        vm.RunDrc();

        var row = Assert.Single(vm.DrcViolations);
        vm.SetWaived(row, true, "accepted by the house");

        Assert.Equal(0, vm.DrcResult!.ErrorCount);
        Assert.Equal(1, vm.DrcResult!.WaivedCount);
        Assert.True(vm.DrcResult!.IsClean);
    }

    [Fact]
    public void RaisingTheClearance_TurnsAPassingDesignIntoAFailingOne()
    {
        // 2 mil centres, 1 mil wires: 1 mil of metal-to-metal gap. Clean at the default half a mil.
        var vm = new LayoutEditorViewModel(new LayoutView()) { WireDesign = ParallelPair(2) };

        Assert.True(vm.RunDrc().IsClean);

        WBondWireClearance.Mil = 2.0;
        Assert.False(vm.RunDrc().IsClean);
    }

    private static WBondDesign ParallelPair(double centreSeparationMil)
    {
        long sep = WBondUnits.ToNm(centreSeparationMil, WBondUnit.Mil);
        long d   = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        long len = WBondUnits.ToNm(100.0, WBondUnit.Mil);

        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires =
            {
                new Wire { DiameterNm = d, Material = "Gold",
                           Points = { new Point3(0, 0, d), new Point3(len, 0, d) } },
                new Wire { DiameterNm = d, Material = "Gold",
                           Points = { new Point3(0, sep, d), new Point3(len, sep, d) } },
            },
        });
        return design;
    }

    /// <summary>Centres half a diameter apart — the metal genuinely interpenetrates.</summary>
    private static WBondDesign OverlappingPair() => ParallelPair(0.5);
}
