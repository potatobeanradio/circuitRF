using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.WBond;
using CircuitRF.WBond.Mom;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The two "publish this wirebond network" actions — Export Touchstone and Compare Distributed Model —
/// as one implementation with more than one entry point.
///
/// <h3>Why this exists rather than a handler per view</h3>
/// <para><b>A wirebond design is reachable from two completely different editors</b>, and that was not
/// obvious until the owner reported it twice (2026-08-18: <i>"the export UI can't be accessed from
/// circuitRF — only from wBond"</i>, then <i>"I still don't see any of the new buttons in the wBond
/// hosted layout"</i>):</para>
/// <list type="bullet">
/// <item><b><c>WBondEditorView</c></b> — a <c>.wBond</c> opened as its own document, and the whole of
///   the standalone <c>wBond</c> binary. Its own toolbar row carries these.</item>
/// <item><b><c>LayoutEditorView</c></b> — a <c>.clay</c> with a <c>.wBond</c> beside it (WB40), which
///   is how a wirebond is normally worked on inside circuitRF. <b>There is no
///   <c>WBondEditorView</c> anywhere in that document</b>; the wire tools live in the layout editor's
///   own toolbar, gated on <c>HasWireDesign</c>, and until now that group ended at Transform.</item>
/// </list>
///
/// <para>The repository's own rule — route every entry point through the same accessor, never a second
/// implementation — is what this file is. The file-picker flow, the extension handling and the refusal
/// reporting are subtle enough that two copies would drift.</para>
/// </summary>
internal static class WBondPublishCommands
{
    /// <summary>
    /// 1 while a wirebond computation is running anywhere in the process; 0 otherwise.
    ///
    /// <para><b>One at a time, and the reason is memory rather than tidiness.</b> A distributed run's
    /// peak is <c>threads × 16·N_s²</c> bytes on top of the setup's own two N × N matrices —
    /// <see cref="WireMomCost.SolveThreadCount"/> sizes the thread count against a quarter of available
    /// memory on the assumption that it is the only such run. Two concurrent exports would each size
    /// themselves that way and together exceed the budget both were checked against. The export button
    /// stays live while a run is in flight (it is not modal), so this is reachable by anyone who presses
    /// it twice.</para>
    /// </summary>
    private static int _running;

    /// <summary>What happened, for a host to report through whatever status channel it has.</summary>
    /// <param name="Posted">
    /// True when this outcome has ALREADY been put in the Messages panel, so a host with a panel must
    /// not post it a second time and a host with a status line still should.
    ///
    /// <para><b>Why the flag rather than letting each host post.</b> The write's final line has to
    /// carry the file path — the Messages panel renders a path as a reveal-in-file-manager link, and
    /// that link is the point of the line. The path is known here, so the line is posted here. But
    /// the layout-hosted entry point then posted the outcome AGAIN through
    /// <c>LayoutEditorViewModel.ReportMessage</c>, which goes to the same panel and carries no path —
    /// so the last thing in the panel after an export was a linkless duplicate, and the line with the
    /// link sat above it looking like part of the progress trace (owner, 2026-08-19).</para>
    /// </param>
    internal readonly record struct Outcome(string Message, bool IsWarning, bool Posted = false)
    {
        /// <summary>Nothing to say — the user cancelled a picker or a dialog.</summary>
        public static Outcome Silent => new("", false);

        public bool IsSilent => Message.Length == 0;
    }

    /// <summary>
    /// Writes the design's network as a Touchstone file.
    ///
    /// <para>The dialog states the port map, the model and the cost before anything is written; the
    /// write itself goes through <see cref="WBondTouchstoneExport"/>, which is <c>RFNetwork</c>'s
    /// conversions and <c>TouchstoneExporter</c> and nothing of its own.</para>
    /// </summary>
    internal static async Task<Outcome> ExportTouchstoneAsync(
        Window owner, WBondDesign design, IMessageSink? messages = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(design);

        if (design.Arrays.Count == 0)
            return new Outcome("This design has no wire arrays, so it has no ports to publish.", true);

        var options = await WBondTouchstoneExportDialog.ShowAsync(owner, design);
        if (options is null) return Outcome.Silent;

        // The PORT COUNT follows the chosen basis, not the array count — a terminal-basis export gives
        // every terminal its own port, so three arrays is a 6-port. Asking the one method that decides
        // the port map keeps the picker's filter from disagreeing with the file that gets written.
        int ports = WBondTouchstoneExport.PortNames(design, options.PortBasis).Count;

        // The suffix is the exporter's to choose from the port count, so the picker asks for a base
        // name and never for an extension it might disagree with.
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Touchstone",
            SuggestedFileName = "wirebonds",
            FileTypeChoices = [new FilePickerFileType($"Touchstone ({ports}-port)") { Patterns = [$"*.s{ports}p"] }],
        });

        if (file?.TryGetLocalPath() is not { } chosen) return Outcome.Silent;

        // Strip whatever extension the picker attached: the exporter appends its own .sNp, and a
        // doubled suffix is the one way this write can produce a file nobody can find.
        string baseNoSuffix = Path.Combine(
            Path.GetDirectoryName(chosen) ?? "", Path.GetFileNameWithoutExtension(chosen));

        // The write is the LAST thing, so a run that is stopped or refused leaves no half-written file
        // behind — WBondTouchstoneExport.Export computes the whole network before it opens the file.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new Outcome(
                "A wirebond computation is already running. Wait for it to finish before starting another.",
                true);

        try
        {
            var outcome = await WBondBackgroundRun.ExecuteAsync(
                messages,
                "Exporting Touchstone",
                StartText(design, options, ports),
                options.Points,
                run => WBondTouchstoneExport.Export(design, options, baseNoSuffix, run),
                result => result.WrittenPaths.Count > 0
                    ? $"wrote {Path.GetFileName(result.WrittenPaths[0])}"
                    : "nothing was written",
                CancellationToken.None).ConfigureAwait(true);

            if (outcome.Cancelled) return Outcome.Silent;

            // A refusal (no declared return path, or the distributed model on an array-pair basis) is
            // the message that matters most here — it names a fault the file would otherwise have
            // carried silently. ExecuteAsync has already posted it to the Messages panel; repeating it
            // on the status line is what a host with no panel (the standalone binary) relies on.
            if (outcome.Error is { } error) return new Outcome(error, true);

            var result = outcome.Value;
            if (result is null || result.WrittenPaths.Count == 0)
                return new Outcome("Nothing was written.", true);

            // THE FILE IS THE LAST LINE, and it carries the path. Both progress rows were posted
            // before the run, so they sit above whatever follows; this is the one line that answers
            // "where is it", and in the Messages panel its path renders as a link that reveals the
            // file. Same ordering argument as the error path in WBondBackgroundRun.
            string written =
                $"Exported {Path.GetFileName(result.WrittenPaths[0])} — {ports} port(s), " +
                $"{options.Points} frequency point(s) from " +
                $"{options.StartHz * 1e-9:0.####} to {options.StopHz * 1e-9:0.####} GHz.";

            messages?.Success(written, result.WrittenPaths[0]);

            return new Outcome(written, false, Posted: messages is not null);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// The line posted the moment the export starts — before the first long piece of work, so it is
    /// genuinely immediate.
    ///
    /// <para><b>It states the predicted cost, not an adjective.</b> A distributed export of a 200-wire
    /// design is minutes; the same dialog that offered the choice already showed
    /// <see cref="WireMomCost"/>'s prediction, and repeating it here is what lets someone who walked
    /// away from the dialog still read the panel and know whether to wait.</para>
    /// </summary>
    private static string StartText(WBondDesign design, WBondTouchstoneExport.Options options, int ports)
    {
        string grid = $"{options.Points.ToString("N0", CultureInfo.CurrentCulture)} frequency point(s) " +
                      $"from {options.StartHz * 1e-9:0.####} to {options.StopHz * 1e-9:0.####} GHz, " +
                      $"written as a {ports}-port";

        if (options.Model != WBondNetworkModel.Distributed)
            return $"Touchstone export started: the lumped model over {grid}.";

        try
        {
            var settings = WBondTouchstoneExport.MomSettings(options);
            var report = WireMomMesh.Predict(design, settings);
            return $"Touchstone export started: the distributed (MoM) model over {grid}. " +
                   report.CostSummary(options.Points);
        }
        catch (Exception)
        {
            // A refusal is about to be raised by the run itself, where it carries its own remedies.
            // This line does not pre-empt it with a half-answer.
            return $"Touchstone export started: the distributed (MoM) model over {grid}.";
        }
    }

    /// <summary>
    /// Runs the distributed (MoM) model next to the lumped one and shows the two side by side
    /// (brief-wbond-mom-w2 §7.3).
    /// </summary>
    internal static async Task<Outcome> CompareDistributedModelAsync(
        Window owner, WBondDesign design, IMessageSink? messages = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(design);

        if (design.Arrays.Count == 0)
            return new Outcome("This design has no wire arrays, so it has no ports to compare.", true);

        await WBondMomCompareDialog.ShowAsync(owner, design, messages);
        return Outcome.Silent;
    }

    /// <summary>
    /// Claims the one-computation-at-a-time gate, or returns false. Public to the assembly because the
    /// Compare dialog's Run has to take the SAME gate an export takes — two runs from two surfaces are
    /// the same memory problem as two from one.
    /// </summary>
    internal static bool TryBeginRun() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    internal static void EndRun() => Interlocked.Exchange(ref _running, 0);
}
