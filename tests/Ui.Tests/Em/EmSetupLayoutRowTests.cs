// ================================================================
//  EmSetupLayoutRowTests.cs — owner request, 2026-09-15, two changes to the EM Setup panel
//
//    * the Layout row (which .clay this setup is FOR) becomes SELECTABLE text, with a context menu
//      offering "Open…" and "Open in New Window…";
//    * every warning-coloured string in the panel becomes selectable — the unknown-count refusal
//      above all, because it is a paragraph of measured numbers that belongs in a bug report.
//
//  The panel resolves the layout; it does not open one. Which window a document lands in is the
//  shell's question and has exactly one answer already (OpenOrActivateLayoutAsync for "show it",
//  OpenDocumentInOwnWindow for the drag tear-off path), so the view model's whole contribution is a
//  resolved absolute path and a flag — which is what these tests assert.
//
//  The view half is a SOURCE SCAN, the fallback this suite already uses for view-level wiring claims
//  (EmSetupWindowFixesTests' own note): this view cannot be constructed without an Avalonia app host.
// ================================================================

using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmSetupLayoutRowTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-emrow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static LayoutView OneLine()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = new(1, 0),
            Xy    = [0, 0, 10_000_000, 0, 10_000_000, 400_000, 0, 400_000],
        });
        return view;
    }

    /// <summary>An editor whose layout resolves to <paramref name="clay"/>, or to nothing when it is
    /// null — the two states the context menu is gated between.</summary>
    private static EmSetupEditorViewModel Editor(string dir, string? clay)
    {
        string path  = Path.Combine(dir, "panel.cem");
        var    setup = new EmSetup
        {
            Name = "panel", LayoutRef = "cell/layout/a.clay", AnalysisKind = EmAnalysisKind.Planar,
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => clay is null
                ? null
                : new EmLayoutSource(clay, OneLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The view model
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The path handed over is the one the resolution produced</b>, not one re-derived here from
    /// <c>LayoutRef</c>. The reference rule is a walk-up with an absolute-path case, so a second
    /// reading of it could open a different file from the one being analysed — which is the whole
    /// class of bug the reference/resolution pair was written to prevent.
    /// </summary>
    [Fact]
    public void OpenLayout_HandsOverTheRESOLVEDAbsolutePath()
    {
        string dir  = TempDir();
        string clay = Path.Combine(dir, "somewhere", "else", "a.clay");
        var    vm   = Editor(dir, clay);

        (string Path, bool NewWindow)? asked = null;
        vm.OpenLayoutRequested = (p, w) => asked = (p, w);

        Assert.Equal(clay, vm.ResolvedLayoutPath);
        Assert.True(vm.CanOpenLayout);

        vm.OpenLayoutCommand.Execute(null);
        Assert.Equal((clay, false), asked);

        vm.OpenLayoutInNewWindowCommand.Execute(null);
        Assert.Equal((clay, true), asked);
    }

    /// <summary>A reference that resolves to nothing gates both items off — an "Open" that can only
    /// fail is an item that should not be enabled.</summary>
    [Fact]
    public void AReferenceThatDoesNotResolve_LeavesNothingToOpen()
    {
        var vm = Editor(TempDir(), clay: null);

        Assert.Null(vm.ResolvedLayoutPath);
        Assert.False(vm.CanOpenLayout);

        bool asked = false;
        vm.OpenLayoutRequested = (_, _) => asked = true;
        vm.OpenLayoutCommand.Execute(null);
        vm.OpenLayoutInNewWindowCommand.Execute(null);
        Assert.False(asked);
    }

    /// <summary>
    /// <b>Change Layout re-points the menu.</b> The resolved path is state, so it has to be cleared
    /// and re-taken on every refresh — a menu still naming the previous artwork would open the wrong
    /// document with no way to tell.
    /// </summary>
    [Fact]
    public void ChangingWhatTheReferenceResolvesTo_MovesTheMenuWithIt()
    {
        string dir    = TempDir();
        string first  = Path.Combine(dir, "a.clay");
        string second = Path.Combine(dir, "b.clay");

        string current = first;
        string path    = Path.Combine(dir, "panel.cem");
        var    setup   = new EmSetup { Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(current, OneLine(), StarterTechnologies.Pcb2Layer(), Dbu),
        };

        vm.Refresh();
        Assert.Equal(first, vm.ResolvedLayoutPath);

        current = second;
        vm.Refresh();
        Assert.Equal(second, vm.ResolvedLayoutPath);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The view
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string Xaml() => File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));

    /// <summary>The Layout row is selectable and carries both items, each bound to its command and
    /// each gated on the reference having resolved.</summary>
    [Fact]
    public void TheLayoutRow_IsSelectable_AndOffersBothWaysIn()
    {
        string xaml = Xaml();

        int start = xaml.IndexOf("Name=\"LayoutRefText\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the EM Setup panel's Layout row is not named — it cannot be gated");

        // The element it names must be the selectable kind, not a TextBlock with a name on it.
        int tag = xaml.LastIndexOf('<', start);
        Assert.StartsWith("<SelectableTextBlock", xaml[tag..], StringComparison.Ordinal);

        int end = xaml.IndexOf("</SelectableTextBlock>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string row = xaml[start..end];

        Assert.Contains("<ContextMenu>", row, StringComparison.Ordinal);
        foreach ((string header, string command) in ((string, string)[])
                 [("Open…", "OpenLayoutCommand"), ("Open in New Window…", "OpenLayoutInNewWindowCommand")])
        {
            Assert.Contains($"Header=\"{header}\"", row, StringComparison.Ordinal);
            Assert.Contains($"{{Binding ViewModel.{command}}}", row, StringComparison.Ordinal);
        }

        // Both gated, so neither can be picked when there is nothing to open.
        Assert.Equal(2, Regex.Matches(row, @"IsEnabled=""\{Binding ViewModel\.CanOpenLayout\}""").Count);
    }

    /// <summary>
    /// <b>Every style class the panel uses is declared for BOTH text element types.</b> A Selector
    /// names a TYPE, so <c>TextBlock.colhdr</c> does not match a <c>SelectableTextBlock</c> — and the
    /// layout reference kept its <c>Classes="colhdr"</c> while silently rendering at the control
    /// default: 12 px at full opacity, beside 10 px at 0.6 on every label around it. Caught by the
    /// documentation figure, which is a rendering of the real panel; this is the cheap gate that
    /// stops it needing to be caught that way again.
    /// </summary>
    [Fact]
    public void EveryClassUsedByASelectableTextBlock_HasAStyleThatMatchesIt()
    {
        string xaml = Xaml();

        var used = Regex.Matches(xaml, "<SelectableTextBlock\\b[^>]*?Classes=\"([^\"]+)\"", RegexOptions.Singleline)
                        .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Distinct()
                        .ToList();

        Assert.NotEmpty(used);

        foreach (string cls in used)
            Assert.True(
                xaml.Contains($"Selector=\"SelectableTextBlock.{cls}\"", StringComparison.Ordinal),
                $"a SelectableTextBlock carries Classes=\"{cls}\" but the panel declares only " +
                $"TextBlock.{cls} — the class is applied and does nothing");
    }

    /// <summary>
    /// <b>Every warning-coloured string in the panel is selectable.</b> Asked of the FOREGROUND rather
    /// than of a list of known names, so a warning added later is covered by the same gate instead of
    /// quietly falling outside it — which is the only way a rule phrased as "all of those" can hold.
    /// </summary>
    [Fact]
    public void EveryWarningColouredString_IsSelectable()
    {
        var plain = Regex.Matches(Xaml(), @"<TextBlock\b[^>]*?CrfWarningBrush", RegexOptions.Singleline)
                         .Select(m => m.Value.Replace('\n', ' ').Trim())
                         .ToList();

        Assert.True(plain.Count == 0,
            "these warning strings are plain TextBlocks and cannot be copied:\n  " + string.Join("\n  ", plain));

        // …and there is still warning-coloured text to be selectable, so the assertion above is not
        // passing because the colour was renamed out from under it.
        Assert.Contains("CrfWarningBrush", Xaml(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The unknown-count refusal specifically — the string the request was about. It reaches the panel
    /// as <c>BlockingReason</c>, and its own element has to be the selectable kind.
    /// </summary>
    [Fact]
    public void TheBlockingReason_TheUnknownCountRefusalsOwnElement_IsSelectable()
    {
        string xaml = Xaml();

        // Text=, not ToolTip.Tip= — the Simulate button carries the same string as its tooltip, and a
        // tooltip is not something anyone can select.
        int at = xaml.IndexOf("Text=\"{Binding ViewModel.BlockingReason}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "nothing in the EM Setup panel DISPLAYS the blocking reason");
        int tag = xaml.LastIndexOf('<', at);
        Assert.StartsWith("<SelectableTextBlock", xaml[tag..], StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal that lands there IS the mesher's own, verbatim — which is what makes copying it
    /// worth anything. A budget refusal names the unknown count, the ceiling and the settings that
    /// move them; this is the seam that carries it into the panel, and the reason the element showing
    /// it had to become selectable.
    /// </summary>
    [Fact]
    public void ExceedingTheUnknownCeiling_PutsTheMeshersOwnSentenceInTheBlockingReason()
    {
        string dir  = TempDir();
        string path = Path.Combine(dir, "panel.cem");
        var    setup = new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            Frequency = new CircuitRF.Core.Design.FrequencySpec(
                "1", "40", numPoints: 2, startUnit: "GHz", stopUnit: "GHz"),
            // 20 cells across a 3 mm conductor is a 150 um pitch over a 60 mm board: the tensor
            // product pays for it everywhere, which is the shape that crosses the 5,000 ceiling.
            PlanarMesh = new PlanarMeshSettings(MinCellsAcrossConductor: 20),
        };
        EmSetupPersistence.SaveToFile(path, setup);

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 60_000_000, Y2 = 3_000_000 });
        view.Shapes.Add(new LabelShape
        { Layer = new(1, 0), X = 0, Y = 1_500_000, Text = "P1", Height = 1_000_000, IsPort = true });
        view.Shapes.Add(new LabelShape
        { Layer = new(1, 0), X = 60_000_000, Y = 1_500_000, Text = "P2", Height = 1_000_000, IsPort = true });

        // CAP-1: a run handed a memory limit (the Settings limit, else physical memory) is never REFUSED by the mesher —
        // past the limit is a warning and a Simulate Anyway question. The refusal this test is about is the one the
        // fallback ceiling (SurfaceMesher.UnknownCeiling) still makes when no memory figure can be read, so that is the
        // machine the panel is given here rather than whichever computer runs the test.
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
            MachineMemoryLimitBytes = () => null,
            PhysicalMemoryBytes     = () => 0,
        };
        vm.Refresh();
        vm.BuildPlanarMesh();

        Assert.True(vm.PlanarBudgetRefusal is not null,
                    $"no refusal: {vm.PlanarMeshReport?.UnknownCount} unknowns against a ceiling of {vm.PlanarMeshReport?.CeilingUnknowns}");
        // Reported VERBATIM, which is the property that makes a copied message worth reading.
        Assert.Equal(vm.PlanarBudgetRefusal, vm.BlockingReason);
        Assert.Contains("unknown", vm.BlockingReason!, StringComparison.OrdinalIgnoreCase);
    }
}
