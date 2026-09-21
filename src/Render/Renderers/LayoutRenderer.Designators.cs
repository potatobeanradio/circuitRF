// The reference designator a placement draws — brief-footprint-4b-designators.md §6.
//
// A DEFERRED PASS, NOT THE INSTANCE COMPILE, AND THE REASON IS STRUCTURAL. LayoutRenderer.Instances
// compiles a sub-cell ONCE into a reusable per-layer aggregate SKPath in cell-local space (R-L3a-3),
// which is exactly why it skips a LabelShape inside a placed instance: per-placement text cannot be
// baked into a shared path. That constraint forces the right design anyway — THE DESIGNATOR IS DRAWN
// BY THE PARENT, BECAUSE IT IS THE PARENT'S DATA (R-fp4b-5). The instance compile stays geometry-only
// and both of its label skips stay exactly as they are (R-fp4b-5b).
//
// Copied in shape from the port-glyph pass, which exists for precisely this: collect while walking,
// paint once the frame's content is down. It runs after every layer, every instance, the mesh overlay
// and the rulers, and BELOW the port glyphs — a port renders higher than any geometry by owner
// instruction, and a designator is content, not a marker.
//
// IT IS THE SAME ARTWORK THE EXPORT CARRIES. What is drawn here comes out of FootprintLabel, the one
// function LayoutDesignFlatten (Gerber, DRC, `check`) and the GDSII/DXF exports also call — on the
// technology's SILKSCREEN role, resolved through LandPatternLayers exactly as a land pattern's body
// outline is. A screen that disagreed with the Gerber about what is on the board is the defect
// R-fp4b-4a exists to prevent, and one function is what makes the agreement structural.

using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>
    /// <b>The legibility floor, in device pixels</b> (R-fp4b-5c). Below it a designator is DROPPED,
    /// not shrunk — the judgement <c>DrawBrokenInstancePlaceholder</c> already makes for its own
    /// label, reused as a shape rather than as a constant: text too small to read is not information,
    /// it is a smear, and growing it to stay legible (which is what
    /// <see cref="EffectiveVisibleLabelHeightDbu"/> does for a label the user AUTHORED, and rightly,
    /// since that one is model data that would otherwise be invisible) would cover the very artwork
    /// the designator names at the zoom where a whole board is on screen.
    /// </summary>
    private const double DesignatorLegibilityDevicePixels = 5.0;

    /// <summary>One placement's designator, held back for <see cref="DrawDesignators"/>.</summary>
    internal readonly record struct DeferredDesignator(int Index, LabelShape Label, SKColor Color);

    /// <summary>
    /// Every designator this frame draws, resolved from <paramref name="view"/>'s OWN instances.
    ///
    /// <para>Returns an empty list — having resolved nothing and read no cell — whenever there is
    /// nothing to draw: no instance carries a designator, the technology declares no silkscreen role
    /// (R-fp4b-4c: nothing drawn, nothing RELOCATED to soldermask or the board outline), or that
    /// layer is switched off. The last of those is deliberately not a separate visibility switch:
    /// R-fp4b-6d rules out a global toggle, and hiding the silk layer already hides the body outlines
    /// these designators belong to.</para>
    /// </summary>
    private static List<DeferredDesignator> CollectDesignators(
        LayoutView view, Technology? tech, IReadOnlyList<LayoutSpatialEntry> candidates,
        IReadOnlyDictionary<int, LayoutInstance> dragOverrides, LayoutRenderOptions opts,
        double devicePxPerDbu)
    {
        var none = new List<DeferredDesignator>();
        if (view.Instances.Count == 0) return none;

        bool any = false;
        foreach (var inst in view.Instances)
            if (inst.DesignatorShown && inst.DisplayRefDes is { Length: > 0 }) { any = true; break; }
        if (!any) return none;

        var roles = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, []);
        if (roles.Silkscreen is not { } silk) return none;

        var def = tech?.Layers.FirstOrDefault(l => l.Key.Equals(silk)) ?? FallbackPalette.For(silk);
        if (!def.Visible) return none;
        var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);

        string baseDir = opts.BaseDir ?? "";
        // One resolve per distinct CellRef per frame, on DrawInstances' own terms and for its own
        // reason — a broken reference costs a real filesystem stat every time it is resolved.
        var cellViews = new Dictionary<string, LayoutView?>(StringComparer.Ordinal);

        var list = new List<DeferredDesignator>();
        var seen = new HashSet<int>();
        foreach (var entry in candidates)
        {
            if (entry.Kind != SpatialEntryKind.Instance) continue;
            if (entry.Index < 0 || entry.Index >= view.Instances.Count) continue;
            if (!seen.Add(entry.Index)) continue;

            // A live drag renders the preview clone, exactly as the geometry does — which is what
            // makes dragging the designator itself show up, since that drag publishes an override
            // carrying the new offset and nothing else (R-fp4b-6a).
            var inst = dragOverrides.TryGetValue(entry.Index, out var ov) ? ov : view.Instances[entry.Index];
            if (!inst.DesignatorShown || inst.DisplayRefDes is not { Length: > 0 }) continue;

            // A stored offset needs no cell at all, so a BROKEN reference still draws its designator
            // where the user put it — which is the case where knowing what the part was called matters
            // most. Only the auto position reads the resolved cell.
            LayoutView? cellView = null;
            if (inst.LabelDx is null || inst.LabelDy is null)
            {
                if (!cellViews.TryGetValue(inst.CellRef, out cellView))
                    cellViews[inst.CellRef] = cellView =
                        CellHierarchy.ResolveForWalk(
                            inst, baseDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0).SubView;
            }

            if (FootprintLabel.ShapeFor(inst, cellView, roles, view.DbuPerMicron, silk) is not { } label)
                continue;
            if (label.Height * devicePxPerDbu < DesignatorLegibilityDevicePixels) continue;

            list.Add(new DeferredDesignator(entry.Index, label, color));
        }
        return list;
    }

    /// <summary>
    /// Paints what <see cref="CollectDesignators"/> gathered, and — when the editor has one selected
    /// — the box around it that says so.
    /// </summary>
    private static void DrawDesignators(SKCanvas canvas, List<DeferredDesignator> designators,
        LayoutRenderOptions opts, PathSpace ps, double scaleUm, LayoutFrameCounters counters)
    {
        var selected = opts.Overlay?.SelectedDesignatorIndices;
        using var selectionPaint = selected is { Count: > 0 }
            ? new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = DevicePixelsToPathSpace(scaleUm, SelectionStrokeDevicePixels),
                Color = opts.Theme.Selection,
            }
            : null;

        foreach (var (index, label, color) in designators)
        {
            DrawLabelText(canvas, label, ps, color);
            counters.DrawCalls++;

            if (selectionPaint is null || selected is null || !selected.Contains(index)) continue;
            if (DesignatorWorldBbox(label) is not { IsEmpty: false } bb) continue;
            canvas.DrawRect(
                NormalizedRect(ps.X(bb.MinX), ps.Y(bb.MinY), ps.X(bb.MaxX), ps.Y(bb.MaxY)), selectionPaint);
            counters.DrawCalls++;
        }
    }

    /// <summary>
    /// The world-space box a designator occupies — <b>the one measurement the highlight, the hit test
    /// and the drag all read</b>. Two independently-derived regions is the defect
    /// <c>LayoutHitTest.LabelHitBbox</c>'s own note records an owner report for ('W' and 'i' are the
    /// same width to an estimate that counts characters), so this is that same measurement, not a
    /// second one beside it.
    /// </summary>
    internal static Bbox? DesignatorWorldBbox(LabelShape label) => MeasureLabelWorldBbox(label);
}
