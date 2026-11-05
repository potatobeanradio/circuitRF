// ================================================================
//  DataSourceLibraryViewModel.Published.cs  —  in-memory results under
//  a source's own path, frame coalescing, and snapshots
//  (brief-tuneopt-3 R-to3-5, R-to3-6, R-to3-8, R-to3-9)
//
//  data-display.md §2.2: a source is addressed by PATH. A live tuning
//  session or an optimizer publishes the DataSet it just computed under
//  the schematic's own results path, and every trace bound to that path
//  re-resolves against it with no change to the trace — the path stays
//  the identity, only the bytes come from memory. A publication lasts
//  until the session drops it or the file changes on disk.
//
//  Frames are COALESCED: a publication that arrives while the display is
//  still drawing the previous one replaces whatever was waiting, so the
//  display redraws once more, for the newest, and the intermediates are
//  counted as skipped.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataSourceLibraryViewModel
{
    /// <summary>The chip a display carries while it shows tuned data.</summary>
    public const string TuningChip = "Tuning";

    /// <summary>The chip a display carries while it shows optimizer data (TO-10).</summary>
    public const string OptimizingChip = "Optimizing";

    /// <summary>The chip a display carries while it shows a running Monte Carlo or yield run (brief-yield-10, D14).</summary>
    public const string YieldChip = "Yield";

    private readonly Dictionary<string, (DataSet Data, string Chip)> _pendingFrames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _publishedChips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataSet> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _staleGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _frameInFlight;

    /// <summary>
    /// How a frame's END is waited for: handed the continuation that ends the frame, it runs it once
    /// the display has drawn. The application posts it behind the render pass; null runs it at once,
    /// which is what a headless caller wants.
    /// </summary>
    public Action<Action>? FrameScheduler { get; set; }

    /// <summary>Frames drawn from published DataSets (R-to3-6).</summary>
    public long PublishedRedraws { get; private set; }

    /// <summary>Published DataSets replaced before they were drawn (R-to3-6) — TO-10's "skipped n frames".</summary>
    public long SkippedFrames { get; private set; }

    /// <summary>"Tuning" or "Optimizing" while any source shows published data; null otherwise.</summary>
    public string? PublishedChip => _publishedChips.Values.LastOrDefault();

    /// <summary>Raised when <see cref="PublishedChip"/> may have changed.</summary>
    public event EventHandler? PublicationChanged;

    /// <summary>Raised when a snapshot is taken or cleared.</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>True when <paramref name="absPath"/> currently shows a published DataSet.</summary>
    public bool IsPublished(string absPath) => FindEntry(absPath)?.IsPublished ?? false;

    private const string FarFieldCubePrefix = CircuitRF.Engine.Mom.PlanarFarField.Group + ".";

    /// <summary>
    /// Shows <paramref name="data"/> in place of the file at <paramref name="absPath"/> — a newer
    /// version of that source. Only a source this library already holds, loaded from a <c>.npy</c>
    /// that exists, can be published over: a display that names nothing at that path has no trace to
    /// update, and a not-yet-written file has no version to return to on Revert (the session's Stop
    /// writes it, and the ordinary re-run refresh picks it up). Returns false when nothing was taken.
    /// </summary>
    public bool Publish(string absPath, DataSet data, string chip = TuningChip) => Publish(absPath, data, chip, partial: false);

    /// <summary>
    /// <see cref="Publish(string, DataSet, string)"/> for a DataSet that holds only SOME of the source's
    /// analyses (the Optimizer's "goal analyses only", brief-tuneopt-10 R-to10-7) when
    /// <paramref name="partial"/>: every group the file holds and <paramref name="data"/> does not is
    /// carried over from the file, and is STALE — the traces bound to it keep their old data and draw
    /// dimmed (<see cref="StaleGroupsFor"/>). So are the far-field cubes of an analysis the DataSet does hold: an optimizer's evaluation
    /// reads a pattern without forming its cubes (brief-cosim-pattern-4 R-cp4-1), and an open pattern plot must not empty for it.
    /// </summary>
    public bool Publish(string absPath, DataSet data, string chip, bool partial)
    {
        var entry = FindEntry(absPath);
        if (entry is null || entry.Kind != SourceKind.Npy || entry.IsBroken) return false;

        string key = Path.GetFullPath(absPath);
        if (partial && entry.FileData is { } file)
        {
            var stale  = new HashSet<string>(StringComparer.Ordinal);
            var merged = ShallowCopy(data);
            foreach (var group in file.Groups)
            {
                if (data.Groups.Contains(group))
                {
                    var held = data.CubesIn(group);
                    foreach (var (name, cube) in file.CubesIn(group))
                    {
                        if (!name.StartsWith(FarFieldCubePrefix, StringComparison.Ordinal) || held.ContainsKey(name)) continue;
                        stale.Add($"{group}.{CircuitRF.Engine.Mom.PlanarFarField.Group}");
                        merged.AddToGroup(group, name, cube);
                    }
                    continue;
                }
                stale.Add(group);
                foreach (var (name, cube) in file.CubesIn(group)) merged.AddToGroup(group, name, cube);
            }
            data = merged;
            if (stale.Count > 0) _staleGroups[key] = stale; else _staleGroups.Remove(key);
        }
        else _staleGroups.Remove(key);
        if (_frameInFlight)
        {
            if (_pendingFrames.Remove(key)) SkippedFrames++;
            _pendingFrames[key] = (data, chip);
            return true;
        }
        DrawFrame(entry, key, data, chip);
        return true;
    }

    /// <summary>Drops the publication at <paramref name="absPath"/>; the display returns to the file
    /// as it was.</summary>
    public void Unpublish(string absPath)
    {
        string key = Path.GetFullPath(absPath);
        _pendingFrames.Remove(key);
        var entry = FindEntry(absPath);
        if (entry is null || !entry.IsPublished) { SetChip(key, null); return; }
        entry.RestoreFile();
        SetChip(key, null);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The published DataSet has just been written to the file (a session's Stop, R-to3-7; an optimizer's
    /// finish): it IS the file's version now. Ends the publication without returning to the older file data
    /// and without re-reading the disk. A frame still waiting is the NEWEST publication — the one just
    /// written — so it is shown first, never dropped: dropping it left the display on an earlier frame
    /// while the file held the final one, whenever the last publication arrived mid-draw.
    /// </summary>
    public void KeepPublishedAsFile(string absPath)
    {
        string key = Path.GetFullPath(absPath);
        if (FindEntry(absPath) is not { } entry) { SetChip(key, null); return; }
        if (_pendingFrames.Remove(key, out var waiting) && !entry.IsBroken)
        {
            PublishedRedraws++;
            entry.ApplyPublished(ShallowCopy(waiting.Data));
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        ForgetPublication(entry);
    }

    private void DrawFrame(DataSourceEntryViewModel entry, string key, DataSet data, string chip)
    {
        _frameInFlight = true;
        PublishedRedraws++;
        // A shallow copy: reading an entry's Data materializes virtual Z/Y cubes INTO its DataSet, and
        // the publisher's own DataSet is the one a session later writes to disk.
        entry.ApplyPublished(ShallowCopy(data));
        SetChip(key, chip);
        LibraryChanged?.Invoke(this, EventArgs.Empty);

        if (FrameScheduler is { } schedule) schedule(EndFrame);
        else EndFrame();
    }

    private void EndFrame()
    {
        _frameInFlight = false;
        if (_pendingFrames.Count == 0) return;

        var (key, frame) = _pendingFrames.First();
        _pendingFrames.Remove(key);
        if (FindEntry(key) is { } entry && !entry.IsBroken) DrawFrame(entry, key, frame.Data, frame.Chip);
        else EndFrame();
    }

    /// <summary>The file changed on disk: it is the newer version now.</summary>
    private void ForgetPublication(DataSourceEntryViewModel entry)
    {
        if (entry.FilePath is not { } fp) return;
        string key = Path.GetFullPath(fp);
        _pendingFrames.Remove(key);
        if (!entry.IsPublished) return;
        entry.ForgetPublication();
        SetChip(key, null);
    }

    /// <summary>The groups of <paramref name="absPath"/>'s published DataSet that were carried over from
    /// the file rather than computed — an analysis the optimizer is not running (R-to10-7). Empty when
    /// nothing is stale.</summary>
    public IReadOnlySet<string> StaleGroupsFor(string? absPath) =>
        absPath is not null && _staleGroups.TryGetValue(Path.GetFullPath(absPath), out var s) ? s : EmptyGroups;

    private static readonly HashSet<string> EmptyGroups = [];

    private void SetChip(string key, string? chip)
    {
        if (chip is null) _staleGroups.Remove(key);
        var before = PublishedChip;
        if (chip is null) _publishedChips.Remove(key);
        else              _publishedChips[key] = chip;
        if (!string.Equals(before, PublishedChip, StringComparison.Ordinal))
            PublicationChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Snapshots (R-to3-9) -----------------------------------------------

    /// <summary>
    /// Freezes what <paramref name="absPath"/> currently shows — published or the file's — as the
    /// snapshot every trace bound to it draws a faded ghost of. Session-only: never written, never
    /// exported. Returns false when this library holds nothing at that path.
    /// </summary>
    public bool TakeSnapshot(string absPath)
    {
        if (FindEntry(absPath)?.Data is not { } shown) return false;
        _snapshots[Path.GetFullPath(absPath)] = ShallowCopy(shown);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Shows <paramref name="data"/> as the snapshot at <paramref name="absPath"/> — a re-run trial's full result
    /// (brief-yield-9 R-ya9-6), drawn as the ghost of every trace bound to that source. False when this library
    /// holds nothing at that path.
    /// </summary>
    public bool ShowSnapshot(string absPath, DataSet data)
    {
        if (FindEntry(absPath) is null) return false;
        _snapshots[Path.GetFullPath(absPath)] = data;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes the snapshot at <paramref name="absPath"/>.</summary>
    public void ClearSnapshot(string absPath)
    {
        if (_snapshots.Remove(Path.GetFullPath(absPath)))
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The snapshot traces bound to <paramref name="absPath"/> draw ghosts from, or null.</summary>
    public DataSet? SnapshotFor(string? absPath) =>
        absPath is not null && _snapshots.TryGetValue(Path.GetFullPath(absPath), out var s) ? s : null;

    /// <summary>
    /// What "Add as goal…" on a trace does with the goal it pre-filled (brief-tuneopt-9 R-to9-4) —
    /// set by the workspace, which owns the schematic the trace's source came from; the second
    /// argument is that source's path. Null where there is no workspace, which greys the item out.
    /// </summary>
    public Action<CircuitRF.Core.Design.OptimizationGoal, string?>? AddAsGoal { get; set; }

    /// <summary>
    /// "Send trial to Tuning" (brief-yield-9 R-ya9-6): loads a trial's values into the Tuning sliders of the schematic
    /// the source was run from — set by the workspace; the second argument is the source's path and the third the
    /// label the panel reports. Null where there is no workspace, which greys the item out.
    /// </summary>
    public Action<IReadOnlyDictionary<string, string>, string, string>? SendTrialToTuning { get; set; }

    /// <summary>"Save as corner…" (brief-yield-9 R-ya9-6, YA-6): adds a statistical corner naming the trial to the
    /// schematic the source was run from — set by the workspace, which asks for the name. Null greys it out.</summary>
    public Action<string, int, DataSet>? SaveTrialAsCorner { get; set; }

    /// <summary>Where a trial action's sentence goes when it could not do what was asked — the Messages panel, set by
    /// the workspace.</summary>
    public Action<string>? TrialMessage { get; set; }

    /// <summary>Every open display's library — where a re-run trial's ghost is shown, so it appears in every display
    /// bound to the source. Set by the workspace; null means this library alone.</summary>
    public Func<IEnumerable<DataSourceLibraryViewModel>>? AllLibraries { get; set; }

    /// <summary>The DataSet the source at <paramref name="absPath"/> currently holds, or null — what
    /// "Add as goal…" reads a trace's analysis and S cube from.</summary>
    public DataSet? DataFor(string? absPath) => absPath is null ? null : FindEntry(absPath)?.Data;

    // ---- helpers -------------------------------------------------------------

    private DataSourceEntryViewModel? FindEntry(string absPath)
    {
        string full = Path.GetFullPath(absPath);
        return Entries.FirstOrDefault(e => e.FilePath is { } fp
            && string.Equals(Path.GetFullPath(fp), full, StringComparison.OrdinalIgnoreCase));
    }

    private static DataSet ShallowCopy(DataSet src)
    {
        var copy = new DataSet();
        foreach (var group in src.Groups)
            foreach (var (name, cube) in src.CubesIn(group))
                copy.AddToGroup(group, name, cube);
        return copy;
    }
}
