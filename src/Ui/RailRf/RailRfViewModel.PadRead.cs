// The board's PADS follow the layout (docs/sonnet-briefs/brief-railrf-28-pads-follow-the-layout.md).
//
// ── WHAT WAS WRONG ──────────────────────────────────────────────────────────────────────────────
//
// NotifyArtworkChanged re-flattened the shapes on every edit next door and never re-read the pads,
// so a footprint moved, turned, added or deleted in the layout while railRF was open left the net
// points, the pick list and the turned-parts note describing the placements as they were when the
// board was opened. Turning a part by hand — the natural answer to the turned-parts note — was the
// commonest way in: the note went on naming a part the user had already fixed.
//
// ── WHY IT IS DEBOUNCED AND OFF THE UI THREAD ───────────────────────────────────────────────────
//
// The pad funnel (RailArtwork.PadsFor) builds a galvanic partition, and one per keystroke is the
// shape R-rail19-2c forbids. A drag is a burst of edits, so the read waits for the burst to SETTLE
// and then runs once, off the UI thread, on a copy of the placements taken when it STARTS. An edit
// arriving after that supersedes it and its answer is dropped — BeginCopperJob's contract, held by
// the same means (the CTS it was started under is no longer the current one).
//
// ── WHICH EDITS ─────────────────────────────────────────────────────────────────────────────────
//
// Only something that can move a PIN. Every instance command raises InstancesOnly, and so does the
// workspace when a REFERENCED cell changes (its lands moved with no instance field changing), so
// that kind always re-reads. The shape kinds (Appended, RemovedTrailing, Updated) change copper and
// not pins; they re-read only when a stamped Net on the root changed, which renames the pads on the
// stamped path. Full is ambiguous — a shape-only delete and a field edit raise it as readily as a
// mixed delete — so it is settled by comparing the placements and stamps against the last read.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>How long an edit burst has to be quiet before the pads are re-read.</summary>
    internal static readonly TimeSpan PadReadSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How the read waits for the burst to settle. <c>Task.Delay</c> in the application; a test that
    /// wants to decide when the burst is over replaces it.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> SettlePadRead { get; set; } =
        static (delay, token) => Task.Delay(delay, token);

    /// <summary>The settle-and-read in flight, or the last one. <b>Awaitable</b>: where
    /// <see cref="PostToUi"/> is inline it completes when the answer has been published or dropped,
    /// so a test waits for it rather than sleeping.</summary>
    internal Task? PadRead { get; private set; }

    /// <summary>How many times the pad funnel has been run by this window after the board was opened —
    /// <b>counted so the debounce is testable without timing anything</b>, for
    /// <see cref="NetWalksPerformed"/>' reason.</summary>
    internal int PadReadsPerformed { get; private set; }

    /// <summary>
    /// True from the edit that moved a pin until the re-read lands — <b>what the strip says</b>, and
    /// what holds the Turn button, whose list may name a part the user has just turned by hand.
    /// </summary>
    [ObservableProperty]
    private bool _isReadingParts;

    partial void OnIsReadingPartsChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusLine));
        TurnPartsCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource? _padReadCts;

    /// <summary>What the pads were last read from — see <see cref="PinSignature"/>.</summary>
    private PinSignature _padsReadFrom = PinSignature.None;

    /// <summary>The net that was selected when the burst began. <see cref="InvalidateNetWalks"/>
    /// clears the selection on every edit, and R-rail28-2 says a pad refresh must hand it back when
    /// the net still exists.</summary>
    private string? _netAcrossPadRead;

    /// <summary>
    /// The placements and root stamps the pad funnel reads, as one comparable value.
    /// </summary>
    /// <remarks>
    /// <b>Text, not a hash</b>: a collision would be a pad read silently skipped, and the string is a
    /// few kilobytes on a board with hundreds of parts — far less than the flatten every edit already
    /// pays for. The instance fields are the ones <see cref="LayoutGeometry.Clone(LayoutInstance)"/>
    /// copies that place a land; the stamps are by index because a stamp moved to another shape
    /// renames different pads.
    /// </remarks>
    private readonly record struct PinSignature(int Instances, string Text)
    {
        public static readonly PinSignature None = new(0, "");

        public static PinSignature Of(LayoutView? view)
        {
            if (view is null) return None;

            var sb = new StringBuilder();
            foreach (var i in view.Instances)
                sb.Append(i.CellRef).Append('|').Append(i.X).Append(',').Append(i.Y).Append(',')
                  .Append(i.RotationDegrees).Append(',').Append(i.MirrorX).Append(',').Append(i.Mag).Append(',')
                  .Append(i.Rows).Append('x').Append(i.Cols).Append(',').Append(i.PitchX).Append(',').Append(i.PitchY)
                  .Append('|').Append(i.SchematicId).Append('|').Append(i.RefDes).Append('\n');

            sb.Append('#');
            for (int s = 0; s < view.Shapes.Count; s++)
                if (view.Shapes[s].Net is { Length: > 0 } net) sb.Append(s).Append('=').Append(net).Append('\n');

            return new PinSignature(view.Instances.Count, sb.ToString());
        }
    }

    /// <summary>
    /// Whether <paramref name="change"/> can have moved a pin on <paramref name="live"/> — the file
    /// header's rule, in order.
    /// </summary>
    private bool PinsMayHaveMoved(LayoutView live, LayoutChangeInfo? change)
    {
        // R-rail28-2: a board with no instances, before and after, has no pad to re-read, and the
        // stamped-net partition is not a cost to pay for nothing.
        if (live.Instances.Count == 0 && _padsReadFrom.Instances == 0) return false;

        if (change?.Kind == LayoutChangeKind.InstancesChanged) return true;

        return PinSignature.Of(live) != _padsReadFrom;
    }

    /// <summary>
    /// Starts (or restarts) the settle-then-read — every edit in a burst lands here, and only the
    /// last one's read runs.
    /// </summary>
    private void SchedulePadRead()
    {
        _padReadCts?.Cancel();
        _padReadCts?.Dispose();

        var cts = new CancellationTokenSource();
        _padReadCts = cts;
        IsReadingParts = true;
        PadRead = SettleThenReadPads(cts, cts.Token);
    }

    /// <summary>Abandons whatever pad read is pending — a new board, or a synchronous refresh that
    /// has just answered the same question.</summary>
    private void CancelPadRead()
    {
        _padReadCts?.Cancel();
        _padReadCts?.Dispose();
        _padReadCts = null;
        IsReadingParts = false;
    }

    private async Task SettleThenReadPads(CancellationTokenSource cts, CancellationToken token)
    {
        try { await SettlePadRead(PadReadSettle, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;

        // Back to the UI thread to START, because what the job reads is captured there.
        var begun = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(() => begun.SetResult(BeginPadRead(cts)));
        if (await begun.Task.ConfigureAwait(false) is { } job) await job.ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the board as it is NOW and runs the pad funnel on the copy, off the UI thread.
    /// Returns the job, or null where there is nothing to read.
    /// </summary>
    private Task? BeginPadRead(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_padReadCts, cts)) return null;                    // superseded while settling
        if (Board is not { View: { } live } board) { CancelPadRead(); return null; }

        // A COPY of the placements, not the live model: the layout editor goes on mutating that one
        // while this runs, and a list edited under an enumeration throws. The instances are cloned
        // because a move drag mutates them in place; the root's shapes are the same objects, which
        // the funnel only reads the stamped Net off.
        var view = new LayoutView { DbuPerMicron = live.DbuPerMicron, TechRef = live.TechRef, DisplayUnit = live.DisplayUnit };
        view.Shapes.AddRange(live.Shapes);
        foreach (var inst in live.Instances) view.Instances.Add(LayoutGeometry.Clone(inst));

        // NotifyArtworkChanged keeps the flatten current on every edit, so it is taken as it stands.
        // Copied only where it IS the live list, which is what FlattenedShapes hands back for a board
        // with no instances.
        IReadOnlyList<LayoutShape> shapes = ReferenceEquals(board.Shapes, live.Shapes)
            ? live.Shapes.ToArray()
            : board.Shapes;
        var signature = PinSignature.Of(live);
        var tech = board.Technology;
        string? clay = board.ArtworkCellRef;
        var netlist = BoardNetlist;

        return ReadCopperOffThread(() =>
        {
            RailArtwork.RailPadResolution? resolved = null;
            try { resolved = RailArtwork.PadsFor(view, clay, tech, netlist, null, shapes); }
            catch (Exception) { /* dropped below: the board keeps the reading it had */ }

            PostToUi(() => FinishPadRead(cts, live, tech, shapes, signature, resolved));
        });
    }

    /// <summary>Publishes one pad read, unless it has been superseded.</summary>
    private void FinishPadRead(
        CancellationTokenSource cts, LayoutView readFrom, Technology tech, IReadOnlyList<LayoutShape> shapes,
        PinSignature signature, RailArtwork.RailPadResolution? resolved)
    {
        // An edit after the job started is a newer model, and its own read is already settling.
        if (!ReferenceEquals(_padReadCts, cts)) return;

        PadReadsPerformed++;
        CancelPadRead();

        if (Board is not { } board || !ReferenceEquals(board.View, readFrom) || resolved is null)
        {
            _netAcrossPadRead = null;
            return;
        }

        // The technology was re-adopted while this ran, so these pads were read against the old
        // stackup's connectivity. Read again rather than publish them.
        if (!ReferenceEquals(board.Technology, tech)) { SchedulePadRead(); return; }

        RefreshBoardPads(board, shapes, resolved, signature);
    }
}
