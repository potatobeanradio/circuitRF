using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// What kind of thing an optional archive entry is — the three branches the Archive Workspace dialog
/// shows, and the three the writer treats differently.
/// </summary>
public enum ArchiveOptionKind
{
    /// <summary>A referenced kit FOLDER (a <c>.cws</c> LibraryRef or PdkRef pointing outside the workspace).</summary>
    Kit,

    /// <summary>A file referenced from inside the workspace but living outside it — a Known File, a
    /// bitmap underlay, a Touchstone a component names.</summary>
    ExternalFile,

    /// <summary>A file under the workspace's own <c>results/</c> folder.</summary>
    Result,
}

/// <summary>
/// One thing the user can tick in the Archive Workspace dialog.
///
/// <para>An option is never <i>required</i> — everything a workspace cannot be read without (its
/// cells, its <c>.cws</c>, its technologies, every loose file beside them) is archived
/// unconditionally and never appears here. This list is only the material whose bulk or provenance
/// makes it a judgement call.</para>
/// </summary>
public sealed class ArchiveOption
{
    public required ArchiveOptionKind Kind { get; init; }

    /// <summary>
    /// What the dialog's row is CALLED — a kit's own name, a file's name. Never a full path: the
    /// tree trims a long row from the END, which is precisely the half of a path that identifies it.
    /// The path goes in <see cref="Detail"/>, where the tooltip shows it in full.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>The row's tooltip — where the thing actually is. Falls back to <see cref="SourcePath"/>.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Absolute path of the file or folder to copy.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Path INSIDE the zip, always '/'-separated and always relative to the archive root.</summary>
    public required string ArchivePath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>
    /// Size in bytes, or -1 when not measured yet. A kit folder is measured lazily (walking a vendor
    /// kit of tens of thousands of files must not hold up the dialog); a plain file is measured on
    /// the spot, which costs one <c>FileInfo</c>.
    /// </summary>
    public long SizeBytes { get; set; } = -1;

    /// <summary>Ticked when the dialog opens. See <see cref="WorkspaceArchiveScanner"/> for the defaults.</summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Group heading for a <see cref="ArchiveOptionKind.Result"/> — "Data Displays", "Touchstone",
    /// "Analysis", or "Other". Empty for the other kinds, which are their own branch.
    /// </summary>
    public string Group { get; init; } = "";

    /// <summary>Stable identity for tests and for the writer's own bookkeeping.</summary>
    public string Id => $"{Kind}:{ArchivePath}";
}

/// <summary>
/// Everything <c>Archive Workspace…</c> needs to know about one workspace: what it will always
/// include, and what it is asking the user about.
/// </summary>
public sealed class WorkspaceArchivePlan
{
    /// <summary>Absolute workspace root (the folder holding <c>.cws</c>).</summary>
    public required string WorkspaceDir { get; init; }

    /// <summary>
    /// Workspace-relative paths ('/'-separated) archived unconditionally — the <c>.cws</c>, every
    /// cell, every technology, every loose file that is not under <c>results/</c>. The owner's rule:
    /// "It is assumed that all the cells will be archived. The user expects that."
    /// </summary>
    public List<string> AlwaysIncluded { get; init; } = [];

    /// <summary>The optional material, in dialog order.</summary>
    public List<ArchiveOption> Options { get; init; } = [];

    public IEnumerable<ArchiveOption> Kits          => Options.Where(o => o.Kind == ArchiveOptionKind.Kit);
    public IEnumerable<ArchiveOption> ExternalFiles => Options.Where(o => o.Kind == ArchiveOptionKind.ExternalFile);
    public IEnumerable<ArchiveOption> Results       => Options.Where(o => o.Kind == ArchiveOptionKind.Result);

    /// <summary>Bytes of the always-included material, measured during the scan.</summary>
    public long AlwaysIncludedBytes { get; set; }

    /// <summary>Paths the scan deliberately left out, for the message log — temp and OS clutter.</summary>
    public List<string> SkippedPaths { get; init; } = [];

    /// <summary>Sum of what is currently ticked, plus the unconditional material.</summary>
    public long SelectedBytes =>
        AlwaysIncludedBytes + Options.Where(o => o.Selected && o.SizeBytes > 0).Sum(o => o.SizeBytes);

    /// <summary>Human-readable size, the way a file manager writes one.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "…";
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes / 1024.0;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v >= 100 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }
}
