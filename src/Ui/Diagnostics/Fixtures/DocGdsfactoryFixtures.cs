using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The user guide's "Using a gdsfactory kit" page (brief-gdf-5): the consent prompt, and the report an
/// import ends with. <b>The kit is invented</b> — no real kit is named in this repository — and so is
/// the path it is installed at, because the real flow reads the user's own environment and a figure
/// built from this machine's would carry its paths into a committed picture. The dialogs are the
/// application's own, built by the functions it shows them with.
/// </summary>
public static class DocGdsfactoryFixtures
{
    private const string Module = "acmerf_gf";
    private const string Version = "0.4.1";
    private const string Installed = "/home/you/.venvs/rf/lib/python3.12/site-packages/acmerf_gf";
    private const string Python = "/home/you/.venvs/rf/bin/python";

    /// <summary>The consent prompt, before anything of the kit has run.</summary>
    public static FigureScene TrustPrompt()
    {
        string dir = "/designs/PowerAmp/" + Module + "-pcells";
        var kit = new PCellKit(dir, dir + "/kit_entry.py")
        {
            Module = Module, ModuleVersion = Version, Location = Installed, Interpreter = Python,
        };
        return Lifted(PCellTrustDialog.Build([kit with { TrustKey = Installed }]));
    }

    /// <summary>The import report: what is offered, what is not and why, and the technology offer.</summary>
    public static FigureScene ImportReport()
    {
        var report = new GdsfactoryImportReport
        {
            Module = Module,
            GdsfactoryVersion = "9.51.0",
            Offered = ["coupler_rf", "cpw_line", "inductor", "mim_cap", "pad_rf", "resistor_poly", "tline_bend", "wilkinson"],
            NotOffered = new()
            {
                // [cell, detail] — for a refused cell the detail is the parameters it requires.
                ["required arguments"] = [["array_of", "component, spacing"]],
                ["raised while being built at its defaults"] = [["logo", "FileNotFoundError: logo.gds"]],
            },
            ParametersNotOffered = new() { ["wilkinson"] = ["cross_section"], ["coupler_rf"] = ["layers"] },
            Models = new() { ["inductor"] = "ind_spiral", ["mim_cap"] = "cap_mim" },
            Parameters = new()
            {
                ["inductor"] = ["width", "space", "turns", "diameter", "model"],
                ["mim_cap"] = ["width", "length", "model"],
            },
        };
        var dialog = GdsfactoryImportReportDialog.Build(
            new GdsfactoryKit("acmerf-gf", Version, ">=3.12", Module, Installed), report, null, [], allowed: true,
            new GdsfactoryImportReportDialog.TechnologyOffer(18, 6, Module + ".ctech"));
        // Every section but the long list of offered cells open, so what was NOT offered, and why, is on show.
        foreach (var expander in dialog.GetLogicalDescendants().OfType<Expander>().Skip(1)) expander.IsExpanded = true;
        return Lifted(dialog);
    }

    /// <summary>A dialog's content, lifted out of its window with its margin kept as padding, as
    /// <see cref="DocAppNoteGerberFixtures"/> lifts one.</summary>
    private static FigureScene Lifted(Window dialog)
    {
        var content = (Control)dialog.Content!;
        var styles = dialog.Styles.ToList();
        dialog.Content = null;
        dialog.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        var margin = content.Margin;
        content.Margin = default;
        return new FigureScene(new Border { Padding = margin, Child = content });
    }
}
