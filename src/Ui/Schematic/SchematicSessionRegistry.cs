using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Registry of active editing sessions: exactly one <see cref="SchematicViewModel"/> per
/// absolute-normalized <c>.csch</c> path.  Framework-free; owned by <c>WorkspaceViewModel</c>.
///
/// All paths passed in are assumed to already be <c>Path.GetFullPath</c>-normalized by the caller.
/// </summary>
internal sealed class SchematicSessionRegistry
{
    private readonly Dictionary<string, SchematicViewModel> _sessions
        = new(StringComparer.OrdinalIgnoreCase);

    // Paths whose sessions currently differ from their saved baseline (UndoRedoStack.IsModified).
    // Maintained bidirectionally: added when a session becomes modified, removed when it returns to
    // its saved baseline (undo) or is saved (MarkSaved).
    private readonly HashSet<string> _dirtyPaths
        = new(StringComparer.OrdinalIgnoreCase);

    // ── Query ─────────────────────────────────────────────────────────────────

    public bool TryGet(string normalizedPath, out SchematicViewModel? vm)
        => _sessions.TryGetValue(normalizedPath, out vm);

    public bool IsDirty(string normalizedPath)
        => _dirtyPaths.Contains(normalizedPath);

    /// <summary>
    /// True when there are dirty sessions not currently referenced by any open document.
    /// </summary>
    public bool HasOrphanedDirtySession(Func<string, bool> isReferenced)
        => _dirtyPaths.Any(p => !isReferenced(p));

    /// <summary>
    /// Returns paths of dirty sessions not currently referenced by any open document.
    /// </summary>
    public IReadOnlyList<string> GetOrphanedDirtyPaths(Func<string, bool> isReferenced)
        => _dirtyPaths.Where(p => !isReferenced(p)).ToList();

    // ── Mutation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="vm"/> under <paramref name="normalizedPath"/>.
    /// <paramref name="onDirtyChanged"/> is called (with the path) whenever the session's modified
    /// state changes — on first edit, on undo back to the saved baseline, and on save — so the
    /// caller can refresh the project tree and other dirty-state consumers in either direction.
    /// </summary>
    public void Register(string normalizedPath, SchematicViewModel vm, Action<string> onDirtyChanged)
    {
        _sessions[normalizedPath] = vm;
        if (vm.UndoRedo.IsModified) _dirtyPaths.Add(normalizedPath);  // seed an already-dirty session
        vm.UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(UndoRedoStack.IsModified)) return;
            if (vm.UndoRedo.IsModified) _dirtyPaths.Add(normalizedPath);
            else                        _dirtyPaths.Remove(normalizedPath);
            onDirtyChanged(normalizedPath);
        };
    }

    /// <summary>
    /// Marks a session clean (called after its <c>.csch</c> has been written to disk): records the
    /// session's current undo position as its saved baseline.  The resulting IsModified change
    /// (true → false) fires the registration handler, which removes the path and refreshes the
    /// indicator; the explicit Remove covers a not-yet-subscribed session.
    /// </summary>
    public void MarkSaved(string normalizedPath)
    {
        if (_sessions.TryGetValue(normalizedPath, out var vm))
            vm.UndoRedo.MarkSaved();
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
        _sessions.Remove(normalizedPath);
    }

    /// <summary>Removes all sessions and dirty flags (called on workspace switch / reset).</summary>
    public void Clear()
    {
        _sessions.Clear();
        _dirtyPaths.Clear();
    }

    /// <summary>
    /// Reverse lookup: returns the registered normalized path for <paramref name="vm"/>, or <c>null</c>.
    /// Used by <c>PopOutOf</c> to find the path of a just-popped session for retirement.
    /// </summary>
    public bool TryGetPath(SchematicViewModel vm, out string? path)
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
