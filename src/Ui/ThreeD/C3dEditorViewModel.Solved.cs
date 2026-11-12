// brief-em3d-98 — which of this 3D view's setups have a current result: C3dSolveStatus's answer (the one function every surface
// reads), kept here for the setup list, the document tab's glyphs, the workspace tree row and a floating window's title.
//
// NO CHECK EVER DELAYS AN OPEN OR AN EDIT (R-em3d98-6). The document is checked in the background: after the first frame, then
// after ~500 ms of quiet following an edit, and at once when a run finishes. The one thing taken on the UI thread is the
// document's text (a copy, so the worker never reads a model the user is editing); the comparison, the hashing and the file
// reads are the worker's. A drag writes no document (brief-em3d-47), so it never triggers one.
//
// The open document is checked against its IN-MEMORY model, unsaved edits included, as the stale banner is; pushed into a
// nested view, the tab is still the TOP document's, so that is what is checked.

using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>Every (setup, solver leg) of the top document, as last checked; empty until the first answer.</summary>
    [ObservableProperty] private IReadOnlyList<SetupSolveStatus> _solveStatuses = [];

    /// <summary>What the tab's and the tree row's glyphs are drawn from: the statuses and the active setup. One instance until
    /// either changes, so a binding that reads it again sees the same object.</summary>
    public SolveBadgeSet SolveBadges => _solveBadges ??= new(SolveStatuses, BadgeActiveSetup);

    private SolveBadgeSet? _solveBadges;

    /// <summary>Raised on the UI thread each time a check answers (the shell follows it onto the tree row).</summary>
    public event Action? SolveStatusesChecked;

    /// <summary>The ~500 ms of quiet an edit waits for before the document is checked.</summary>
    public const int SolveStatusQuietMs = 500;

    private CancellationTokenSource? _solveCts;

    /// <summary>The top document: what the tab is, whatever frame is being edited.</summary>
    private C3dDocument TopDocument => _frames.Count > 0 ? _frames[0].Document : Document;

    /// <summary>The active setup the glyphs are drawn against: the editor's at the top; pushed in, the top document's own
    /// (R-em3d98-3); none while an external <c>.cem</c> is active, since no embedded setup is.</summary>
    private string? BadgeActiveSetup
        => CanPopOut ? TopDocument.ActiveSetup ?? C3dSetups.Read(TopDocument).FirstOrDefault(s => s.Refusal is null)?.Name
         : IsExternalActive ? null : ActiveSetupName;

    /// <summary>Checks the document after <see cref="SolveStatusQuietMs"/> of quiet: a later call restarts the wait.</summary>
    public void ScheduleSolveStatus() => StartSolveStatus(SolveStatusQuietMs);

    /// <summary>Checks the document now (still off the UI thread): a run finished.</summary>
    public void RefreshSolveStatusNow() => StartSolveStatus(0);

    private void StartSolveStatus(int delayMs)
    {
        if (IsScratch || ResultsRootProvider?.Invoke() is not { } root) return;
        _solveCts?.Cancel();
        var cts = _solveCts = new CancellationTokenSource();
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                // Never mid-resize or mid-move: the snapshot is UI-thread work, and the answer redraws the tab (owner, 2026-10-02).
                await Controls.WindowMotion.WaitForQuietAsync(ct).ConfigureAwait(false);
                // The text on the UI thread (the model is the UI's), then everything else here.
                var taken = new TaskCompletionSource<(string Text, string Path)?>();
                _post(() =>
                {
                    try { taken.TrySetResult(ct.IsCancellationRequested ? null : (C3dPersistence.Serialize(TopDocument), RunPath)); }
                    catch (Exception e) { taken.TrySetException(e); }
                });
                if (await taken.Task.ConfigureAwait(false) is not { } snapshot) return;
                var rows = C3dSolveStatus.Of(C3dPersistence.Deserialize(snapshot.Text), snapshot.Path, root, ct);
                await Controls.WindowMotion.WaitForQuietAsync(ct).ConfigureAwait(false);
                _post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    // The same answer as last time (most checks) changes nothing: no tab, tree row or title is touched.
                    if (rows.SequenceEqual(SolveStatuses)) return;
                    SolveStatuses = rows;
                    if (!CanPopOut) foreach (var item in SetupItems) item.ApplySolveStatuses(rows);
                    SolveStatusesChecked?.Invoke();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or C3dReadException) { /* the next edit asks again */ }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Of setup <paramref name="setupName"/> now, synchronously — the confirmation before a re-run must not be skipped because
    /// the background check has not answered yet (R-em3d98-7). One setup's check.
    /// </summary>
    public IReadOnlyList<SetupSolveStatus> SolveStatusOf(string setupName)
        => ResultsRootProvider?.Invoke() is { } root
            ? C3dSolveStatus.OfSetup(TopDocument, RunPath, root, setupName) : [];

    /// <summary>
    /// R-em3d98-3 — the active setup is part of the document: written only when it is not the first setup (so no existing file
    /// changes), and choosing one marks the document dirty, as any saved setting does — never an undo entry, and display state,
    /// so it never makes a result stale (SerializeForRun leaves it out).
    /// </summary>
    private void KeepActiveSetupInDocument(bool markDirty = true)
    {
        if (IsExternalActive) return;
        string? first = C3dSetups.Read(Document).FirstOrDefault(s => s.Refusal is null)?.Name;
        string? stored = ActiveSetupName == first ? null : ActiveSetupName;
        if (Document.ActiveSetup == stored) return;
        Document.ActiveSetup = stored;
        if (!markDirty) return;
        _preferenceDirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnSolveStatusesChanged(IReadOnlyList<SetupSolveStatus> value)
    {
        _solveBadges = null;
        OnPropertyChanged(nameof(SolveBadges));
        OnPropertyChanged(nameof(SolveTitleSuffix));
    }

    /// <summary>The badges in words, for a floating window's title (R-em3d98-5 item 4): <c>solved (FEM, thermal)</c>; null
    /// when no kind's glyph is solid filled.</summary>
    public string? SolveTitleSuffix => SolveBadgeRules.TitleSuffix(SolveBadges);

    /// <summary>The active setup changed: the glyphs are drawn against another setup.</summary>
    private void ActiveSetupBadgesChanged()
    {
        _solveBadges = null;
        OnPropertyChanged(nameof(SolveBadges));
        OnPropertyChanged(nameof(SolveTitleSuffix));
    }
}
