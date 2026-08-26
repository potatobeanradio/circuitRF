// Framework-free. No Avalonia, no SkiaSharp — this sits between a resolved .clay and the two
// extractors, and both of those are framework-free too.

using System;
using System.Collections.Generic;
using System.IO;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// The shapes an EM extraction actually sees for a layout — top-level artwork PLUS everything inside
/// every placed instance, flattened.
///
/// <para><b>Owner report, 2026-08-09: "My EM setup says 'This EM setup is pointed at geometry with
/// nothing on a layer bound to a signal conductor.' I don't know why."</b> The workspace's layout
/// held two port labels and ONE instance of a generated MLIN cell — and no top-level metal at all,
/// which is what "Update Layout from Schematic" produces by construction. Both extractors were handed
/// <c>view.Shapes</c> directly, so they saw two labels, classified both as annotation, found zero
/// conductor shapes and refused. The artwork was right there on screen; nothing that read it could
/// see it.</para>
///
/// <para><b>Flattening is the right answer rather than teaching each extractor to recurse.</b> The
/// EM problem is a set of polygons in world coordinates — hierarchy carries no meaning into it, and
/// L3c already owns one tested affine flatten (<see cref="LayoutFlatten.FlattenAllLevels"/>,
/// including the mirror bulge-sign rule, array expansion and the <see cref="CellHierarchy.MaxDepth"/>
/// cap). A second traversal inside the extractors would be a second chance to get that transform
/// wrong, in a place where being wrong draws perfectly and simulates something else.</para>
///
/// <para><b>An instance that cannot be resolved is REPORTED, never silently dropped</b> — the
/// extractors' own "N shapes ignored" notes already set that convention, and an unresolvable
/// reference is exactly the case where a quiet zero-conductor refusal is most misleading.</para>
/// </summary>
public static class EmGeometry
{
    /// <summary>
    /// The flattened shape list, plus one note per unresolvable instance, plus <b>the PCell generator
    /// ids the flattened geometry came from</b>.
    ///
    /// <para><b><see cref="GeneratorIds"/> is additive and exists because R-msh-8a had never once
    /// fired in the shipping application</b> (found 2026-08-14). <c>PlanarExtractor.Extract</c> has
    /// taken an optional <c>generatorIds</c> since L8b and <b>no caller in <c>src/</c> ever passed
    /// one</b> — so <c>AnalyticAlternativeFor</c>, its three mappings and its note were live, tested,
    /// and unreachable by any user. Flattening is the right place to collect them for the same reason
    /// it is the right place to collect shapes: it is the one pass that already walks every instance,
    /// and the extractors are handed world-coordinate polygons with no hierarchy left to ask.</para>
    /// </summary>
    public sealed record Result(
        IReadOnlyList<LayoutShape> Shapes,
        IReadOnlyList<string>      Notes,
        IReadOnlyList<string>      GeneratorIds);

    /// <summary>
    /// Flatten <paramref name="view"/> for extraction. <paramref name="clayPath"/> is the layout's
    /// own absolute path — instance <c>CellRef</c>s resolve relative to the directory CONTAINING the
    /// <c>.clay</c>, which is why the path rather than a directory is taken here: getting that one
    /// level wrong resolves nothing and looks exactly like a missing cell.
    /// </summary>
    public static Result Flatten(LayoutView view, string clayPath)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Instances.Count == 0)
            return new Result(view.Shapes, [], CollectGeneratorIds(view));

        string baseDir = Path.GetDirectoryName(clayPath) ?? "";

        var shapes = new List<LayoutShape>(view.Shapes);
        var notes  = new List<string>();
        int unresolved = 0;

        foreach (var inst in view.Instances)
        {
            var flat = LayoutFlatten.FlattenAllLevels(inst, baseDir);
            shapes.AddRange(flat.Shapes);
            unresolved += flat.SurvivingInstances.Count;
        }

        if (unresolved > 0)
            notes.Add($"{unresolved} placed instance(s) could not be resolved and contributed no " +
                      "geometry to the EM problem. A cell that does not resolve is invisible to the " +
                      "solver even though its placeholder is drawn in the layout — check the cell " +
                      "reference, or flatten the instance.");

        int flattened = shapes.Count - view.Shapes.Count;
        if (flattened > 0)
            // "The layout itself is unchanged" was dropped (owner request, 2026-08-11) — the user
            // already expects a read not to edit their design, so the sentence only added length to
            // a note whose useful half is the count.
            notes.Add($"{flattened} shape(s) came from {view.Instances.Count} placed instance(s), " +
                      "flattened into world coordinates for extraction.");

        return new Result(shapes, notes, CollectGeneratorIds(view));
    }

    /// <summary>
    /// Which PCell generators this layout's artwork came from, distinct and in placement order.
    ///
    /// <para><b>Read from <see cref="LayoutView.PCellSnapshots"/>, which is exactly the table built to
    /// answer this.</b> Its own doc comment says it "covers every PCell instance this layout
    /// references, regardless of whether it arrived via schematic generation, a palette drop, or a
    /// layout-authored copy-on-write edit", and it is keyed by the generated cell's FOLDER NAME —
    /// which is the last segment of the instance's <c>CellRef</c>. Nothing has to be parsed out of a
    /// cell name, and nothing has to be loaded off disk.</para>
    ///
    /// <para><b>A miss is silent and that is deliberate.</b> An instance with no snapshot (a
    /// hand-built cell, a foreign document, a snapshot written by an older version) simply contributes
    /// no id, and the run behaves exactly as it did before this existed. This is a note, and a note
    /// that cannot be produced is not a failure.</para>
    /// </summary>
    private static IReadOnlyList<string> CollectGeneratorIds(LayoutView view)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The view may itself BE a generated cell (the panel can be pointed straight at one).
        if (view.PCellOrigin is { GeneratorId: { Length: > 0 } own } && seen.Add(own))
            ids.Add(own);

        foreach (var inst in view.Instances)
        {
            if (inst.CellRef is not { Length: > 0 } cellRef) continue;
            string folder = LastSegment(cellRef);
            if (folder.Length == 0) continue;
            if (view.PCellSnapshots.TryGetValue(folder, out var snap)
                && snap.GeneratorId is { Length: > 0 } id
                && seen.Add(id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The last path segment of a <see cref="LayoutInstance.CellRef"/>, splitting on <b>both</b>
    /// separators regardless of the platform running.
    ///
    /// <para><b><see cref="Path.GetFileName(string)"/> is wrong here and the failure is silent.</b> On
    /// Unix a backslash is an ordinary filename character, so
    /// <c>GetFileName(@"..\..\.generated-cells\MKLOPF_770fa9b3d56e")</c> returns the whole string and
    /// the snapshot lookup misses. A <c>CellRef</c> is stored with whatever separator the machine that
    /// wrote it used, and the report that started all of this arrived as a Windows-authored workspace
    /// opened on macOS — so this is the ordinary case, not an exotic one.</para>
    /// </summary>
    private static string LastSegment(string cellRef)
    {
        string trimmed = cellRef.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
