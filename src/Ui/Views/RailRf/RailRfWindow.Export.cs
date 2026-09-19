// The Export and Report buttons (owner, 2026-09-19: "Compare… and Export are disabled in the UI and
// have tooltip saying that they are not wired yet. Why not?").
//
// ── THE ANSWER, AND WHAT IT COST TO FIX ─────────────────────────────────────────────────────────
//
// Every writer already existed and every one of them was PRIVATE TO `src/Cli/Rail.cs`, which this
// project may not reference. So the three buttons were left disabled rather than given a second copy
// of the CSV writer, the DataSet pack and the report sections — which was the right call at the
// time and the wrong place to leave it. `RailExport` in `src/Design` is the fix: the writers moved
// below the firewall, the verb calls them, and this file calls the same ones.
//
// ── THE EXTENSION PICKS THE FORMAT, EXACTLY AS `circuitrf rail -o` DOES ─────────────────────────
//
// One dialog, six file types, and the dispatch below is `Rail.OutputKindOf`'s own table. A menu of
// formats on the button would be a second vocabulary for one decision the CLI already spells — and
// then the two would have to be kept in step by somebody remembering.
//
// Touchstone is the one the CLI REFUSES, and it is refused here in the same words and for the same
// reason: Z(f) at the observation ports is the frequency answer, and this is the DC phase. A `.s2p`
// of a rail is exactly the file a caller would go on to plot, and one holding the DC point repeated
// would look like a measurement.
//
// ── AND THE PAGE COMES OUT OF `RailReportPage`, NOT FROM HERE ───────────────────────────────────
//
// §11.7's last line: there is one route from an overlay to a page and not two. That route is
// `RailReportPage` in `CircuitRF.Render`, which `circuitrf rail -o report.svg` draws its page with
// and which `RailComparisonExport` already maps the A/B report onto. What this file decides is what
// every railRF export decides and nothing more: which scene, which theme variant, and where an
// instance's cell reference resolves from.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Theming;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    /// <summary>The report page's size, in points — <c>circuitrf rail</c>'s own, so the window's
    /// page and the headless one are the same page.</summary>
    private const int PageW = 1600;

    /// <inheritdoc cref="PageW"/>
    private const int PageH = 1200;

    private void WireExportButtons()
    {
        // Two buttons, one writer. Report opens on the page format because a report IS the page;
        // Export opens on the data format because that is what the tooltip has always promised.
        // Both offer every type, because refusing to write a CSV from the Report button would be a
        // distinction only this file knows about.
        ReportButton.Click += async (_, _) => await ExportAsync("pdf");
        ExportButton.Click += async (_, _) => await ExportAsync("csv");
    }

    /// <summary>
    /// Asks for a path and writes what is on screen to it.
    /// </summary>
    /// <remarks>
    /// <b>The bytes are complete before anything reaches the filesystem</b>, exactly as the verb
    /// arranges it and for its reason: a failure part way through a page leaves no half-written
    /// file for someone to open.
    /// </remarks>
    private async Task ExportAsync(string defaultExtension)
    {
        if (Vm is not { CanExport: true } vm || StorageProvider is not { } sp) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the rail result",
            // NO extension on the suggested name: Avalonia appends DefaultExtension itself when the
            // name has none, and supplying both spells it twice (the Match Designer's own report).
            SuggestedFileName = vm.ExportSuggestedName,
            DefaultExtension = defaultExtension,
            FileTypeChoices =
            [
                new FilePickerFileType("Report page (PDF)")   { Patterns = ["*.pdf"] },
                new FilePickerFileType("Report page (SVG)")   { Patterns = ["*.svg"] },
                new FilePickerFileType("Table (CSV)")         { Patterns = ["*.csv"] },
                new FilePickerFileType("NumPy array (.npy)")  { Patterns = ["*.npy"] },
                new FilePickerFileType("MATLAB (.mat)")       { Patterns = ["*.mat"] },
                new FilePickerFileType("Tab separated (.txt)"){ Patterns = ["*.txt", "*.tsv"] },
            ],
        });
        if (file is null) return;

        string path = file.Path.LocalPath;

        try
        {
            Write(vm, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or NotSupportedException)
        {
            vm.Refusal = new RailRefusal(
                $"The export did not write: {ex.Message}", RailRefusalControl.None);
        }
    }

    /// <summary>
    /// One format each, dispatched on the extension — <c>Rail.OutputKindOf</c>'s own table.
    /// </summary>
    private void Write(RailRfViewModel vm, string path)
    {
        if (vm.ExportProvenance() is not { } provenance) return;

        var results = vm.ExportResults;

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csv":
                File.WriteAllText(path, RailExport.Csv(vm.Document, results, provenance),
                                  new System.Text.UTF8Encoding(false));
                break;

            case ".svg":
                File.WriteAllText(
                    path,
                    PlotDocumentWriter.BuildSvgString(
                        canvas => RailReportPage.Draw(canvas, PageOf(vm, provenance), PageW, PageH),
                        new PagePlacement(PageW, PageH, 0f)),
                    new System.Text.UTF8Encoding(false));
                break;

            case ".pdf":
                File.WriteAllBytes(
                    path,
                    PlotDocumentWriter.BuildPdfBytes(
                        canvas => RailReportPage.Draw(canvas, PageOf(vm, provenance), PageW, PageH),
                        new PagePlacement(PageW, PageH, 0f)));
                break;

            default:
            {
                // The verb's own last case: .npy, .mat, and anything else as TSV. A Touchstone
                // extension is the one refusal, in the verb's own words.
                if (RfCore.TouchstoneIO.ParsePortsFromExtension(path) is > 0)
                {
                    vm.Refusal = new RailRefusal(
                        $"'{Path.GetFileName(path)}' is a Touchstone name, and railRF has no "
                      + "frequency answer to put in one yet: Z(f) at the observation ports is the "
                      + "frequency phase and this is the DC phase. A file holding the DC point "
                      + "repeated would look like a measurement. Write a .csv, a .npy or a report "
                      + "page instead.",
                        RailRefusalControl.None);
                    return;
                }

                var format = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".npy" => ExportFormat.Npy,
                    ".mat" => ExportFormat.Mat,
                    _      => ExportFormat.Tsv,
                };
                DataSetExporter.Export(RailExport.Pack(results, provenance), path, format);
                break;
            }
        }
    }

    /// <summary>
    /// The page request for what the window is showing.
    /// </summary>
    /// <remarks>
    /// <b>The picture is the DROP map</b>, which is §2.4's headline, the tab the window opens on and
    /// the one a report is about — the verb makes the same choice in the same place. The theme is
    /// the one this installation copies pictures in (<c>ClipboardRenderPolicy</c>), not the one the
    /// window happens to be wearing: a user who has said "always export in light mode" has said it
    /// for this page too.
    /// </remarks>
    private static RailReportPageRequest PageOf(RailRfViewModel vm, RailProvenance provenance)
    {
        var first = vm.ExportResults.Count > 0 ? vm.ExportResults[0] : null;

        return new RailReportPageRequest
        {
            Title      = vm.Title,
            Provenance = provenance.Lines,
            Sections   = [.. RailExport.Sections(vm.ExportResults, rows: 10, all: false)
                                       .Select(x => new RailReportSection(x.Heading, x.Lines))],
            Board      = vm.BoardLayout?.Model,
            Technology = vm.Board?.Technology,
            Map        = RailMapScene.Build(first, RailMapKind.Drop,
                                            vm.Board?.DbuPerMicron ?? 1000),
            Theme      = ThemeService.Active,
            Variant    = ClipboardRenderPolicy.Resolve().Variant,
            BaseDir    = vm.ExportBaseDir,
        };
    }
}
