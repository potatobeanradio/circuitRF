// Framework-free. No Avalonia, no SkiaSharp — this sits between a resolved .clay and the two
// extractors, and both of those are framework-free too.

using System;
using System.Collections.Generic;
using System.IO;

namespace CircuitRF.Ui.Layout.Em;

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
    /// <summary>The flattened shape list, plus one note per unresolvable instance.</summary>
    public sealed record Result(IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<string> Notes);

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
            return new Result(view.Shapes, []);

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
            notes.Add($"{flattened} shape(s) came from {view.Instances.Count} placed instance(s), " +
                      "flattened into world coordinates for extraction. The layout itself is unchanged.");

        return new Result(shapes, notes);
    }
}
