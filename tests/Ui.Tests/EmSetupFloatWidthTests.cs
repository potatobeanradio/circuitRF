using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request, 2026-08-25: "when .cem files are undocked, make their window size a little
/// narrower — reducing it down to 1600 pixels wide. This is better for .cem because its content
/// layout is shaped that way."
///
/// <para><c>CrfHostWindow</c> is an Avalonia <c>Window</c> subclass and cannot be constructed
/// headlessly in this suite (the same limitation <see cref="TearOffBackgroundTests"/> records), so
/// the CONSTANT is asserted directly and the code that applies it is pinned by source scan —
/// comments stripped, since the request itself is quoted in one.</para>
/// </summary>
public class EmSetupFloatWidthTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string HostSource() => Strip(
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "Dock", "CrfHostWindow.cs")));

    private static string Strip(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    /// <summary>
    /// <b>700 logical units.</b> Asserted as a literal rather than derived, because the number is a
    /// judgement about this panel's content and a silent drift in it is exactly what a reader of a
    /// float window would not notice.
    /// </summary>
    [Fact]
    public void TheCapIsSevenHundredLogicalUnits()
        => Assert.Equal(700, CrfHostWindow.EmSetupFloatMaxWidth);

    /// <summary>
    /// And it still leaves the stackup table's two STRETCHING columns something to stretch into.
    /// That row is <c>76,*,90,190,*</c> — about 380 units of fixed columns — and it is the widest
    /// thing this panel lays out, so it is what any future change to the cap has to be measured
    /// against. The assertion is a floor, not the chosen value: the owner picks the width, this
    /// stops a later edit from picking one where the table has no room left at all.
    /// </summary>
    [Fact]
    public void TheCapLeavesTheStackupTablesStretchingColumnsUsable()
    {
        string xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Layout", "EmSetupEditorView.axaml"));

        var m = Regex.Match(xaml, @"ColumnDefinitions=""76,\*,90,190,\*""\s+ColumnSpacing=""(\d+)""");
        Assert.True(m.Success, "the stackup row's column definition changed — re-measure the cap");

        double fixedColumns = 76 + 90 + 190 + (4 * double.Parse(m.Groups[1].Value));

        // The panel's own left+right margin (10 each, EmSetupEditorView's body StackPanel) plus the
        // group Border's padding — call it 40 units of chrome the table never gets.
        double perStretchingColumn = (CrfHostWindow.EmSetupFloatMaxWidth - 40 - fixedColumns) / 2;

        Assert.True(perStretchingColumn >= 100,
            $"the cap ({CrfHostWindow.EmSetupFloatMaxWidth}) leaves only {perStretchingColumn:0} units "
            + $"per stretching column beside {fixedColumns} units of fixed ones");
    }

    /// <summary>
    /// <b>Applied in <c>OnOpened</c>, not at window construction.</b> A width set while the window is
    /// being built is not final — <c>DockWindowOptions.ApplyTo</c> assigns geometry unconditionally,
    /// which is the same overwrite the <c>OwnerMode</c> override in <c>CircuitRfDockFactory</c>
    /// already documents having to work around. Moving this into the factory would put the cap back
    /// upstream of that assignment, where a drag tear-off's own bounds would silently win.
    /// </summary>
    [Fact]
    public void TheCapIsAppliedAfterTheWindowIsOpen_WhereDockCanNoLongerOverwriteIt()
    {
        string src = HostSource();

        Assert.Contains("protected override void OnOpened(", src);

        var body = src[src.IndexOf("protected override void OnOpened(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        Assert.Contains("base.OnOpened(e);", body);
        Assert.Contains("EmSetupFloatMaxWidth", body);
        Assert.Contains("FloatsAnyEmSetup()", body);
    }

    [Fact]
    public void TheHeightBonusIsTwoHundred()
        => Assert.Equal(200, CrfHostWindow.EmSetupFloatExtraHeight);

    /// <summary>
    /// <b>The height bonus only applies when the width cap fires, and that is what keeps it from
    /// compounding.</b> A floating window's geometry is captured into the <c>.cws</c>, so an
    /// unconditional <c>Height += 200</c> would restore 200 taller each launch and add 200 again —
    /// growing without bound. Gating both adjustments behind the one early return makes a restored
    /// float (already within the cap) untouched, and a fresh tear-off adjusted exactly once.
    /// </summary>
    [Fact]
    public void TheHeightBonusIsGatedOnTheWidthCapFiring_SoItCannotCompoundAcrossLaunches()
    {
        string src = HostSource();
        var body = src[src.IndexOf("protected override void OnOpened(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        // One early return covering both, rather than two independent conditions.
        Assert.Matches(new Regex(@"if\s*\(\s*Width\s*<=\s*EmSetupFloatMaxWidth\s*\|\|\s*!FloatsAnyEmSetup\(\)\s*\)\s*return;"), body);
        Assert.Contains("EmSetupFloatExtraHeight", body);

        // And nothing outside that guard touches Height.
        int guardEnd = body.IndexOf("return;", StringComparison.Ordinal);
        Assert.DoesNotContain("Height", body[..guardEnd]);
    }

    /// <summary>The bonus is clamped to the screen, so a window already near the bottom of the
    /// display does not gain 200 units of off-screen height.</summary>
    [Fact]
    public void TheHeightBonusIsClampedToTheScreen()
    {
        string src = HostSource();
        Assert.Contains("Math.Min(Height + EmSetupFloatExtraHeight, AvailableHeight())", src);

        // And the logical/device conversion is done, not assumed — WorkingArea is device pixels
        // while Height is logical, which is invisible on an unscaled display.
        var avail = src[src.IndexOf("private double AvailableHeight()", StringComparison.Ordinal)..];
        avail = avail[..avail.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("screen.Scaling", avail);
        Assert.Contains("/ scaling", avail);
    }

    /// <summary>
    /// <b>A CAP, not an assignment.</b> A float that is already narrower than 1600 — a small screen,
    /// a window the user shrank — must be left where it is; only an over-wide one is brought down.
    /// The guard is the whole difference between "narrower when it needs to be" and "always exactly
    /// 1600", and it is one easily-dropped comparison.
    /// </summary>
    [Fact]
    public void ANarrowerFloatIsLeftAlone()
    {
        string src = HostSource();
        var body = src[src.IndexOf("protected override void OnOpened(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        // The comparison must be against the cap, not an unconditional assignment to it.
        Assert.Matches(new Regex(@"Width\s*<=\s*EmSetupFloatMaxWidth\s*\|\|"), body);
        Assert.DoesNotMatch(new Regex(@"^\s*Width\s*=\s*EmSetupFloatMaxWidth\s*;", RegexOptions.Multiline),
                            body[..body.IndexOf("return;", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// It targets the EM setup document specifically, and walks the floated dock tree to find it —
    /// a torn-off window hosts a root dock containing a document dock, never the document directly,
    /// so an `is EmSetupDocument` test on the layout alone would never match.
    /// </summary>
    [Fact]
    public void ItRecognisesAnEmSetupAnywhereInTheFloatedTree()
    {
        string src = HostSource();
        Assert.Contains("Layout.Em.EmSetupDocument", src);
        Assert.Contains("VisibleDockables", src[src.IndexOf("ContainsEmSetup", StringComparison.Ordinal)..]);
    }

    /// <summary>Other documents are untouched — a torn-off schematic, layout or data display still
    /// inherits the shell's width, which is what suits a canvas.</summary>
    [Fact]
    public void OnlyTheEmSetupIsCapped()
    {
        string src = HostSource();
        var body = src[src.IndexOf("private static bool ContainsEmSetup", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        foreach (string other in new[] { "SchematicDocument", "LayoutDocument", "DataDisplayDocument", "SymbolDocument" })
            Assert.DoesNotContain(other, body);
    }
}
