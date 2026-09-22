using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// §2.5's comparison, shown — and saved as the page <c>RailReportPage</c> draws.
/// </summary>
/// <remarks>
/// <b>It composes nothing.</b> Every heading, every line and the three headline sentences come out
/// of <see cref="RailComparisonReport"/>; the page comes out of <see cref="RailComparisonExport"/>,
/// which is §11.7's one route from a comparison to a page. What this class does is show a list and
/// write a file.
/// </remarks>
public partial class RailCompareDialog : Window
{
    public RailCompareDialog() => InitializeComponent();

    private RailComparisonReport? _report;
    private LayoutView? _board;
    private Technology? _technology;
    private RailMapScene? _map;
    private IReadOnlyList<string> _provenance = [];
    private IReadOnlyList<RailPartHighlight> _notFitted = [];

    /// <param name="report">What <see cref="RailComparisonReport.Build"/> produced.</param>
    /// <param name="board">The JUDGED design's artwork — see <c>RailComparisonExport.PageOf</c>
    /// for why it is that one and not the reference's.</param>
    /// <param name="technology">Its stackup.</param>
    /// <param name="map">The scene the board panel is showing, or null.</param>
    /// <param name="provenance">R-rail10-5's lines, from the run that produced the results.</param>
    /// <param name="notFitted">The judged design's unmounted parts — see
    /// <c>RailComparisonExport.PageOf</c>.</param>
    public RailCompareDialog(
        RailComparisonReport report,
        LayoutView? board,
        Technology? technology,
        RailMapScene? map,
        IReadOnlyList<string> provenance,
        IReadOnlyList<RailPartHighlight>? notFitted = null) : this()
    {
        ArgumentNullException.ThrowIfNull(report);

        _report     = report;
        _board      = board;
        _technology = technology;
        _map        = map;
        _provenance = provenance ?? [];
        _notFitted  = notFitted ?? [];

        BannerText.Text =
            $"Reference: {report.Match.ReferenceName} · judged: {report.Match.TargetName} · "
          + $"rail '{report.Match.Reference?.Name ?? "(none)"}'. "
          + "A positive Δ means the judged design is the higher impedance.";

        if (report.Refusal is { Length: > 0 } refusal)
        {
            RefusalText.Text = refusal;
            RefusalText.IsVisible = true;
            FindingsCard.IsVisible = false;
            SaveButton.IsEnabled = false;
        }
        else
        {
            FindingsList.ItemsSource = report.Findings;
            SectionsList.ItemsSource =
                report.Sections.Select(s => new RailCompareSectionView(s.Heading, s.Lines)).ToList();
        }

        SaveButton.Click  += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();
    }

    private async Task SaveAsync()
    {
        if (_report is null || StorageProvider is not { } sp) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the comparison",
            // No extension on the suggested name — the provider appends the chosen one, and
            // supplying both spells it twice (the Match Designer's own finding).
            SuggestedFileName = $"{_report.Match.TargetName} against {_report.Match.ReferenceName}",
            DefaultExtension = "pdf",
            FileTypeChoices =
            [
                new FilePickerFileType("Report page (PDF)") { Patterns = ["*.pdf"] },
                new FilePickerFileType("Report page (SVG)") { Patterns = ["*.svg"] },
            ],
        });
        if (file is null) return;

        string path = file.Path.LocalPath;
        var request = RailComparisonExport.PageOf(
            _report, _board, _technology, _map, _provenance, _notFitted);

        try
        {
            // Complete before anything reaches the filesystem, for `render`'s own reason.
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(path, RailComparisonExport.BuildSvg(request),
                                  new System.Text.UTF8Encoding(false));
            else
                File.WriteAllBytes(path, RailComparisonExport.BuildPdf(request));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            RefusalText.Text = $"The comparison did not write: {ex.Message}";
            RefusalText.IsVisible = true;
        }
    }
}
