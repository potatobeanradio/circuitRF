// THE HIERARCHICAL HALF OF THE LAYOUT READ — brief-lvs-9-hierarchy.md R-lvs9-2 and R-lvs9-3.
//
// `LayoutRead` reads ONE cell, in its own frame, and that has been true since brief 3. What this
// file adds is the three decisions a level has to take about the cells placed on it:
//
//   which placements are cells of their own          (R-lvs9-2c, and the info line when one is not)
//   whose copper therefore stays inside them         (R-lvs9-2d)
//   and where a cell touches this design anyway      (R-lvs9-3)
//
// ── WHY THE SUB-CELL'S COPPER LEAVES THE PARTITION, AND WHAT IT COSTS ─────────────────────────
//
// R-lvs9-2d: net identity across the boundary comes ONLY from the pin landing on parent copper. A
// module read as a cell is a black box with terminals; its internal metal is its own business, and
// leaving it in the parent's partition would let two of its pins share a parent net because of a
// trace the parent cannot see. What stays in is each declared boundary PAD — that pad IS how the
// pin lands on the parent's copper, and dropping it would read a correctly-abutted module as open.
//
// The cost is that the two readings can now differ, which is exactly what makes R-lvs9-4b a real
// gate rather than a tautology: on a design whose cells keep to their pins, taking their internals
// out changes no parent net at all, and the flat and hierarchical runs agree finding for finding.
// Where they do not, the copper is joined somewhere nobody declared — which is R-lvs9-3.
//
// ── NO GEOMETRY IS DONE HERE ──────────────────────────────────────────────────────────────────
//
// The contact test is `SubCellContact`, in Extraction, because it calls Clipper and geometry
// belongs to Extraction and Drc — the rule `LayoutRead`'s own header states and a source scan
// holds. What is here is which shapes to hand it and what to call the answer.

using System.Linq;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>One of the root's placements, resolved once — see <see cref="LayoutReadHierarchy"/>.</summary>
/// <param name="View">Its cell's primary layout, or null where the reference does not resolve.</param>
/// <param name="CellDir">Its resolved cell folder, absolute, or null.</param>
internal readonly record struct PlacedCell(LayoutView? View, string? CellDir);

/// <summary>The three decisions a level takes about the cells placed on it — R-lvs9-2, R-lvs9-3.</summary>
internal static class LayoutReadHierarchy
{
    /// <summary>
    /// Which root placements are read as cells of their own, by instance index — and the info line
    /// for every cell that could have been and was not (R-lvs9-3d, R-lvs9-6b).
    /// </summary>
    /// <remarks>
    /// <b>Only a COMPARABLE cell is ever reported as flattened.</b> Every board places land
    /// patterns, generated parts and via fences, none of which has a drawing of its own; a line
    /// about each would be a report nobody reads, and it would say nothing — there was never a
    /// second reading available. The line exists so that a design which quietly flattens the cells
    /// it COULD have compared is visible, which is the only case it is about.
    /// </remarks>
    public static Dictionary<int, string> Classify(
        LayoutView view, IReadOnlyList<PlacedCell> placed, LvsHierarchyContext? hierarchy,
        List<Diagnostic> notes)
    {
        var modules = new Dictionary<int, string>();
        if (hierarchy is null) return modules;

        // Once per cell TYPE, for UnclassifiedCell's reason: a module placed forty times is one
        // decision, not forty.
        var flattened = new Dictionary<string, (string Name, int Count, string Reason)>(StringComparer.Ordinal);

        for (int i = 0; i < view.Instances.Count; i++)
        {
            if (placed[i].CellDir is not { Length: > 0 } dir) continue;
            if (!LvsHierarchy.IsComparableCell(dir)) continue;

            // `--flat` is the caller asking for one flat graph, so every cell is flattened and
            // saying so once per cell would be a report repeating the command line back. It is also
            // what would make the two runs' reports differ by construction, and R-lvs9-4b's gate is
            // that they do not.
            if (hierarchy.Flat) continue;

            string? reason =
                hierarchy.Descending.Contains(dir)  ? LvsHierarchy.FlattenReason.Cycle
              : hierarchy.FlattenCells.Contains(LvsHierarchyContext.NameOf(dir))
                                                    ? LvsHierarchy.FlattenReason.Asked
              : LvsHierarchy.DeclaresFlattenForLvs(dir)
                                                    ? LvsHierarchy.FlattenReason.Declared
              : !LayoutRead.IsDevice(view.Instances[i], dir, placed[i].View)
                                                    ? LvsHierarchy.FlattenReason.NotADevice
              : null;

            if (reason is null) { modules[i] = dir; continue; }

            var prior = flattened.GetValueOrDefault(dir);
            flattened[dir] = (LvsHierarchyContext.NameOf(dir), prior.Count + 1, reason);
        }

        foreach (var (_, entry) in flattened.OrderBy(e => e.Key, StringComparer.Ordinal))
            notes.Add(LvsDiagnostics.CellFlattened(entry.Name, entry.Count, entry.Reason));

        return modules;
    }

    /// <summary>
    /// The copper this level's partition is built from — everything the flatten produced, less the
    /// INTERNALS of every placement read as a cell of its own (R-lvs9-2d).
    /// </summary>
    /// <remarks>
    /// <b>A declared boundary pad stays</b>, because that pad is how the pin lands on the parent's
    /// copper (R-lvs9-2a) and a module abutting a trace would otherwise read as open.
    ///
    /// <para><b>Which shapes those are is decided by WHERE they are, never by a pin NAME.</b> The
    /// flatten tags every shape with the sub-cell pin it realises, at every depth — so a land
    /// pattern three levels inside a module contributes pads named <c>A</c> and <c>B</c> exactly as
    /// the module's own boundary does, and a name test keeps the module's insides in the partition
    /// on any design whose pin names happen to coincide. They usually do. A shape is a boundary pad
    /// when it COVERS one of this placement's declared pins, on that pin's own layer, which is the
    /// same question <c>PlacedPins</c> already answered by projecting them.</para>
    ///
    /// <para>The test is the shape's bounding box, which is generous for a polygon: a module-wide
    /// pour whose box happens to span a pin is kept. That direction is the safe one — the shape
    /// stays in the partition exactly as a flat reading would have it, so the worst case is a
    /// contact this brief does not report, never a net it invents.</para>
    ///
    /// <para><b>Placements are identified by the flatten's own path spelling</b>, which is
    /// <c>LayoutDesignFlatten.PathOf</c> — the one rule, called from both sides, so a placement the
    /// flatten calls <c>@3</c> and the netlist calls something else cannot happen. Two placements
    /// sharing a designator share a path; that is already an error before anything is matched
    /// (R-lvs3-3e), and both of them are read as cells here.</para>
    /// </remarks>
    public static IReadOnlyList<LayoutShape> CopperFor(
        IReadOnlyList<LayoutDesignFlatten.TaggedShape> shapes,
        LayoutView view, IReadOnlyDictionary<int, string> modules,
        IReadOnlyList<PlacedPin> pads, IReadOnlyList<PlacedPinOrigin> origins)
    {
        if (modules.Count == 0) return [.. shapes.Select(t => t.Shape)];

        // path -> where that placement's declared pins landed, in THIS design's frame.
        var boundary = new Dictionary<string, List<(long X, long Y, LayerKey Layer)>>(StringComparer.Ordinal);
        foreach (int i in modules.Keys)
            boundary[LayoutDesignFlatten.PathOf(view.Instances[i], i)] = [];

        for (int p = 0; p < origins.Count; p++)
        {
            if (!modules.ContainsKey(origins[p].Instance)) continue;
            string path = LayoutDesignFlatten.PathOf(view.Instances[origins[p].Instance], origins[p].Instance);
            if (boundary.TryGetValue(path, out var points))
                points.Add((pads[p].X, pads[p].Y, origins[p].Layer));
        }

        var copper = new List<LayoutShape>(shapes.Count);
        foreach (var tagged in shapes)
        {
            if (boundary.TryGetValue(tagged.InstancePath, out var points)
                && !CoversAPin(tagged.Shape, points))
                continue;

            copper.Add(tagged.Shape);
        }
        return copper;
    }

    /// <summary>Whether <paramref name="shape"/> is one of the pads a declared pin lands on.</summary>
    private static bool CoversAPin(
        LayoutShape shape, List<(long X, long Y, LayerKey Layer)> pins)
    {
        var box = LayoutGeometry.BboxOf(shape);
        foreach (var (x, y, layer) in pins)
            if (shape.Layer == layer && box.Contains(x, y)) return true;
        return false;
    }

    /// <summary>
    /// Every place a placement read as a cell meets this design's copper away from a pin it
    /// declares — R-lvs9-3b, reported and never absorbed.
    /// </summary>
    /// <param name="pads">Every placed pad, as <c>PlacedPins</c> projected them.</param>
    /// <param name="origins">Their instances and layers, in lockstep.</param>
    public static void ReportUndeclaredContact(
        LayoutView view, IReadOnlyList<LayoutDesignFlatten.TaggedShape> shapes,
        IReadOnlyList<PlacedPin> pads, IReadOnlyList<PlacedPinOrigin> origins,
        IReadOnlyDictionary<int, string> modules, CopperPieces pieces, Technology? tech,
        LvsHierarchyContext? hierarchy, List<Diagnostic> notes, LvsGeometryNaming naming)
    {
        if (hierarchy is null || modules.Count == 0 || tech is null || !pieces.Any) return;

        foreach (int i in modules.Keys.OrderBy(k => k))
        {
            string path = LayoutDesignFlatten.PathOf(view.Instances[i], i);
            var contributed = shapes.Where(t => t.InstancePath == path).Select(t => t.Shape).ToList();
            if (contributed.Count == 0) continue;

            // Every declared pin of every element of the array, already transformed into this
            // design's frame by PlacedPins — the one projection, not a second one.
            var declared = new List<(long X, long Y, LayerKey Layer)>();
            for (int p = 0; p < origins.Count; p++)
                if (origins[p].Instance == i) declared.Add((pads[p].X, pads[p].Y, origins[p].Layer));

            hierarchy.Counters.ContactChecks++;
            string cellName = LvsHierarchyContext.NameOf(modules[i]);

            foreach (var contact in SubCellContact.Find(contributed, pieces, declared, tech))
                notes.Add(LvsDiagnostics.UndeclaredContact(
                    path, cellName, contact.Layer, naming.NameOfLayer(contact.Layer),
                    naming.Format.Point(contact.X, contact.Y),
                    contact.WidthDbu, contact.X, contact.Y));
        }
    }
}

/// <summary>
/// How a coordinate and a layer are SPELLED in a sentence — the layout's own display unit and the
/// technology's own layer names.
/// </summary>
/// <remarks>
/// <b>Separated from <see cref="LvsGeometry"/> because the extraction needs it before that object
/// exists.</b> Reading a mark without its scale once produced a run at 2 Hz that looked entirely
/// normal; a contact reported at "1400000" in a design drawn in millimetres is the same class of
/// mistake, so the unit travels with the number.
/// </remarks>
/// <param name="Format">The layout's own length format.</param>
/// <param name="LayerNames">What the technology calls each drawing layer.</param>
internal readonly record struct LvsGeometryNaming(
    RailRf.RailLengthFormat Format, IReadOnlyDictionary<LayerKey, string> LayerNames)
{
    public string NameOfLayer(LayerKey layer)
        => LayerNames.TryGetValue(layer, out string? name) && name.Length > 0
            ? name
            : $"layer {layer.Layer}/{layer.Datatype}";
}
