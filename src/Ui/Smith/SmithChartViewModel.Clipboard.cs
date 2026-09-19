using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The three clipboard commands — copy the network, paste one in, copy the chart
/// (<c>brief-smith-7-clipboard.md</c>; <c>docs/design/smith-chart.md</c> §6).
/// </summary>
/// <remarks>
/// <b>There is no clipboard code in this file and there is none in this tool</b>
/// (<c>R-smith7-1</c>). What lives here is the DECISION each command makes — which projection, which
/// container, whether a paste is representable, what the strip then says, and that a paste is one undo
/// entry — and the act of putting bytes on a platform clipboard is handed to a delegate the view sets.
/// The view's own implementation of each is one call:
/// <c>SchematicClipboard.CopyAsync</c>, <c>SchematicClipboard.PasteAsync</c> and
/// <c>PlotExporter.CopyPlotToClipboardAsync</c>, which between them already carry the JSON round trip,
/// the SVG, the PDF, the PNG and Windows' <c>CF_ENHMETAFILE</c>.
///
/// <para><b>Delegates rather than an <c>IClipboard</c> parameter</b>, on
/// <see cref="TouchstoneFileChooser"/>'s own precedent: it keeps the whole of each command drivable
/// with no display, which is what lets the gate assert the real projection, the real recognizer and
/// the real container instead of asserting that a call was made.</para>
/// </remarks>
public sealed partial class SmithChartViewModel
{
    // ── the three seams the VIEW fills ───────────────────────────────────────

    /// <summary>
    /// Puts one schematic selection on the system clipboard — the view's single call to
    /// <c>SchematicClipboard.CopyAsync</c>. Null headless: the projection is still built and the note
    /// still fires, and nothing is written.
    /// </summary>
    public Func<SchematicEditModel, Task>? NetworkCopySink { get; set; }

    /// <summary>
    /// Reads one schematic selection back off the system clipboard — the view's single call to
    /// <c>SchematicClipboard.PasteAsync</c>. Null, or a null answer, means there was nothing of ours
    /// there.
    /// </summary>
    public Func<Task<(IReadOnlyList<EditableComponent> Components,
                      IReadOnlyList<EditableWire>      Wires)?>>? NetworkPasteSource { get; set; }

    /// <summary>
    /// Puts the chart on the system clipboard — the view's single call to
    /// <c>PlotExporter.CopyPlotToClipboardAsync</c>, with the container this document owns.
    /// </summary>
    /// <remarks>
    /// <b>The container is PASSED, not looked up</b> (<c>R-smith7-5</c>, collecting brief 5's
    /// <c>R-smith5-5</c>). That call opens with <c>if (container is null) return;</c> and
    /// <c>PlotControl</c> gets its container from a provider the host may never have set — so a copy
    /// wired through the provider produces nothing, raises nothing, and looks exactly like a success.
    /// <see cref="ChartContainer"/> is built in this view model's own constructor and cannot be null,
    /// which takes the failure mode out of the command rather than testing for it.
    /// </remarks>
    public Func<PlotContainerViewModel, Task>? ChartCopySink { get; set; }

    // ── R-smith7-2 / R-smith7-3 — copy the network out ───────────────────────

    /// <summary>
    /// <b>Copy</b> on the network strip: the cascade as a real, runnable two-port schematic selection.
    /// </summary>
    /// <remarks>
    /// The projection is <see cref="SmithSchematicCopy"/>'s — the drawing on screen, mirror and all
    /// (<c>R-smith7-4</c>) — with both ends terminated so what lands in a `.csch` is a complete
    /// circuit rather than a fragment with dangling leads.
    /// </remarks>
    [RelayCommand]
    private async Task CopyNetwork()
    {
        var model = SmithSchematicCopy.Build(_design, DocumentDirectory);
        if (model.Components.Count == 0) return;

        // The note is set BEFORE the write and not after: the write is a platform call that can take
        // a visible moment, and a strip that said nothing until it returned would look as though the
        // menu item had missed.
        StripNotice = SmithSchematicCopy.LossyGeneratorNote(_design);

        if (NetworkCopySink is { } sink) await sink(model);
    }

    // ── R-smith7-6 / R-smith7-7 / R-smith7-8 — paste one in ──────────────────

    /// <summary>
    /// <b>Paste</b> on the network strip: replace the cascade wholesale, or refuse by name.
    /// </summary>
    /// <remarks>
    /// <b>One paste is one undo entry</b> (<c>R-smith7-8</c>), restoring the entire previous network —
    /// brief 5's rule applied to a discrete command instead of a drag, and free here because every
    /// mutation in this window goes through <see cref="Edit"/>, which snapshots the whole design.
    ///
    /// <para><b>The GENERATOR table is not touched.</b> What is replaced is the cascade; the ports the
    /// pasted selection carries told the recognizer which end was which and are then discarded.
    /// Overwriting the generator from a <c>Term</c>'s reference impedance would replace a table the
    /// user may have imported from an `.s1p` with a single number, on the strength of a paste they
    /// made to change the network.</para>
    ///
    /// <para>A refusal is a <see cref="StripNotice"/> rather than a
    /// <see cref="SmithChartViewModel.Refusal"/>: the document is still perfectly valid — it is the
    /// clipboard that was not — and the refusal half of the strip is for a design that cannot be
    /// evaluated.</para>
    /// </remarks>
    [RelayCommand]
    private async Task PasteNetwork()
    {
        if (NetworkPasteSource is not { } source) return;

        var payload = await source();
        if (payload is not { } p)
        {
            StripNotice = "There is no circuitRF schematic selection on the clipboard.";
            return;
        }

        var result = SmithPasteRecognizer.Recognize(
            p.Components, p.Wires, MirrorNetwork, _design.Chart.DesignFrequencyHz);

        if (result.Refusal is { Length: > 0 } refusal)
        {
            StripNotice = "Nothing was pasted — " + refusal;
            return;
        }

        var elements = result.Elements!;

        // The selection follows the ELEMENT the user is looking at by name, and every name in the
        // cascade is about to change, so the selection is dropped to the first pasted element rather
        // than left pointing at something that no longer exists.
        _selectedElementName = null;

        Edit($"Paste {elements.Count} element{(elements.Count == 1 ? "" : "s")}", () =>
        {
            _design.Elements.Clear();
            foreach (var e in elements) _design.Elements.Add(e);
        });

        SelectElement(elements.Count > 0 ? 0 : -1);

        // R-smith7-7: the strip states WHICH end rule fired, because the two can disagree and the
        // user is the only one who knows which they meant. Set after Edit, which clears the note.
        StripNotice = result.Note;
    }

    // ── R-smith7-5 — copy the chart ──────────────────────────────────────────

    /// <summary>
    /// <b>Copy</b> on the chart: PDF, SVG, the Data Display config JSON and a 2× bitmap, all on the
    /// clipboard at once.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is hidden and nothing is cloned, because nothing is narrowed</b>
    /// (<c>R-smith7-5a</c>). Trajectories, load points, conjugate targets, the arrowheads, the load
    /// labels and the grippers are all in the picture — the first three are traces on
    /// <see cref="ChartPlot"/> and the last three are <see cref="ChartOverlay"/>'s, which the export
    /// path now carries (<c>PlotContainerViewModel.Overlay</c>). Were a later round to hide any of
    /// them it would have to do so on a CLONE of the plot and the theme, never by mutating the live
    /// ones and putting them back: that is <c>TechnologyCache</c>'s defect, the one that only appears
    /// on the SECOND call — the first copy is perfect, the chart is then missing its grippers until
    /// the document is reopened, and nothing reports anything.
    /// </remarks>
    [RelayCommand]
    private async Task CopyChart()
    {
        if (ChartCopySink is { } sink) await sink(ChartContainer);
    }
}
