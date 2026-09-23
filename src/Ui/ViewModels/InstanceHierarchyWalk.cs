using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One placement descended through — its listed name, and its index in its parent's list
/// as a hint for finding it again (the name decides; the index only breaks a tie between two
/// placements listed under the same name).</summary>
public sealed record InstancePathStep(string Name, int Index);

/// <summary>
/// The <see cref="InstanceRow.Source"/> of a row found INSIDE a placed cell: the placements descended
/// through from the listed frame, outermost first, and the instance itself. Names, never objects —
/// the walk read those cells off the UI thread, and the objects a push-in shows are different ones.
/// </summary>
public sealed record SubCellInstance(IReadOnlyList<InstancePathStep> Chain, InstancePathStep Leaf)
{
    /// <summary>The dotted path the panel lists — <c>X1.X3.R5</c>.</summary>
    public string DottedPath => string.Join(".", [.. System.Linq.Enumerable.Select(Chain, s => s.Name), Leaf.Name]);
}

/// <summary>
/// The Instances panel's "Include sub-cells" search: every placement below the listed frame, found by
/// walking the cell hierarchy <b>off the UI thread</b>.
///
/// <para><b>What the UI thread does, and what it does not.</b> It reads the listed frame and every
/// OPEN session into <see cref="Level"/>s — names, types and cell references copied out of the live
/// models, no filesystem at all. Everything else, resolving references and reading the cells nobody
/// has open, is the background walk's. The walk never touches a live model: an open session's
/// unsaved edits reach it only through the copy, so the user editing while it runs cannot race it.</para>
///
/// <para><b>Bounded three ways.</b> A cell that contains itself is walked once per path, not forever;
/// the walk stops <see cref="MaxDepth"/> levels down; and it stops after <see cref="MaxRows"/> rows —
/// a 1,000-part cell placed 1,000 times is a million rows nobody can read, and the list says it
/// stopped rather than pretending that was everything.</para>
/// </summary>
internal static class InstanceHierarchyWalk
{
    public const int MaxRows  = 100_000;
    public const int MaxDepth = 32;

    internal enum LevelKind { Schematic, Layout }

    /// <summary>One placement of a level: what its row shows, where it sits in its parent's list, and
    /// — for a placement the walk may descend into — the reference to follow.</summary>
    internal sealed record Entry(string Name, string Type, string Detail, int Index, string? CellRef);

    /// <summary>One cell's placements. <see cref="BaseDir"/> is what their references resolve against.</summary>
    internal sealed record Level(LevelKind Kind, string? BaseDir, Entry[] Entries);

    internal sealed record Result(InstanceRow[] Rows, bool Truncated);

    // ── Levels (UI thread for a live model; the walk's thread for a cell read from disk) ────────

    /// <summary>
    /// A schematic's placements. <b>Ground, VAR and MEAS are left out</b> (R-fi-4): they are not what
    /// anyone is looking for, and dozens of grounds would bury the parts. A kit part is listed but
    /// never descended into — it has no schematic of its own.
    /// </summary>
    public static Level Of(SchematicEditModel model)
    {
        var comps   = model.Components;
        var entries = new List<Entry>(comps.Count);

        for (int i = 0; i < comps.Count; i++)
        {
            var c = comps[i];
            if (!InstanceListViewModel.IsListed(c.Symbol)) continue;

            string type, detail = "";
            string? descend = null;
            if (PdkKitRegistry.TryParse(c.CellRef, out string kit, out string part))
            {
                type   = part;
                detail = kit;
            }
            else
            {
                type = c.TypeLabelText();
                if (c.CellRef is { Length: > 0 } cellRef)
                {
                    descend = cellRef;
                    string spelled = cellRef.Replace('\\', '/').TrimEnd('/');
                    if (!string.Equals(spelled, type, StringComparison.Ordinal)) detail = spelled;
                }
                else if (c.Footprint is { Length: > 0 } fp)
                    detail = fp;
            }

            entries.Add(new Entry(NameOf(c), type, detail, i, descend));
        }

        return new Level(LevelKind.Schematic, model.SchematicDirectory, [.. entries]);
    }

    /// <summary>The name a schematic row lists — and the name a push-in finds it by again.</summary>
    public static string NameOf(EditableComponent c) => c.InstanceName.Length > 0 ? c.InstanceName : "(unnamed)";

    /// <summary>
    /// A layout's placements. The name is <see cref="LayoutInstance.DisplayRefDes"/> — never
    /// <c>RefDes</c>, whose own comment says why — and the cell's name for an unnamed placement. A
    /// parametric cell is listed but never descended into: its geometry is generated, and push-in
    /// refuses it for the same reason.
    /// </summary>
    public static Level Of(LayoutView model, string? baseDir)
    {
        var insts   = model.Instances;
        var entries = new Entry[insts.Count];

        for (int i = 0; i < insts.Count; i++)
        {
            var inst    = insts[i];
            string cell = CellName(inst.CellRef);

            string? generator = model.PCellSnapshots.TryGetValue(cell, out var snap) ? snap.GeneratorId : null;
            string? partKind  = LayoutPartKind.Of(inst) is { } kind ? ComponentTypeRegistry.DisplayName(kind) : null;
            string type       = generator ?? partKind ?? cell;

            string detail = string.Equals(type, cell, StringComparison.Ordinal) ? "" : cell;
            if (inst.Rows * inst.Cols > 1)
                detail = (detail.Length > 0 ? detail + " · " : "") + $"{inst.Rows}×{inst.Cols} array";

            string? descend = generator is null && inst.CellRef is { Length: > 0 } r ? r : null;
            entries[i] = new Entry(NameOf(inst), type, detail, i, descend);
        }

        return new Level(LevelKind.Layout, baseDir, entries);
    }

    /// <summary>The name a layout row lists — and the name a push-in finds it by again.</summary>
    public static string NameOf(LayoutInstance inst) => inst.DisplayRefDes ?? CellName(inst.CellRef);

    private static string CellName(string cellRef)
    {
        string trimmed = cellRef.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    // ── The walk (background thread) ──────────────────────────────────────────

    /// <summary>
    /// Every placement below <paramref name="root"/>'s cell placements, as rows named by dotted path
    /// (<c>X1.X3.R5</c>). The root's own placements are NOT included — the caller already lists them.
    /// <paramref name="live"/> holds a copy of every open session, by absolute path; any other cell
    /// is read from disk through <paramref name="cache"/>.
    /// </summary>
    public static Result Walk(Level root, string? rootPath, object sourceViewModel,
                              IReadOnlyDictionary<string, Level> live, DiskLevelCache cache,
                              CancellationToken cancellation)
    {
        var walk = new Walker(sourceViewModel, live, cache, cancellation);
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootPath is { Length: > 0 }) ancestors.Add(Path.GetFullPath(rootPath));

        walk.Descend(root, "", [], ancestors, depth: 0);
        return new Result([.. walk.Rows], walk.Truncated);
    }

    private sealed class Walker(object sourceVm, IReadOnlyDictionary<string, Level> live,
                                DiskLevelCache cache, CancellationToken cancellation)
    {
        public readonly List<InstanceRow> Rows = [];
        public bool Truncated;

        // One cell is usually placed many times; its reference is resolved once per parent folder.
        private readonly Dictionary<(LevelKind, string, string), string?> _paths = [];

        public void Descend(Level level, string prefix, List<InstancePathStep> chain,
                            HashSet<string> ancestors, int depth)
        {
            foreach (var entry in level.Entries)
            {
                if (entry.CellRef is null || level.BaseDir is null) continue;
                cancellation.ThrowIfCancellationRequested();

                if (PathOf(level.Kind, entry.CellRef, level.BaseDir) is not { } path) continue;
                if (ancestors.Contains(path)) continue;   // a cell that contains itself
                if (LevelAt(level.Kind, path, entry.CellRef, level.BaseDir) is not { } child) continue;

                chain.Add(new InstancePathStep(entry.Name, entry.Index));
                string childPrefix = prefix + entry.Name + ".";
                InstancePathStep[] steps = [.. chain];

                foreach (var e in child.Entries)
                {
                    if (Rows.Count >= MaxRows) { Truncated = true; return; }
                    Rows.Add(new InstanceRow(childPrefix + e.Name, e.Type, e.Detail,
                                             new SubCellInstance(steps, new InstancePathStep(e.Name, e.Index)),
                                             sourceVm));
                }

                if (depth + 1 < MaxDepth)
                {
                    ancestors.Add(path);
                    Descend(child, childPrefix, chain, ancestors, depth + 1);
                    ancestors.Remove(path);
                }

                chain.RemoveAt(chain.Count - 1);
                if (Truncated) return;
            }
        }

        private string? PathOf(LevelKind kind, string cellRef, string baseDir)
        {
            var key = (kind, cellRef, baseDir);
            if (_paths.TryGetValue(key, out var known)) return known;

            string? path = null;
            try
            {
                path = kind == LevelKind.Schematic
                    ? HierarchyResolver.ResolvePrimaryPath(cellRef, baseDir)
                    : LayoutHierarchyResolver.ResolvePrimaryPath(cellRef, baseDir);
                if (path is not null) path = Path.GetFullPath(path);
            }
            catch { path = null; }   // an unreadable folder is a cell with nothing to list

            return _paths[key] = path;
        }

        private Level? LevelAt(LevelKind kind, string path, string cellRef, string baseDir)
            => live.TryGetValue(path, out var open) ? open : cache.Get(kind, path, cellRef, baseDir);
    }
}

/// <summary>
/// The cells the sub-cell search read from disk, kept between walks and re-read only when the file
/// changes — so the rebuild after an edit to the listed frame re-walks names in memory rather than
/// re-reading every cell. Safe to share between a walk still running and the next one.
/// </summary>
internal sealed class DiskLevelCache
{
    private readonly ConcurrentDictionary<string, (DateTime Mtime, InstanceHierarchyWalk.Level? Level)> _levels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many cells have been read from disk — the counter the reuse is gated on.</summary>
    public int Reads => _reads;
    private int _reads;

    public InstanceHierarchyWalk.Level? Get(InstanceHierarchyWalk.LevelKind kind, string path, string cellRef, string baseDir)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); }
        catch { return null; }

        if (_levels.TryGetValue(path, out var hit) && hit.Mtime == mtime) return hit.Level;

        Interlocked.Increment(ref _reads);
        var level = Read(kind, path, cellRef, baseDir);
        _levels[path] = (mtime, level);
        return level;
    }

    private static InstanceHierarchyWalk.Level? Read(InstanceHierarchyWalk.LevelKind kind, string path,
                                                     string cellRef, string baseDir)
    {
        try
        {
            if (kind == InstanceHierarchyWalk.LevelKind.Schematic)
                return InstanceHierarchyWalk.Of(SchematicPersistence.LoadFromFile(path).model);

            // Through the resolver, for its cache: a cell the open layout draws has already been read.
            // It hands back an open session's live model when one exists — the walk's own copy of every
            // open session is checked before this is reached, so that is only a session opened since,
            // and reading it is what the catch below is for.
            var view = CellLayoutResolver.Resolve(cellRef, baseDir).View;
            if (view is null || view.PCellOrigin is not null) return null;
            return InstanceHierarchyWalk.Of(view, Path.GetDirectoryName(path));
        }
        catch
        {
            return null;   // unreadable, or changed under the walk: nothing to list inside it
        }
    }
}
