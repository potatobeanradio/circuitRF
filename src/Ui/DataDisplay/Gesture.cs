// ================================================================
//  Gesture.cs — what the USER did, in the crash trail, one line per command.
//
//  Six rounds of a field report (src/RfCore/RESOLVED.md) have failed to reproduce a trace-resolve
//  crash, and the reason is not that the state was unrecorded — by round 6 the note carries the cube,
//  its buffer, the slice, the branch-selecting trace state, the group inventory, the stack, the
//  faulting index and a replay. What is missing is the GESTURE. One of the reported trails shows three
//  identical failures five seconds apart: someone retrying something, and nothing anywhere says what.
//
//  So the Data Display's commands announce themselves. A command is a click, so the trail grows at
//  human pace, and the whole point of the reporter's autoflush is that the last line survives a death
//  the process never gets to report.
// ================================================================

using System;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// Breadcrumbs for user-initiated Data Display commands.
///
/// <para><b>No coalescing, no ring buffer, on purpose.</b> Both were considered. A ring buffer
/// flushed on failure would keep the trail clean, but it loses everything in exactly the deaths the
/// session file exists for — a native fault or the OOM killer, where nothing gets to flush it. Rate
/// limiting would mean a diagnostic with state of its own that can be wrong. Every command here is a
/// discrete click with no auto-repeat, so the simple thing is also the correct one.</para>
///
/// <para><b>Only commands.</b> Continuous gestures — marker drags, pan, zoom-by-wheel — are not
/// routed through here: they fire per frame, they would drown the trail, and they cannot change what
/// a trace READS, which is the surface this exists to describe.</para>
/// </summary>
internal static class Gesture
{
    // Deliberately NOT in a `CircuitRF.Ui.DataDisplay.Diagnostics` namespace, however natural that
    // reads. Every file under `CircuitRF.Ui.DataDisplay.ViewModels` refers to the reporter as
    // `Diagnostics.CrashReporter`, and a nested `Diagnostics` namespace under `DataDisplay` wins that
    // lookup over `CircuitRF.Ui.Diagnostics` — so creating one breaks five unrelated files with a
    // "CrashReporter does not exist in the namespace" that names the wrong namespace.

    /// <summary>One breadcrumb. The <c>dd:</c> prefix is what makes the gestures greppable out of a
    /// trail that also carries run progress and resolve failures.</summary>
    public static void Note(string what) => Ui.Diagnostics.CrashReporter.Note($"dd: {what}");

    /// <summary>One breadcrumb naming what it acted on — a trace spec, a source, a plot.</summary>
    public static void Note(string what, string? target)
        => Ui.Diagnostics.CrashReporter.Note(string.IsNullOrEmpty(target) ? $"dd: {what}" : $"dd: {what} — {target}");

    /// <summary>
    /// A <see cref="RelayCommand"/> that records itself before it runs.
    ///
    /// <para>Returns the concrete type rather than <c>ICommand</c> because callers reach back for
    /// <see cref="RelayCommand.NotifyCanExecuteChanged"/>; a factory that widened the type would move
    /// the churn into every one of those instead.</para>
    /// </summary>
    public static RelayCommand Command(string what, Action execute)
        => new(() => { Note(what); execute(); });

    /// <inheritdoc cref="Command(string, Action)"/>
    public static RelayCommand Command(string what, Action execute, Func<bool> canExecute)
        => new(() => { Note(what); execute(); }, canExecute);

    /// <summary>As <see cref="Command(string, Action)"/>, with the target resolved AT CLICK TIME —
    /// a trace's spec or a source's name is only meaningful as it was when the button was pressed.</summary>
    public static RelayCommand Command(string what, Func<string?> target, Action execute)
        => new(() => { Note(what, Safe(target)); execute(); });

    /// <inheritdoc cref="Command(string, Func{string}, Action)"/>
    public static RelayCommand Command(string what, Func<string?> target, Action execute, Func<bool> canExecute)
        => new(() => { Note(what, Safe(target)); execute(); }, canExecute);

    private static string? Safe(Func<string?> target)
    {
        try { return target(); } catch { return "(target unreadable)"; }
    }
}
