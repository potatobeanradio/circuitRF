// ================================================================
//  MatchSolutionScrollTests.cs  —  the applied solution has to be VISIBLE when the Designer opens.
//
//  Owner-reported, 2026-08-28: opening the Match Designer on a design that already has a solution
//  picked shows the Solutions pane unscrolled, so the one card carrying the green border is off
//  screen and the panel reads as though nothing were selected.
//
//  The view-model half is exercised directly. The scroll itself is view code-behind, which this
//  project cannot host headlessly, so it is a SOURCE scan — the same technique
//  MatchDesignerHostingTests uses for the same reason, gating the property that would actually
//  regress rather than the pixels.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchSolutionScrollTests(ITestOutputHelper output)
{
    private static (SchematicViewModel Vm, EditableComponent Comp) Host(MatchDesign design)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        model.Components.Add(comp);
        return (new SchematicViewModel(model), comp);
    }

    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    /// <summary>
    /// <b>The view-model half was never the bug, and this is what says so.</b> A design reopened with
    /// a solution already applied badges that row Current, and the row is listed whatever the filter
    /// says — so the green border is on the right card. What the user could not see was the card.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void AReopenedDesign_BadgesItsAppliedSolutionCurrent()
    {
        var (vm1, comp1) = Host(Golden());
        var first = new MatchDesignerViewModel();
        first.SetTarget(vm1, comp1);
        first.WaitForAnalysis();
        Assert.NotEmpty(first.AllSolutions);

        // Something well down the list, so the reopened window genuinely has to scroll to show it.
        var pick = first.AllSolutions.Skip(4).First();
        pick.Apply();
        first.WaitForAnalysis();
        Assert.True(first.Solutions.Any(r => r.IsCurrent));

        string payload = comp1.Parameters.First(p => p.Name == "Design").Expression;
        first.Dispose();

        Assert.True(MatchEmbedding.TryDecode(payload, out var stored));
        var (vm2, comp2) = Host(stored!);
        var reopened = new MatchDesignerViewModel();
        reopened.SetTarget(vm2, comp2);
        reopened.WaitForAnalysis();

        var current = reopened.Solutions.Where(r => r.IsCurrent).ToList();
        output.WriteLine($"{reopened.AllSolutions.Count} solutions, "
                         + $"applied at index {reopened.Solutions.IndexOf(current.FirstOrDefault()!)}");
        Assert.Single(current);
        Assert.True(reopened.HasAppliedSolution);

        reopened.Dispose();
    }

    /// <summary>
    /// <b>The once-only scroll flag is spent when the scroll LANDS, never when it is attempted.</b>
    /// </summary>
    /// <remarks>
    /// This is the whole of the fix. <c>Show</c> starts the solution search BEFORE the window is
    /// constructed, so on a first open the rows are usually already in the list when <c>Loaded</c>
    /// fires — the collection-changed handler never sees them and the only attempt is the one
    /// <c>WireSolutionsList</c> makes, at a moment when the ListBox has not been arranged and
    /// <c>ScrollIntoView</c> does nothing. Setting the flag before knowing the scroll worked is what
    /// made that attempt the ONLY attempt.
    /// </remarks>
    [Fact]
    public void TheOnceOnlyScroll_SetsItsFlagOnlyAfterScrollIntoViewHasRun()
    {
        string src = Source("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");

        int body = src.IndexOf("private void ScrollToApplied(bool once, int attempt", StringComparison.Ordinal);
        Assert.True(body > 0, "the scroll no longer takes a once/attempt pair");

        int scroll = src.IndexOf("ScrollIntoView(applied)", body, StringComparison.Ordinal);
        int flag = src.IndexOf("_scrolledToApplied = true", body, StringComparison.Ordinal);
        Assert.True(scroll > 0 && flag > scroll,
            "the once-only flag must be set AFTER ScrollIntoView, or a scroll that could not land "
            + "still consumes the one attempt");

        // And there must be a bounded retry for the not-yet-arranged case.
        Assert.Contains("ItemsPanelRoot is null", src, StringComparison.Ordinal);
        Assert.Contains("MaxScrollToAppliedAttempts", src, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"attempt\s*<\s*MaxScrollToAppliedAttempts"), src);
    }

    /// <summary>
    /// The applied row's ONLY visible mark is the card's own border, so nothing else can stand in for
    /// scrolling to it.
    /// </summary>
    /// <remarks>
    /// The list's selection is deliberately invisible — a selection highlight beside the card's
    /// accent border would be a second, contradictory mark — which is exactly why an applied card
    /// that is off screen reads as "nothing is selected" and why the scroll is not cosmetic.
    /// </remarks>
    [Fact]
    public void TheSelectionHighlight_IsInvisible_SoTheScrollIsTheOnlyWayToSeeTheAppliedCard()
    {
        string xaml = Source("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");

        Assert.Contains(
            "<Style Selector=\"ListBox.rows ListBoxItem:selected /template/ ContentPresenter\">",
            xaml, StringComparison.Ordinal);
        Assert.Contains("AutoScrollToSelectedItem=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes.current=\"{Binding IsCurrent}\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The global font defaults reach <c>SelectableTextBlock</c> too</b> (owner, 2026-08-28: the
    /// Response readout "is now rendered too big").
    /// </summary>
    /// <remarks>
    /// An Avalonia type selector matches its type EXACTLY, so <c>Style Selector="TextBlock"</c> stops
    /// at the base class and every selectable readout in the application falls back to Avalonia's own
    /// defaults — Stretch on both axes, which makes each line claim more height than the TextBlock it
    /// replaced. The second style is the fix, and it must stay TARGETED: widening the first to
    /// <c>:is(TextBlock)</c> would reach <c>TextPresenter</c> inside every TextBox and ComboBox.
    /// </remarks>
    [Fact]
    public void TheGlobalFontDefaults_AlsoTargetSelectableTextBlock()
    {
        string styles = Source("src", "Ui", "Styles", "CircuitRfStyles.axaml");

        int plain = styles.IndexOf("<Style Selector=\"TextBlock\">", StringComparison.Ordinal);
        int selectable = styles.IndexOf("<Style Selector=\"SelectableTextBlock\">", StringComparison.Ordinal);
        Assert.True(plain > 0, "the global TextBlock defaults are gone");
        Assert.True(selectable > plain, "SelectableTextBlock has no global defaults of its own");

        string block = styles[selectable..styles.IndexOf("</Style>", selectable, StringComparison.Ordinal)];
        foreach (string property in (string[])["FontSize", "VerticalAlignment", "HorizontalAlignment"])
            Assert.Contains($"Property=\"{property}\"", block, StringComparison.Ordinal);

        // Widening the plain selector instead would restyle the inside of every text input.
        Assert.DoesNotContain("<Style Selector=\":is(TextBlock)\">", styles, StringComparison.Ordinal);
    }
}
