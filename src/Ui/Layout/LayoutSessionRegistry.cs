using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Registry of active layout editing sessions: exactly one <see cref="LayoutEditorViewModel"/> per
/// absolute-normalized <c>.clay</c> path. Mirrors <c>CircuitRF.Ui.Schematic.SchematicSessionRegistry</c>
/// exactly (brief-L3b-hierarchy-navigation.md §1: "reuse the schematic's nav-frame model") — same
/// get-or-create-from-disk funnel for both "open as tab" and "push in," so a cell simultaneously open
/// as its own tab and pushed into elsewhere is the SAME session instance and edits stay coherent
/// everywhere. Framework-free; owned by <c>WorkspaceViewModel</c>.
///
/// All paths passed in are assumed to already be <c>Path.GetFullPath</c>-normalized by the caller.
/// </summary>
internal sealed class LayoutSessionRegistry
{
    private readonly Dictionary<string, LayoutEditorViewModel> _sessions
        = new(StringComparer.OrdinalIgnoreCase);

    // Paths whose sessions currently differ from their saved baseline (LayoutEditorViewModel.IsDirty,
    // which combines both undo-stack modification AND preference edits — see that property's own doc
    // comment). Maintained bidirectionally: added when a session becomes dirty, removed when it
    // returns to clean (undo back to saved baseline) or is saved (MarkSaved).
    private readonly HashSet<string> _dirtyPaths
        = new(StringComparer.OrdinalIgnoreCase);

    // One dirty-state subscription per SESSION, not one per Register call — see the schematic
    // registry's own field of the same name for why a captured path is the wrong thing to hold.
    private readonly Dictionary<LayoutEditorViewModel, System.ComponentModel.PropertyChangedEventHandler> _hooks
        = new();

    // ── Query ─────────────────────────────────────────────────────────────────

    public bool TryGet(string normalizedPath, out LayoutEditorViewModel? vm)
        => _sessions.TryGetValue(normalizedPath, out vm);

    public bool IsDirty(string normalizedPath)
        => _dirtyPaths.Contains(normalizedPath);

    /// <summary>True when there are dirty sessions not currently referenced by any open document.</summary>
    public bool HasOrphanedDirtySession(Func<string, bool> isReferenced)
        => _dirtyPaths.Any(p => !isReferenced(p));

    /// <summary>Returns paths of dirty sessions not currently referenced by any open document.</summary>
    public IReadOnlyList<string> GetOrphanedDirtyPaths(Func<string, bool> isReferenced)
        => _dirtyPaths.Where(p => !isReferenced(p)).ToList();

    // ── Mutation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="vm"/> under <paramref name="normalizedPath"/>.
    /// <paramref name="onDirtyChanged"/> is called (with the path) whenever the session's dirty
    /// state changes — on first edit, on undo back to the saved baseline, and on save — so the
    /// caller can refresh the project tree and other dirty-state consumers in either direction.
    /// </summary>
    public void Register(string normalizedPath, LayoutEditorViewModel vm, Action<string> onDirtyChanged)
    {
        // A session answers to exactly ONE path, so re-registering a live session — which is what a
        // Save As does — must retire the path it used to answer to. Leaving the old key bound to the
        // same VM makes a later open of that path hand back THIS document's session, so the two tabs
        // render one model (the schematic form of this was the reported bug; layout had it too).
        foreach (var stale in PathsOf(vm))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(stale, normalizedPath)) continue;
            _sessions.Remove(stale);
            _dirtyPaths.Remove(stale);
        }

        // A DIFFERENT session taking over this path (a scratch document materialised onto a path a
        // retired session still held) drops the displaced one's hook the same way.
        if (_sessions.TryGetValue(normalizedPath, out var displaced) && !ReferenceEquals(displaced, vm))
            Unbind(normalizedPath);

        _sessions[normalizedPath] = vm;
        if (vm.IsDirty) _dirtyPaths.Add(normalizedPath);  // seed an already-dirty session
        else            _dirtyPaths.Remove(normalizedPath);

        if (_hooks.ContainsKey(vm)) return;   // already subscribed — one hook per session, see _hooks
        System.ComponentModel.PropertyChangedEventHandler hook = (_, e) =>
        {
            if (e.PropertyName is not nameof(LayoutEditorViewModel.IsDirty)) return;
            if (!TryGetPath(vm, out var path) || path is null) return;  // retired or discarded
            if (vm.IsDirty) _dirtyPaths.Add(path);
            else            _dirtyPaths.Remove(path);
            onDirtyChanged(path);
        };
        _hooks[vm] = hook;
        vm.PropertyChanged += hook;
    }

    /// <summary>Every path currently bound to <paramref name="vm"/> (normally zero or one).</summary>
    private List<string> PathsOf(LayoutEditorViewModel vm)
        => _sessions.Where(kv => ReferenceEquals(kv.Value, vm)).Select(kv => kv.Key).ToList();

    /// <summary>Unbinds one path, detaching the session's dirty hook once nothing refers to it.</summary>
    private void Unbind(string normalizedPath)
    {
        if (!_sessions.Remove(normalizedPath, out var vm)) return;
        _dirtyPaths.Remove(normalizedPath);
        if (PathsOf(vm).Count == 0 && _hooks.Remove(vm, out var hook))
            vm.PropertyChanged -= hook;
    }

    /// <summary>
    /// Marks a session clean (called after its <c>.clay</c> has been written to disk): records the
    /// session's current state as its saved baseline. The resulting IsDirty change (true → false)
    /// fires the registration handler, which removes the path and refreshes the indicator; the
    /// explicit Remove covers a not-yet-subscribed session.
    /// </summary>
    public void MarkSaved(string normalizedPath)
    {
        if (_sessions.TryGetValue(normalizedPath, out var vm))
            vm.MarkSaved();
        _dirtyPaths.Remove(normalizedPath);
    }

    /// <summary>
    /// Removes the session if it is clean AND not referenced by any open document.
    /// Dirty sessions are never retired — they stay alive until explicitly saved.
    /// </summary>
    /// <param name="isReferenced">
    /// Returns <c>true</c> when at least one open document wraps the session at the given path.
    /// </param>
    public void RetireIfUnreferenced(string normalizedPath, Func<string, bool> isReferenced)
    {
        if (_dirtyPaths.Contains(normalizedPath)) return;
        if (isReferenced(normalizedPath)) return;
        Unbind(normalizedPath);
    }

    /// <summary>
    /// Drops a session and its unsaved state, when nothing open still refers to it.
    ///
    /// <para><b>Unlike <see cref="RetireIfUnreferenced"/> this DISCARDS unsaved work, so it may only
    /// be called where the user has already been asked and declined to save.</b> That is leaving a
    /// workspace: the prompt has happened, the answer was Don't Save, and the documents are being
    /// closed. Keeping the dirty flag past that point makes the NEXT workspace open prompt to save a
    /// document belonging to a workspace that is gone.</para>
    ///
    /// <para>Still guarded on being unreferenced, because a torn-off document from this workspace can
    /// legitimately survive the switch — its session is still live and is not ours to discard.</para>
    /// </summary>
    public void DiscardIfUnreferenced(string normalizedPath, Func<string, bool> isReferenced)
    {
        if (isReferenced(normalizedPath)) return;
        Unbind(normalizedPath);
    }

    /// <summary>Removes all sessions and dirty flags (called on workspace switch / reset).</summary>
    public void Clear()
    {
        foreach (var (vm, hook) in _hooks) vm.PropertyChanged -= hook;
        _hooks.Clear();
        _sessions.Clear();
        _dirtyPaths.Clear();
    }

    /// <summary>
    /// Reverse lookup: returns the registered normalized path for <paramref name="vm"/>, or <c>null</c>.
    /// Used by <c>PopOutOf</c> to find the path of a just-popped session for retirement.
    /// </summary>
    public bool TryGetPath(LayoutEditorViewModel vm, out string? path)
    {
        foreach (var kvp in _sessions)
        {
            if (ReferenceEquals(kvp.Value, vm))
            {
                path = kvp.Key;
                return true;
            }
        }
        path = null;
        return false;
    }

    // ── Diagnostics (internal — test / WorkspaceViewModel access) ─────────────

    internal int Count          => _sessions.Count;
    internal int DirtyCount     => _dirtyPaths.Count;
    internal IEnumerable<string> AllPaths      => _sessions.Keys;
    internal IEnumerable<string> AllDirtyPaths => _dirtyPaths;
}
