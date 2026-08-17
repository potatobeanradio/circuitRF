using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Embedding the layout geometry a wBond was designed against, so the file can be handed to someone
/// with no access to the originating workspace (wbond.md §9.1).
///
/// <h3>PDK PCells are flattened; native circuitRF PCells are not (WB33 / WB34)</h3>
/// <para>A PDK PCell's generator is licensed vendor code that cannot be shipped inside a design file,
/// so it becomes ordinary polygons. A native circuitRF PCell's generator ships with circuitRF, so the
/// receiving copy can regenerate it and it stays parametric. <b>That asymmetry is the whole reason
/// the distinction is worth drawing</b> — flattening everything would be simpler and would silently
/// cost the recipient every parameter on cells that did not need to lose them.</para>
///
/// <h3>The user is told before the save, not after (WB33)</h3>
/// <para><see cref="Analyze"/> produces the list the dialog shows. A file that quietly lost
/// parametricity on the way out is discovered by the recipient, which is the worst possible moment.</para>
///
/// <h3>Unpacked as real cell folders, deliberately</h3>
/// <para><see cref="Unpack"/> writes the bundle to a scratch directory as ordinary cell folders
/// rather than installing an in-memory resolver, because <c>CellLayoutResolver.Resolve</c> requires a
/// directory to exist before it consults anything else. Going through the real path means rendering,
/// hit-testing, snapping and hierarchy descent all work on embedded geometry with no second
/// code path — and a second code path is exactly where embedded and referenced geometry would start
/// behaving differently.</para>
/// </summary>
public static class WBondGeometryEmbedding
{
    /// <summary>What embedding WOULD do — shown before the save, never reported afterwards.</summary>
    /// <param name="Cells">Cell directories that will be embedded, root first.</param>
    /// <param name="PdkFlattened">
    /// Cells whose PCell generator is not one of circuitRF's own, so they lose parametricity.
    /// </param>
    /// <param name="NativeKept">Cells staying parametric because circuitRF ships their generator.</param>
    /// <param name="Unresolved">Instance references that could not be resolved and will be lost.</param>
    public readonly record struct EmbedPlan(
        IReadOnlyList<string> Cells,
        IReadOnlyList<string> PdkFlattened,
        IReadOnlyList<string> NativeKept,
        IReadOnlyList<string> Unresolved)
    {
        /// <summary>True when the save costs the user nothing they need warned about.</summary>
        public bool HasNothingToReport => PdkFlattened.Count == 0 && Unresolved.Count == 0;
    }

    private sealed class Bundle
    {
        [JsonPropertyName("marker")] public string? Marker { get; set; }

        /// <summary>The root layout, serialised with <c>LayoutPersistence</c>'s own format.</summary>
        public string Root { get; set; } = "";

        /// <summary>Cell-relative-path to serialised layout, for every cell the root reaches.</summary>
        public Dictionary<string, string> Cells { get; set; } = [];
    }

    public const string Marker = "circuitrf/wbond-geometry-v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Whether there is any reference geometry for a save to be asked about at all (owner,
    /// 2026-08-16).
    ///
    /// <para><b>A question with one possible answer is not a question.</b> A wBond holding nothing but
    /// wires — the ordinary state of a new document, and of every document opened from the palette —
    /// was still shown "Include the layout geometry in this file?" with nothing on either side of the
    /// choice. The dialog now appears only when the layout actually holds something: a shape, or an
    /// instance of a cell.</para>
    ///
    /// <para>Deliberately asked of the ROOT view rather than of <see cref="Analyze"/>'s plan: an
    /// unresolvable instance is still geometry the user put there and still something they may want
    /// to be told about, whereas <c>Analyze</c> would report it only as a loss.</para>
    /// </summary>
    public static bool HasGeometryToEmbed(LayoutView? root) =>
        root is not null && (root.Shapes.Count > 0 || root.Instances.Count > 0);

    /// <summary>
    /// Walks what <paramref name="root"/> references and reports what embedding would cost.
    /// Never mutates anything.
    /// </summary>
    public static EmbedPlan Analyze(LayoutView? root, string? baseDir)
    {
        var cells = new List<string>();
        var pdk = new List<string>();
        var native = new List<string>();
        var unresolved = new List<string>();

        if (root is not null && baseDir is not null)
            Walk(root, baseDir, cells, pdk, native, unresolved, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);

        return new EmbedPlan(cells, pdk, native, unresolved);
    }

    private static void Walk(LayoutView view, string baseDir,
                             List<string> cells, List<string> pdk, List<string> native,
                             List<string> unresolved, HashSet<string> seen, int depth)
    {
        if (depth > CellHierarchy.MaxDepth) return;

        foreach (var instance in view.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.CellRef)) continue;

            var resolution = CellLayoutResolver.Resolve(instance.CellRef, baseDir);
            if (resolution.State != CellLayoutState.Resolved || resolution.View is null)
            {
                if (!unresolved.Contains(instance.CellRef)) unresolved.Add(instance.CellRef);
                continue;
            }

            string cellDir = Path.GetFullPath(Path.Combine(baseDir, instance.CellRef));
            if (!seen.Add(cellDir)) continue;

            cells.Add(cellDir);

            if (resolution.View.PCellOrigin is { } origin)
            {
                // "One of ours" is asked of the BUILT-IN registry only. A generator that arrived from
                // a kit resolver is the vendor's, and its code cannot travel inside a design file.
                if (PCellRegistry.KnownGeneratorIds.Contains(origin.GeneratorId)) native.Add(cellDir);
                else pdk.Add(cellDir);
            }

            Walk(resolution.View, CellHierarchy.LayoutBaseDirOf(cellDir), cells, pdk, native, unresolved, seen, depth + 1);
        }
    }

    /// <summary>
    /// Produces the bundle to store in <c>WBondDesign.EmbeddedGeometryJson</c>.
    ///
    /// <para>A PDK-backed cell is flattened to polygons; every other cell travels as-is, keeping its
    /// <c>PCellOrigin</c> so the receiving copy can regenerate it.</para>
    /// </summary>
    public static string Embed(LayoutView root, string baseDir)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(baseDir);

        var plan = Analyze(root, baseDir);
        var bundle = new Bundle { Marker = Marker, Root = LayoutPersistence.Serialize(root) };

        var pdkSet = new HashSet<string>(plan.PdkFlattened, StringComparer.OrdinalIgnoreCase);

        foreach (string cellDir in plan.Cells)
        {
            var resolution = CellLayoutResolver.Resolve(cellDir, baseDir: "");
            if (resolution.State != CellLayoutState.Resolved || resolution.View is null) continue;

            var view = resolution.View;

            if (pdkSet.Contains(cellDir))
            {
                // Flattening happens on a CLONE. Mutating the resolver's cached view would leave the
                // live workspace holding flattened geometry after a save, which is a save changing
                // the user's design.
                view = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
                view.PCellOrigin = null;   // it is polygons now; claiming otherwise would invite a regenerate
            }

            bundle.Cells[Key(cellDir, baseDir)] = LayoutPersistence.Serialize(view);
        }

        return JsonSerializer.Serialize(bundle, JsonOpts);
    }

    /// <summary>
    /// Unpacks a bundle into <paramref name="targetDir"/> as ordinary cell folders and returns the
    /// root view plus the base directory its instances resolve against.
    ///
    /// <para>Returns null for anything that is not one of ours, so an older or foreign
    /// <c>EmbeddedGeometryJson</c> is ignored rather than half-applied.</para>
    /// </summary>
    public static (LayoutView Root, string BaseDir)? Unpack(string? bundleJson, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(bundleJson) ||
            !bundleJson.Contains(Marker, StringComparison.Ordinal)) return null;

        Bundle? bundle;
        try { bundle = JsonSerializer.Deserialize<Bundle>(bundleJson, JsonOpts); }
        catch (JsonException) { return null; }

        if (bundle?.Marker != Marker || string.IsNullOrWhiteSpace(bundle.Root)) return null;

        Directory.CreateDirectory(targetDir);

        foreach (var (key, clay) in bundle.Cells)
        {
            string cellDir = Path.Combine(targetDir, key);
            string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            Directory.CreateDirectory(layoutDir);

            string fileName = Path.GetFileName(key) + ".clay";
            File.WriteAllText(Path.Combine(layoutDir, fileName), clay);

            // A .ccell naming the primary, so CellFolder.ResolvePrimary answers the same way it does
            // for a cell in a real workspace.
            CellPersistence.SaveToFile(
                Path.Combine(cellDir, CellFolder.CcellFileName),
                new CcellFile { PrimaryLayout = fileName });
        }

        // Instances resolve relative to the directory holding the .clay, which for the root is a
        // synthetic layout folder one level down — the same shape a real cell has.
        string rootLayoutDir = Path.Combine(targetDir, "__root", CellFolder.LayoutSubFolder);
        Directory.CreateDirectory(rootLayoutDir);

        var root = LayoutPersistence.Deserialize(bundle.Root);
        return (root, rootLayoutDir);
    }

    /// <summary>
    /// A cell's key inside the bundle: its path relative to the workspace where possible, else its
    /// own folder name. Kept stable so a re-embed of an unchanged design produces the same bundle.
    /// </summary>
    private static string Key(string cellDir, string baseDir)
    {
        try
        {
            string relative = Path.GetRelativePath(baseDir, cellDir);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative.Replace('\\', '/');
        }
        catch { /* fall through to the folder name */ }

        return Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
