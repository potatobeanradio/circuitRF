using System;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// What a double-click in the Instances panel does to the document once it is in front: select the
/// instance, and FRAME it (brief-find-instance-panel.md R-fi-9/R-fi-10).
///
/// <para><b>Frames, never merely reveals.</b> Find means "show me it": Sort Placement and Update
/// Layout pan only so as to keep the user's working zoom, and that is the wrong answer here — the
/// part is found in a design too large to see whole, so the view zooms in on it.</para>
///
/// <para>Separate from the workspace, which only has to bring the right document forward first, so
/// the selection and the framing are testable against a bare view model.</para>
/// </summary>
public static class InstanceReveal
{
    /// <summary>The framed square is this many times the instance's own largest side — the part
    /// with its immediate neighbourhood, rather than edge to edge.</summary>
    public const double FrameFactor = 3.0;

    /// <summary>The smallest schematic frame, in world units — twelve connection-grid squares at the
    /// default grid, so a two-pin part is not blown up to fill the window.</summary>
    public const double SchematicMinSpan = 1200.0;

    /// <summary>The frame for a layout instance with no extent to measure, in µm.</summary>
    public const double LayoutFallbackSpanUm = 100.0;

    // ── Schematic ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects <paramref name="component"/> alone and asks <paramref name="document"/>'s view to frame
    /// it. The box is the component's <c>FullBb</c> — glyph PLUS labels, what is actually drawn —
    /// from the render model. Returns false when the component is not in the document's active frame.
    /// </summary>
    public static bool Reveal(SchematicDocument document, EditableComponent component)
    {
        var vm = document.ActiveViewModel;
        if (!vm.EditModel.Components.Contains(component)) return false;

        vm.Selection.SelectOne(component.Id);

        var rc = FindRendered(vm, component.Id);
        var box = rc is not null
            ? (rc.FullBbMinX, rc.FullBbMinY, rc.FullBbMaxX, rc.FullBbMaxY)
            : (component.X, component.Y, component.X, component.Y);

        var f = SchematicFrame(box.Item1, box.Item2, box.Item3, box.Item4);
        document.RequestZoomToWorldRect(f.MinX, f.MinY, f.MaxX, f.MaxY);
        return true;
    }

    private static SchematicComponent? FindRendered(SchematicViewModel vm, string id)
    {
        if (vm.RenderModel is not { } model) return null;
        foreach (var c in model.Components)
            if (string.Equals(c.Id, id, StringComparison.Ordinal)) return c;
        return null;
    }

    /// <summary>A square about the box's centre, <see cref="FrameFactor"/> times its largest side and
    /// never smaller than <see cref="SchematicMinSpan"/>.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) SchematicFrame(
        double minX, double minY, double maxX, double maxY)
    {
        double side = Math.Max(Math.Max(maxX - minX, maxY - minY) * FrameFactor, SchematicMinSpan);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        return (cx - side / 2, cy - side / 2, cx + side / 2, cy + side / 2);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects <paramref name="instance"/> in <paramref name="vm"/> and asks the canvas to frame it.
    /// The index is looked up HERE, at the moment of the gesture, because any delete since the list
    /// was built shifted it. Returns false when the instance is no longer in the layout.
    /// </summary>
    public static bool Reveal(LayoutEditorViewModel vm, LayoutInstance instance)
    {
        int index = vm.Model.Instances.IndexOf(instance);
        if (index < 0) return false;

        vm.SelectInstance(index);
        vm.RequestZoomToRegion(LayoutFrame(CellHierarchy.InstanceBbox(instance, vm.InstanceBaseDir),
                                           instance, vm.Model.DbuPerMicron));
        return true;
    }

    /// <summary>
    /// A square about the instance's box, <see cref="FrameFactor"/> times its largest side. <b>An
    /// empty box still frames something</b> — the placement's origin at
    /// <see cref="LayoutFallbackSpanUm"/> — because a part that cannot be measured (a reference that
    /// no longer resolves) is often exactly why somebody is looking for it.
    /// </summary>
    public static Bbox LayoutFrame(Bbox box, LayoutInstance instance, int dbuPerMicron)
    {
        long cx, cy;
        double side;

        if (box.IsEmpty || (box.MaxX == box.MinX && box.MaxY == box.MinY))
        {
            cx = instance.X;
            cy = instance.Y;
            side = LayoutFallbackSpanUm * Math.Max(dbuPerMicron, 1);
        }
        else
        {
            cx = box.MinX / 2 + box.MaxX / 2;
            cy = box.MinY / 2 + box.MaxY / 2;
            side = Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY) * FrameFactor;
        }

        long half = (long)Math.Ceiling(side / 2);
        return new Bbox(cx - half, cy - half, cx + half, cy + half);
    }
}
