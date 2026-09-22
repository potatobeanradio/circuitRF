using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Palette;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Library palette opens two component glyphs wide, and goes on showing whatever number of glyph
/// columns it is showing when the workspace window is resized (owner, 2026-09-13).
///
/// <para><b>What can be asserted here is the arithmetic and the numbers it is fed.</b> There is no
/// Avalonia platform in this project, so the pin itself — <c>PaletteColumnPin</c>, which reads the
/// live panel and writes the proportions back — cannot be laid out. It was verified against a real
/// dock tree in a headless host at five window widths (900 to 2000 px), for palettes dragged to two,
/// three and four glyph columns: each holds its own count at every width, where unpinned a two-glyph
/// palette reflowed to three columns at 1600 px and to one at 900. A splitter drag is NOT snapped
/// back while the window is still. Both are in this file's sibling notes in
/// <c>src/Ui/RESOLVED.md</c>.</para>
/// </summary>
public sealed class PaletteColumnWidthTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Src(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    // ── The two numbers the arithmetic is built on ────────────────────────────

    /// <summary>
    /// <see cref="PaletteColumnWidth.GlyphSlotWidth"/> IS the tile's own slot in the XAML — a tile
    /// resized without this constant following it would leave the palette sized for a glyph column
    /// that is not there, and nothing about the number itself would say so.
    /// </summary>
    [Fact]
    public void GlyphSlotWidth_IsTheTilesDeclaredWidthPlusItsMargins()
    {
        string xaml = Src("src/Ui/Controls/PaletteTile.axaml");

        var tile = Regex.Match(xaml, @"<StackPanel\b[^>]*?Width=""(?<w>[\d.]+)""[^>]*?Margin=""(?<m>[^""]+)""", RegexOptions.Singleline);
        Assert.True(tile.Success, "PaletteTile.axaml no longer declares the tile StackPanel's Width and Margin.");

        double width = double.Parse(tile.Groups["w"].Value);
        var margin = tile.Groups["m"].Value
                         .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                         .Select(double.Parse).ToArray();
        Assert.Equal(4, margin.Length);

        Assert.Equal(PaletteColumnWidth.GlyphSlotWidth, width + margin[0] + margin[2]);
    }

    /// <summary>
    /// The opening window width the default proportion is derived against is the one
    /// <c>WorkspaceWindow.axaml</c> actually opens at.
    /// </summary>
    [Fact]
    public void OpeningWindowWidth_IsTheWindowsDeclaredWidth()
    {
        string xaml = Src("src/Ui/Views/WorkspaceWindow.axaml");
        var declared = Regex.Match(xaml, @"^\s*Width=""(?<w>[\d.]+)""", RegexOptions.Multiline);
        Assert.True(declared.Success, "WorkspaceWindow.axaml no longer declares an opening Width.");
        Assert.Equal(DockLayoutDefaults.OpeningWindowWidth, double.Parse(declared.Groups["w"].Value));
    }

    // ── The shipped default ───────────────────────────────────────────────────

    /// <summary>
    /// A new workspace opens with the palette at exactly two glyph columns — the point of the whole
    /// exercise, and the half of it that is visible before anyone touches the window.
    ///
    /// <para>Dock arranges a child at <c>floor(pool x proportion)</c> with a shared fractional-pixel
    /// carry that can add one, so the assertion is "two whole glyph columns and less than one more",
    /// not an exact pixel count. The carry is why <see cref="PaletteColumnWidth.ProportionFor"/> aims
    /// half a pixel high: landing one pixel SHORT would cost a whole glyph column.</para>
    /// </summary>
    [Fact]
    public void TheShippedDefault_OpensAtExactlyTwoGlyphColumns()
    {
        double pool = DockLayoutDefaults.OpeningWindowWidth - DockLayoutDefaults.OuterSplitterTotal;

        double floored = Math.Floor(pool * DockLayoutDefaults.LibraryColumnProportion);
        foreach (double arranged in new[] { floored, floored + 1 })
        {
            double tiles = arranged - DockLayoutDefaults.PaletteColumnChrome;
            Assert.Equal(PaletteColumnWidth.DefaultGlyphColumns,
                         (int)Math.Floor(tiles / PaletteColumnWidth.GlyphSlotWidth));
        }
    }

    // ── Reset Layout ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reset Layout puts the Library back to the DEFAULT glyph count, not back to the default
    /// FRACTION (owner, 2026-09-13).
    ///
    /// <para>They are the same thing only at the window's opening size. The layout carries a share
    /// of the window; applied to a window that has since been widened it lands on three glyph
    /// columns, so "reset to the shipped arrangement" would produce a palette the shipped
    /// arrangement does not have. The count therefore travels separately, as a latch on the panel
    /// that <c>PaletteColumnPin</c> consumes when the panel first measures itself.</para>
    ///
    /// <para>Asserted on the latch rather than on a laid-out dock, which this project has no
    /// platform for. The conversion from count to fraction is <see cref="TryPin"/>'s, gated above;
    /// the wiring between them was checked in a headless host at 900-2000 px.</para>
    /// </summary>
    [Fact]
    public void ResetLayout_AsksThePaletteForTheDefaultGlyphCount()
    {
        var vm = new WorkspaceViewModel();
        var palette = ((CircuitRfDockFactory)vm.DockFactory).PaletteTool;
        Assert.NotNull(palette);

        // Nothing pending on a shell nobody has reset.
        Assert.Equal(0, palette!.ConsumeGlyphColumnsRequest());

        vm.ResetLayoutCommand.Execute(null);

        // The factory may hand back a fresh tool for the rebuilt tree — it is whichever one the
        // rebuilt layout holds that has to carry the request.
        Assert.Equal(PaletteColumnWidth.DefaultGlyphColumns,
                     ((CircuitRfDockFactory)vm.DockFactory).PaletteTool!.ConsumeGlyphColumnsRequest());
    }

    /// <summary>
    /// Field report, 2026-09-22: with the window maximised, New Workspace opened the Library at three
    /// columns. Every clean-slate rebuild that no SAVED arrangement follows is the shipped arrangement,
    /// so it asks for the shipped count exactly as Reset Layout does — New Workspace, closing to the
    /// blank shell, and opening a workspace whose <c>.cws</c> has no usable layout. Opening one that
    /// HAS a layout must not ask: the request would override the width its user chose.
    /// </summary>
    [Fact]
    public void EveryDefaultArrangementNoSavedLayoutFollows_AsksForTheDefaultGlyphCount()
    {
        string ws = Regex.Replace(Regex.Replace(Src("src/Ui/ViewModels/WorkspaceViewModel.cs"),
            @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        var rebuilds = Regex.Matches(ws,
            @"var newLayout = _factory\.CreateDefaultLayout\([^;]*;\s*(?<req>_factory\.PaletteTool\?\.RequestDefaultWidth\(\);)?");
        // New Workspace, switching workspace (which a saved layout may follow), and the blank shell.
        Assert.Equal(3, rebuilds.Count);
        Assert.Equal(2, rebuilds.Count(m => m.Groups["req"].Success));

        string docking = Src("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        Assert.Matches(@"if \(read\.Layout is not \{ \} layout\)\s*\{\s*_factory\.PaletteTool\?\.RequestDefaultWidth\(\);\s*return;", docking);
    }

    /// <summary>
    /// The latch is one-shot. Once honoured the palette is the user's again: the next drag or window
    /// resize keeps whatever they set, which is the whole point of the pin reading the count off the
    /// panel the rest of the time.
    /// </summary>
    [Fact]
    public void TheDefaultWidthRequestIsConsumedOnce()
    {
        var tool = new PaletteTool();

        Assert.Equal(0, tool.ConsumeGlyphColumnsRequest());
        tool.RequestDefaultWidth();
        Assert.Equal(PaletteColumnWidth.DefaultGlyphColumns, tool.ConsumeGlyphColumnsRequest());
        Assert.Equal(0, tool.ConsumeGlyphColumnsRequest());
    }

    /// <summary>
    /// Field report, 2026-09-22: a workspace closed with a two-column Library reopened at one. The
    /// saved arrangement carried only the column's FRACTION of a window whose size is not saved, so
    /// the count was re-read off pixels on open — one column in a narrower window, or a pixel short
    /// of two slots. The count itself is now saved, and opening asks the Library for it.
    /// </summary>
    [Fact]
    public void TheLibraryGlyphCount_IsSaved_AndRequestedOnOpen()
    {
        var vm   = new WorkspaceViewModel();
        var tool = ((CircuitRfDockFactory)vm.DockFactory).PaletteTool!;
        tool.ConsumeGlyphColumnsRequest();

        // Saved: the count the pin is holding, through the .cws JSON.
        tool.HeldGlyphColumns = 3;
        var saved = DockLayoutSerialization.TryRead(DockLayoutSerialization.Write(vm.CaptureDockLayout()!)).Layout!;
        Assert.Equal(3, saved.LibraryGlyphColumns);

        // Restored: the reopen asks for that count, whatever the fraction comes to in this window.
        // Three, not the default two, so a restore that fell back to the default cannot pass.
        tool.HeldGlyphColumns = 1;
        typeof(WorkspaceViewModel)
            .GetMethod("ApplyRestoredDockShell", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, [new DockLayoutSerialization.ReadResult(saved, null), (Func<string, bool>)(_ => false)]);
        Assert.Equal(3, ((CircuitRfDockFactory)vm.DockFactory).PaletteTool!.ConsumeGlyphColumnsRequest());
    }

    /// <summary>A request not yet honoured is what a save records — a Library tab never brought to
    /// the front this session must not lose the count it was opened with.</summary>
    [Fact]
    public void APendingRequest_IsWhatASaveRecords()
    {
        var tool = new PaletteTool { HeldGlyphColumns = 1 };
        tool.RequestGlyphColumns(3);
        Assert.Equal(3, tool.GlyphColumnsToSave);
        tool.ConsumeGlyphColumnsRequest();
        Assert.Equal(1, tool.GlyphColumnsToSave);
    }

    // ── What count is being held ──────────────────────────────────────────────

    /// <summary>
    /// The count is the one on SCREEN — floored, never rounded to nearest. A tile area most of the
    /// way to another column is still showing the smaller number, and rounding up would add a column
    /// the user never asked for the moment the window was first resized.
    /// </summary>
    [Theory]
    [InlineData(124.0, 2)]   // exactly two
    [InlineData(125.0, 2)]   // two, plus the fractional-pixel carry
    [InlineData(160.0, 2)]   // most of the way to three, still showing two
    [InlineData(186.0, 3)]
    [InlineData(298.0, 4)]   // a 300 px column: four glyphs and a strip of nothing
    [InlineData(61.0,  0)]   // narrower than one glyph: no count to hold
    [InlineData(0.0,   0)]
    public void GlyphColumnsIn_CountsWholeColumnsOnly(double tileArea, int expected)
        => Assert.Equal(expected, PaletteColumnWidth.GlyphColumnsIn(tileArea));

    /// <summary>
    /// Count and width are each other's inverse: the width that shows N columns shows exactly N, for
    /// every N the palette could be dragged to. This is what makes the pin settle in one pass rather
    /// than creeping a column wider each time the window moves.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(2.0)]
    [InlineData(2.4)]
    public void TargetWidth_ShowsExactlyTheColumnsItWasAskedFor(double chrome)
    {
        for (int n = 1; n <= 12; n++)
        {
            double width = PaletteColumnWidth.TargetWidth(chrome, n);
            // The arranged column can be a pixel wider than the target (Dock's carry), so both.
            Assert.Equal(n, PaletteColumnWidth.GlyphColumnsIn(width - chrome));
            Assert.Equal(n, PaletteColumnWidth.GlyphColumnsIn(width + 1.0 - chrome));
        }
    }

    /// <summary>The target is the chrome plus whole glyph slots, and fractional chrome rounds UP —
    /// rounding down would take the pixel out of the last glyph column.</summary>
    [Theory]
    [InlineData(0.0, 124.0)]
    [InlineData(2.0, 126.0)]
    [InlineData(2.4, 127.0)]
    public void TargetWidth_IsWholeGlyphSlotsPlusTheChrome(double chrome, double expected)
        => Assert.Equal(expected, PaletteColumnWidth.TargetWidth(chrome));

    // ── Re-proportioning ──────────────────────────────────────────────────────

    /// <summary>
    /// The pinned column arranges to its target, and the difference comes out of the column beside
    /// it — every other column keeps the width the user gave it.
    ///
    /// <para>The row here is the shipped arrangement: Project Tree, documents, Library, with the
    /// Library last.</para>
    /// </summary>
    [Fact]
    public void TryPin_PutsTheTargetOnTheColumnAndTakesItFromTheNeighbour()
    {
        const double pool = 1192.0;
        double[] before = [0.20, 0.69, 0.11];

        Assert.True(PaletteColumnWidth.TryPin(before, index: 2, pool, targetWidth: 126.0, out var after));

        Assert.Equal(126.0, Math.Floor(pool * after[2]));
        Assert.Equal(before[0], after[0]);                                   // Project Tree untouched
        Assert.Equal(1.0, after.Sum(), 10);                                  // still a whole row
        Assert.Equal(before[1] - (after[2] - before[2]), after[1], 10);       // documents absorbed it
    }

    /// <summary>A palette that is already at its target is left entirely alone — no write, so no
    /// layout pass, so nothing to oscillate.</summary>
    [Fact]
    public void TryPin_DoesNothingWhenTheColumnIsAlreadyThere()
    {
        const double pool = 1192.0;
        double want = PaletteColumnWidth.ProportionFor(126.0, pool);
        double[] before = [0.20, 1.0 - 0.20 - want, want];

        Assert.False(PaletteColumnWidth.TryPin(before, index: 2, pool, targetWidth: 126.0, out _));
    }

    /// <summary>
    /// With the palette FIRST in the row — the arrangement where it is docked left of the documents
    /// — the neighbour is still the column next to it.
    /// </summary>
    [Fact]
    public void TryPin_TakesFromTheNextColumnWhenThePaletteIsFirst()
    {
        const double pool = 1192.0;
        double[] before = [0.11, 0.69, 0.20];

        Assert.True(PaletteColumnWidth.TryPin(before, index: 0, pool, targetWidth: 126.0, out var after));

        Assert.Equal(126.0, Math.Floor(pool * after[0]));
        Assert.Equal(before[2], after[2]);
        Assert.Equal(1.0, after.Sum(), 10);
    }

    /// <summary>
    /// No room in the neighbour is a refusal, not a column squeezed to nothing: the window is too
    /// narrow to hold both, and collapsing the documents to show the palette is not the trade.
    /// </summary>
    [Fact]
    public void TryPin_RefusesWhenTheNeighbourCannotGiveTheWidthBack()
    {
        double[] before = [0.90, 0.05, 0.05];
        Assert.False(PaletteColumnWidth.TryPin(before, index: 2, available: 300.0, targetWidth: 126.0, out _));
    }

    /// <summary>
    /// A row with nothing to take from, an index off the end, or a proportion Dock has not assigned
    /// yet (splitters keep NaN until the first arrange) — all left alone rather than guessed at.
    /// </summary>
    [Fact]
    public void TryPin_RefusesEveryRowItCannotReasonAbout()
    {
        Assert.False(PaletteColumnWidth.TryPin([1.0], 0, 1192.0, 126.0, out _));
        Assert.False(PaletteColumnWidth.TryPin([0.2, 0.7, 0.1], 3, 1192.0, 126.0, out _));
        Assert.False(PaletteColumnWidth.TryPin([0.2, double.NaN, 0.1], 2, 1192.0, 126.0, out _));
        Assert.False(PaletteColumnWidth.TryPin([0.2, 0.7, 0.1], 2, available: 0.0, targetWidth: 126.0, out _));
        Assert.False(PaletteColumnWidth.TryPin([0.2, 0.7, 0.1], 2, available: 100.0, targetWidth: 126.0, out _));
    }

    // ── No room is not the same refusal as nothing to do ──────────────────────

    /// <summary>
    /// <see cref="PaletteColumnWidth.TryPin"/> returns false for two opposite situations — the column
    /// is already exactly where it should be, and the row is too narrow to put it there — and the pin
    /// has to tell them apart. It keeps the user's glyph count through the second and re-reads it
    /// through the first, and getting that backwards is what made a resize able to take a column away
    /// for good (owner, 2026-09-14).
    /// </summary>
    [Fact]
    public void HasRoomFor_SeparatesNoRoomFromAlreadyThere()
    {
        const double pool = 1192.0;

        // Already there: nothing to do, but the room is plainly there.
        double want = PaletteColumnWidth.ProportionFor(126.0, pool);
        double[] settled = [0.20, 1.0 - 0.20 - want, want];
        Assert.False(PaletteColumnWidth.TryPin(settled, index: 2, pool, 126.0, out _));
        Assert.True(PaletteColumnWidth.HasRoomFor(settled, index: 2, pool, 126.0));

        // No room: the neighbour cannot give 126 px back out of a 300 px row.
        double[] cramped = [0.90, 0.05, 0.05];
        Assert.False(PaletteColumnWidth.TryPin(cramped, index: 2, available: 300.0, targetWidth: 126.0, out _));
        Assert.False(PaletteColumnWidth.HasRoomFor(cramped, index: 2, available: 300.0, targetWidth: 126.0));
    }

    /// <summary>
    /// Every row <see cref="PaletteColumnWidth.TryPin"/> cannot reason about has no room either —
    /// the two refuse the same set, so a caller reading one never has to re-check the other.
    /// </summary>
    [Fact]
    public void HasRoomFor_RefusesEveryRowTryPinCannotReasonAbout()
    {
        Assert.False(PaletteColumnWidth.HasRoomFor([1.0], 0, 1192.0, 126.0));
        Assert.False(PaletteColumnWidth.HasRoomFor([0.2, 0.7, 0.1], 3, 1192.0, 126.0));
        Assert.False(PaletteColumnWidth.HasRoomFor([0.2, double.NaN, 0.1], 2, 1192.0, 126.0));
        Assert.False(PaletteColumnWidth.HasRoomFor([0.2, 0.7, 0.1], 2, available: 0.0, targetWidth: 126.0));
        Assert.False(PaletteColumnWidth.HasRoomFor([0.2, 0.7, 0.1], 2, available: 100.0, targetWidth: 126.0));

        // And the ordinary case has room, so the assertions above are not vacuous.
        Assert.True(PaletteColumnWidth.HasRoomFor([0.20, 0.69, 0.11], 2, 1192.0, 126.0));
    }

    // ── What the pin must not do with the count ───────────────────────────────

    /// <summary>
    /// The count is a LATCH, so the only thing allowed to change it is the POINTER on the splitter
    /// (owner, 2026-09-21: a window resize could still change how many columns the docked Library
    /// showed). Every read outside a drag is a bug of the same shape, whatever moved the tile area —
    /// a scrollbar that reserves its column, a density change, a font change — because once the
    /// latch reads one the pin enforces one and widening the window back does not undo it.
    ///
    /// <para><c>PaletteColumnPin</c> needs a laid-out dock, which this project has no platform for,
    /// so the gate is held here as a source scan; it was measured in a headless host, where the
    /// former "a pass in which the pool did not change is a splitter drag" inference turned a
    /// three-column palette into a two-column one on a window-HEIGHT change alone.</para>
    /// </summary>
    [Fact]
    public void ThePin_ReadsTheCountOnlyWhenTheUserHasTheSplitter()
    {
        string pin = Src("src/Ui/Views/Palette/PaletteColumnPin.cs");
        pin = Regex.Replace(pin, @"/\*.*?\*/", "", RegexOptions.Singleline);   // block comments
        pin = Regex.Replace(pin, @"//[^\n]*", "");                             // and line comments,
                                                                               // which the doc uses


        // Three reads, and three only: the first measurement of a new arrangement, the passes during
        // a drag, and the one pass after it ends.
        Assert.Equal(3, Regex.Matches(pin, @"PaletteColumnWidth\.GlyphColumnsIn\(").Count);
        Assert.Contains("if (!_settled)", pin);
        Assert.Contains("if (_dragging)", pin);
        Assert.Contains("if (_readAfterDrag)", pin);

        // And the drag is the pointer's own answer, not one inferred from the layout.
        Assert.Contains("_dragging = IsOnSplitter(e.Source as Visual);", pin);
        Assert.DoesNotContain("poolChanged", pin);

        // The ClientSize handler predicts a pool to act on before the pass runs, so the palette never
        // flashes at the scaled width. That prediction must stay out of the MEASUREMENT.
        Assert.Contains("_predicted = pool;", pin);
        Assert.DoesNotContain("_pool = pool;\n        Apply(pool);", pin.Replace("\r\n", "\n"));
    }
}
