using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// EVERY deep link <see cref="DocLauncher"/> is capable of emitting, enumerated.
///
/// <para>The Help buttons in the Parameter Editor, the Analyses list and the Plot inspector do not
/// open the documentation index — they open a specific page at a specific anchor. That anchor scheme
/// is a contract between the application and the generated HTML, and it fails the way a broken link
/// always fails: the browser opens the page at the top and nobody notices the section is missing.
/// So the generator checks it, and the check needs a list of what the app can ask for.</para>
///
/// <para>Framework-free on purpose, so <c>tests/Ui.Tests</c> can assert the contract without a UI
/// platform. Keep it in step with <c>DocLauncher.OpenComponent</c> /
/// <c>AnalysesListView.OnHelp</c> / <c>PlotInspectorView.OnPlotTypeHelp</c>, which are the only three
/// places an anchor is produced.</para>
/// </summary>
public static class DocAnchors
{
    /// <summary>One required destination: a page, relative to <c>docs/user/</c>, and an anchor in it.</summary>
    public readonly record struct Link(string Page, string Anchor)
    {
        public override string ToString() => Anchor.Length == 0 ? Page : $"{Page}#{Anchor}";
    }

    /// <summary>
    /// The component anchor for a kind — the same expression <c>DocLauncher.OpenComponent</c> uses.
    /// The three tuner variants deliberately share one section.
    /// </summary>
    public static string ComponentAnchor(SymbolKind kind) => kind switch
    {
        SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner => "tuner",
        SymbolKind.Generic => "",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Analysis anchors, from <c>AnalysesListView.OnHelp</c>'s switch.</summary>
    public static readonly IReadOnlyList<string> AnalysisAnchors =
        ["dc", "s-parameters", "harmonic-balance", "parametric-sweep", "loadpull-pursuit", "loadpull"];

    /// <summary>Plot-type anchors, from <c>PlotInspectorView.OnPlotTypeHelp</c>'s expression.</summary>
    public static readonly IReadOnlyList<string> PlotTypeAnchors =
        ["smith", "polar", "table", "rectangular"];

    /// <summary>Pages opened whole, with no anchor.</summary>
    public static readonly IReadOnlyList<string> WholePages =
        ["index.html", "reference/components.html", "reference/nonlinear-capacitor.html",
         "reference/em-setup.html", "reference/harmonicarf.html", "reference/wbond.html",
         "reference/match.html"];

    /// <summary>Every destination the application can navigate to, deduplicated.</summary>
    public static IReadOnlyList<Link> All()
    {
        var links = new List<Link>();

        foreach (var kind in Enum.GetValues<SymbolKind>())
        {
            if (SymbolArtworkGenerator.NotUserPlaceable.Contains(kind)) continue;
            var anchor = ComponentAnchor(kind);
            if (anchor.Length == 0) continue;
            links.Add(new Link("reference/components.html", anchor));
        }

        foreach (var a in AnalysisAnchors) links.Add(new Link("reference/simulations.html", a));
        foreach (var a in PlotTypeAnchors) links.Add(new Link("reference/plot-types.html", a));
        foreach (var p in WholePages)      links.Add(new Link(p, ""));

        return links.Distinct().ToList();
    }
}
