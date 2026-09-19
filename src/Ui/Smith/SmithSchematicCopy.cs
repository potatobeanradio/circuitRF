using System;
using System.Linq;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The network strip <b>as editable schematic objects</b> — what its <c>Copy</c> puts on the clipboard
/// (<c>brief-smith-7-clipboard.md</c> <c>R-smith7-2</c>; <c>docs/design/smith-chart.md</c> §6.1).
/// </summary>
/// <remarks>
/// <b>Nothing here renders, serialises or touches the clipboard</b> (<c>R-smith7-1</c>). All of that is
/// <c>SchematicClipboard.CopyAsync</c>'s — the same call the schematic editor's own Copy makes, so this
/// tool gets the JSON round-trip, the SVG, the PDF, the PNG and Windows' <c>CF_ENHMETAFILE</c> for free
/// and cannot drift from the editor's clipboard behaviour. <see cref="MatchSchematicCopy"/> is the
/// worked example this file follows, and the Windows path is the reason: it must be written in a
/// SINGLE P/Invoke session, because Avalonia's <c>SetDataAsync</c> empties the clipboard and keeps
/// ownership. None of that is spent again here.
///
/// <para><b>And it does not build a second drawing either.</b> The Designer's copy has to project its
/// ladder itself, because that pane's drawing is assembled from render records with no editable model
/// behind it. This one's already IS a <see cref="Design.Schematic.SchematicEditModel"/> —
/// <see cref="SmithNetworkModel"/> builds one and the strip renders it — so the copy is that same
/// projection with its two ends terminated, which is what makes "the copy is the drawing you were
/// looking at" true by construction rather than by a second layout pass that agrees today.</para>
///
/// <para><b>The copy follows the mirror</b> (<c>R-smith7-4</c>). <c>MatchSchematicCopy</c>'s own stated
/// rule is that a copy is <i>the drawing on screen, not the flattened cell</i>; someone who flipped the
/// network to make a figure and then copied it would not thank us for un-flipping it on the way out.
/// <c>MirrorX</c> travels on each component, so a 2-port's port 1 still faces the generator and the
/// pasted circuit is electrically identical either way — only its geometry is reflected.</para>
///
/// <para><b>No analysis card is copied.</b> A pasted selection is a fragment of a circuit, and the
/// TestBench it lands in owns its analyses. The projection has never carried one.</para>
/// </remarks>
public static class SmithSchematicCopy
{
    /// <summary>
    /// The cascade as a real, runnable two-port: the generator end a <c>TermG</c> with <c>Num=1</c>
    /// and the generator's impedance at the design frequency, the load end a <c>TermG</c> with
    /// <c>Num=2</c> and Z₀_chart.
    /// </summary>
    /// <param name="design">The document. Read, never written.</param>
    /// <param name="documentDirectory">What a file element's relative <c>FileRef</c> resolves
    /// against.</param>
    public static Design.Schematic.SchematicEditModel Build(SmithDesign design, string? documentDirectory)
        => SmithNetworkModel.Build(design, documentDirectory, terminated: true).Edit;

    /// <summary>
    /// The sentence the status strip says when the copy was a lossy projection of the generator table,
    /// or null when it was not (<c>R-smith7-3</c>).
    /// </summary>
    /// <remarks>
    /// <b>Stated rather than prevented.</b> A <c>Term</c> carries one impedance and the table may carry
    /// many, so a multi-row generator cannot survive the trip whole — but the copy is still the right
    /// circuit at the design frequency, and that is what someone pasting into a presentation or into a
    /// schematic wants. Refusing it, or quietly picking a row, are both worse answers than saying which
    /// frequency was used.
    /// </remarks>
    public static string? LossyGeneratorNote(SmithDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        if (design.Generator.Rows.Count <= 1) return null;

        string f = SmithPlotBuilder.FrequencyLabel(design.Chart.DesignFrequencyHz);
        return $"Copied at {f}. A Term carries ONE impedance and the generator table has "
             + $"{design.Generator.Rows.Count} rows, so the copied port is the generator as it is at "
             + $"{f} and nowhere else.";
    }

    /// <summary>The instance names the copy's two ports carry, for a caller that needs to find them
    /// again — the projection's own, never a second spelling.</summary>
    public static (string Generator, string Load) PortNames
        => (SmithNetworkModel.GeneratorName, SmithNetworkModel.LoadName);
}
