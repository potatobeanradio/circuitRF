// L3a — instance (SREF) and array (AREF) rendering (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md).
// Partial-class extension of LayoutRenderer, kept in its own file per this codebase's convention for a
// large concern that deserves its own home (mirrors LayoutEditorViewModel's per-concern partial files).
//
// R-L3a-3 — the phase's headline requirement: "a sub-cell's geometry is built once and drawn once per
// placement under a matrix." A resolved sub-cell is compiled EXACTLY ONCE into one aggregate fill path
// and one aggregate stroke path PER LAYER (reusing BuildShapePath so PathsConstructed still counts real
// path construction), cached by the LayoutView INSTANCE CellLayoutResolver's own (path, mtime) cache
// returns — a ConditionalWeakTable keyed on that reference means the compile cache and the resolver
// cache invalidate TOGETHER for free (a file change produces a NEW LayoutView instance on the next
// resolve, which is simply a cache miss here; the old compiled entry becomes unreachable and its
// SKPaths are reclaimed via SKObject's own finalizer — no separate invalidation call needed).
//
// This composes with L2c's shape-local path cache for exactly the reason the brief calls out: R-L2c-3
// cached shape paths in SHAPE-LOCAL space specifically so a pan (which moves the per-frame path-space
// origin) never invalidates them. The SAME property is what makes a COMPILED CELL reusable across every
// placement of every instance referencing it: the compiled paths live in CELL-LOCAL path space (origin
// at the sub-cell's own (0,0), never the per-frame viewport-anchored one), so the per-placement SKMatrix
// is the ONLY thing that varies frame to frame or placement to placement. Had L2c cached in path space
// instead of shape-local space, this reuse would not be possible — the second time that decision has
// paid off.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

public static partial class LayoutRenderer
{
    private sealed class CompiledLayerGeometry
    {
        public required LayerKey Key;
        public readonly SKPath Fill = new();
        public readonly SKPath Stroke = new();
    }

    /// <summary>One resolved cell's compiled geometry — every one of its own shapes AND every one of
    /// its own instances' geometry (recursively, up to <see cref="CellHierarchy.MaxDepth"/>), flattened
    /// into THIS cell's own local path space. <see cref="BrokenPlaceholders"/> holds one dashed marker
    /// rect (in this cell's own local path space) per NESTED broken/cyclic/too-deep instance reference —
    /// deliberately without a text label (see the file header for why a top-level broken instance gets
    /// full labeled treatment and a nested one does not) and, deliberately, only ONE mark regardless of
    /// that nested instance's own array size (a documented corner-case simplification: a broken
    /// reference nested inside an array of an array is rare enough that one representative mark, not
    /// Rows*Cols of them, is an acceptable trade for not needing per-array-cell placeholder bookkeeping
    /// at arbitrary compile depth).</summary>
    private sealed class CompiledCellGeometry
    {
        public readonly List<CompiledLayerGeometry> Layers = [];
        public readonly List<SKRect> BrokenPlaceholders = [];
    }

    /// <summary>Compiled-cell cache, keyed by LayoutView REFERENCE — see the file header for why this
    /// piggybacks on <see cref="CellLayoutResolver"/>'s own cache lifecycle instead of maintaining a
    /// second, separately-invalidated cache.</summary>
    private static readonly ConditionalWeakTable<LayoutView, CompiledCellGeometry> _cellCompileCache = new();

    /// <summary>
    /// Evicts <paramref name="view"/>'s compiled geometry, if any — brief-L3b-hierarchy-navigation.md
    /// §2/R-L3b-1's other invalidation half. A push-in session's <see cref="LayoutView"/> is mutated IN
    /// PLACE across edits (the same reference persists), unlike a fresh disk-load, which produces a
    /// NEW reference the compile cache would simply never have seen before — so an in-place-edited
    /// session's stale compiled paths need this EXPLICIT eviction; a disk-reloaded reference self-heals
    /// via <see cref="ConditionalWeakTable{TKey,TValue}"/> just going stale/unreachable. Safe to call
    /// with a view that was never compiled (no-op).
    /// </summary>
    internal static void InvalidateCompiledGeometry(LayoutView view) => _cellCompileCache.Remove(view);

    private static CompiledCellGeometry CompileCell(LayoutView subView, Technology? tech, string subBaseDir,
        HashSet<string> visiting, int depth, LayoutFrameCounters? counters)
    {
        if (_cellCompileCache.TryGetValue(subView, out var cached)) return cached;

        var compiled = new CompiledCellGeometry();
        double dbuToUm = 1.0 / Math.Max(1, subView.DbuPerMicron);
        var localPs = new PathSpace(0, 0, dbuToUm);

        var byLayer = new Dictionary<LayerKey, CompiledLayerGeometry>();
        CompiledLayerGeometry LayerFor(LayerKey key)
        {
            if (byLayer.TryGetValue(key, out var cl)) return cl;
            cl = new CompiledLayerGeometry { Key = key };
            byLayer[key] = cl;
            compiled.Layers.Add(cl);
            return cl;
        }

        // Own shapes. Bitmaps (not geometry, R-bmp-3) and Labels (text, not baked into a reusable
        // path aggregate — see the file header) are not represented in compiled instance geometry;
        // both are documented gaps in the L3a completion note, not silent omissions.
        foreach (var shape in subView.Shapes)
        {
            if (shape is BitmapShape or LabelShape) continue;
            using var path = BuildShapePath(shape, localPs, counters);
            if (path is null || path.IsEmpty) continue;
            var cl = LayerFor(shape.Layer);
            cl.Fill.AddPath(path);
            cl.Stroke.AddPath(path);
        }

        // Own instances — recursively compiled and flattened into THIS cell's local space, so a
        // placement of THIS cell anywhere else needs no further per-frame recursion at all.
        foreach (var nested in subView.Instances)
        {
            var step = CellHierarchy.ResolveForWalk(nested, subBaseDir, visiting, depth);
            if (step.State != InstanceResolutionState.Resolved)
            {
                var (ox0, oy0) = LayoutInstanceTransform.ArrayCellOrigin(nested, 0, 0);
                long half = CellHierarchy.PlaceholderHalfExtentDbu;
                compiled.BrokenPlaceholders.Add(NormalizedRect(
                    localPs.X(ox0 - half), localPs.Y(oy0 - half), localPs.X(ox0 + half), localPs.Y(oy0 + half)));
                continue;
            }

            visiting.Add(step.ResolvedCellDir!);
            var child = CompileCell(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!), visiting, depth + 1, counters);
            visiting.Remove(step.ResolvedCellDir!);

            var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(nested);
            int rows = Math.Max(1, nested.Rows), cols = Math.Max(1, nested.Cols);
            for (int r = 0; r < rows; r++)
            for (int col = 0; col < cols; col++)
            {
                var (originX, originY) = LayoutInstanceTransform.ArrayCellOrigin(nested, r, col);
                var m = new SKMatrix
                {
                    ScaleX = (float)a, SkewX = (float)b, TransX = localPs.X(originX),
                    SkewY = (float)c, ScaleY = (float)d, TransY = localPs.Y(originY),
                    Persp2 = 1f,
                };
                foreach (var childLayer in child.Layers)
                {
                    var cl = LayerFor(childLayer.Key);
                    cl.Fill.AddPath(childLayer.Fill, in m);
                    cl.Stroke.AddPath(childLayer.Stroke, in m);
                }
                foreach (var rect in child.BrokenPlaceholders)
                    compiled.BrokenPlaceholders.Add(m.MapRect(rect));
            }
        }

        _cellCompileCache.AddOrUpdate(subView, compiled);
        return compiled;
    }

    /// <summary>Draws every candidate instance placement — R-L3a §4/§5/§8 (culling already applied by
    /// the caller's spatial-index query; LOD and the missing/broken placeholder are decided here).</summary>
    private static void DrawInstances(SKCanvas canvas, LayoutView view, Technology? tech,
        IReadOnlyList<LayoutSpatialEntry> candidates, IReadOnlyDictionary<int, LayoutInstance> dragOverrides,
        LayoutRenderOptions opts, PathSpace ps, double scaleUm, LayoutFrameCounters counters,
        HashSet<string> missingCellRefs)
    {
        string baseDir = opts.BaseDir ?? "";
        double lodThreshold = opts.LodPixelThreshold > 0 ? opts.LodPixelThreshold : DefaultLodPixelThreshold;
        double devicePxPerDbu = scaleUm * ps.DbuToUm;
        var layerMap = tech?.Layers.ToDictionary(l => l.Key);

        foreach (var entry in candidates)
        {
            if (entry.Kind != SpatialEntryKind.Instance) continue;
            if (entry.Index < 0 || entry.Index >= view.Instances.Count) continue;
            // A live move-drag renders the translated preview clone in place of the stored instance —
            // the model itself is untouched until the drag commits (mirrors dragOverrides for shapes).
            var inst = dragOverrides.TryGetValue(entry.Index, out var ov) ? ov : view.Instances[entry.Index];

            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var step = CellHierarchy.ResolveForWalk(inst, baseDir, visiting, 0);

            if (step.State != InstanceResolutionState.Resolved)
            {
                DrawBrokenInstancePlaceholder(canvas, inst, step.State, ps, scaleUm, opts.Theme, counters);
                if (!string.IsNullOrEmpty(inst.CellRef)) missingCellRefs.Add(inst.CellRef);
                continue;
            }

            var overallBbox = CellHierarchy.InstanceBbox(inst, baseDir);
            if (overallBbox.IsEmpty) continue;
            double screenW = (overallBbox.MaxX - overallBbox.MinX) * devicePxPerDbu;
            double screenH = (overallBbox.MaxY - overallBbox.MinY) * devicePxPerDbu;
            if (Math.Max(screenW, screenH) < lodThreshold)
            {
                DrawMinimalInstanceMark(canvas, overallBbox, ps, scaleUm);
                counters.InstancesDrawn++;
                continue;
            }

            visiting.Add(step.ResolvedCellDir!);
            var compiled = CompileCell(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!), visiting, 1, counters);
            visiting.Remove(step.ResolvedCellDir!);

            var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(inst);
            int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);

            // Resolved once per candidate instance, reused across every placement (R-L3a-3's "N matrix
            // draws" — not N paint allocations). Magnification is baked into the stroke width HERE
            // (gate 3): the compiled Stroke path is unscaled cell-local geometry, so the on-screen
            // width after this instance's own Mag (part of the placement matrix) must be pre-divided
            // by Mag to still land on GeometryStrokeDevicePixels device pixels.
            var layerVisuals = new List<(CompiledLayerGeometry Layer, SKPaint FillPaint, SKPaint StrokePaint)>();
            double strokeScale = scaleUm * Math.Max(Math.Abs(inst.Mag), 1e-9);
            foreach (var layer in compiled.Layers)
            {
                LayerDef def = layerMap is not null && layerMap.TryGetValue(layer.Key, out var found)
                    ? found : FallbackPalette.For(layer.Key);
                if (!def.Visible) continue;
                var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);
                byte fillAlpha = (byte)Math.Clamp(Math.Round(def.FillOpacity * 255.0), 0, 255);
                layerVisuals.Add((
                    layer,
                    new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(fillAlpha) },
                    new SKPaint
                    {
                        IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = DevicePixelsToPathSpace(strokeScale, GeometryStrokeDevicePixels),
                        Color = color.WithAlpha(255),
                    }));
            }

            using var brokenFillPaint = compiled.BrokenPlaceholders.Count > 0
                ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = opts.Theme.Warning.WithAlpha(40) } : null;
            using var brokenStrokePaint = compiled.BrokenPlaceholders.Count > 0
                ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels), Color = opts.Theme.Warning, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0) }
                : null;

            try
            {
                for (int r = 0; r < rows; r++)
                for (int col = 0; col < cols; col++)
                {
                    var (originX, originY) = LayoutInstanceTransform.ArrayCellOrigin(inst, r, col);
                    var m = new SKMatrix
                    {
                        ScaleX = (float)a, SkewX = (float)b, TransX = ps.X(originX),
                        SkewY = (float)c, ScaleY = (float)d, TransY = ps.Y(originY),
                        Persp2 = 1f,
                    };

                    canvas.Save();
                    canvas.Concat(in m);
                    foreach (var (layer, fillPaint, strokePaint) in layerVisuals)
                    {
                        if (!layer.Fill.IsEmpty) { canvas.DrawPath(layer.Fill, fillPaint); counters.DrawCalls++; }
                        if (!layer.Stroke.IsEmpty) { canvas.DrawPath(layer.Stroke, strokePaint); counters.DrawCalls++; }
                    }
                    if (brokenFillPaint is not null && brokenStrokePaint is not null)
                        foreach (var rect in compiled.BrokenPlaceholders)
                        {
                            canvas.DrawRect(rect, brokenFillPaint);
                            canvas.DrawRect(rect, brokenStrokePaint);
                            counters.DrawCalls += 2;
                        }
                    canvas.Restore();
                    counters.InstancesDrawn++;
                }
            }
            finally
            {
                foreach (var (_, fp, sp) in layerVisuals) { fp.Dispose(); sp.Dispose(); }
            }
        }
    }

    /// <summary>R-L3a-1 — a missing/broken TOP-LEVEL instance renders a labelled dashed placeholder at
    /// its stored extent, array-expanded (each array cell is independently a placeholder — there is no
    /// real geometry to have compressed via the array in the first place), and remains fully selectable
    /// (the caller's spatial index already indexes it via <c>CellHierarchy.PlaceholderBbox</c>).</summary>
    private static void DrawBrokenInstancePlaceholder(SKCanvas canvas, LayoutInstance inst, InstanceResolutionState state,
        PathSpace ps, double scaleUm, LayoutRenderTheme theme, LayoutFrameCounters counters)
    {
        string label = state switch
        {
            InstanceResolutionState.NotFound       => "Not Found",
            InstanceResolutionState.PrimaryMissing => "No Layout",
            InstanceResolutionState.Cyclic         => "Cyclic Ref",
            InstanceResolutionState.DepthExceeded  => "Too Deep",
            _                                       => "Broken",
        };

        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, GeometryStrokeDevicePixels),
            Color = theme.Warning, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Warning.WithAlpha(40) };
        // LayoutTextOutline.ResolveTypeface (not SkiaFonts.PlexRegular directly) — the same seam
        // LayoutRenderer.DrawLabelText uses, so this text ALSO honors LayoutTextOutline.
        // TestOverrideTypeface (SkiaFonts.PlexRegular cannot load without a live Avalonia app host,
        // confirmed empirically in the L1-era label work — see src/Ui/CLAUDE.md).
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(LabelFontStyle.Regular), Math.Max(1f, DevicePixelsToPathSpace(scaleUm, 11.0)));
        using var textPaint = new SKPaint { IsAntialias = true, Color = theme.Warning };

        long half = CellHierarchy.PlaceholderHalfExtentDbu;
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            var (ox, oy) = LayoutInstanceTransform.ArrayCellOrigin(inst, r, c);
            var rect = NormalizedRect(ps.X(ox - half), ps.Y(oy - half), ps.X(ox + half), ps.Y(oy + half));
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);
            counters.DrawCalls += 2;

            float textWidth = font.MeasureText(label);
            if (textWidth < rect.Width * 4) // only draw the label when it's not wildly larger than the box
                canvas.DrawText(label, rect.MidX - textWidth / 2f, rect.MidY, SKTextAlign.Left, font, textPaint);
            counters.InstancesDrawn++;
        }
    }

    /// <summary>R-L3a §4 — a placement (here, the whole instance including its array, since an
    /// out-of-view-individually array cell is by definition also below threshold) whose overall screen
    /// extent falls under the LOD threshold draws as ONE minimal mark instead of compiling/descending
    /// into the sub-cell at all. Deliberately a neutral, fixed marker color rather than any of the
    /// sub-cell's own layer colors — consulting those would require exactly the descent this exists to
    /// avoid.</summary>
    private static readonly SKColor InstanceLodMarkColor = new(148, 148, 148, 200);

    private static void DrawMinimalInstanceMark(SKCanvas canvas, Bbox overallBbox, PathSpace ps, double scaleUm)
    {
        var rect = NormalizedRect(ps.X(overallBbox.MinX), ps.Y(overallBbox.MinY), ps.X(overallBbox.MaxX), ps.Y(overallBbox.MaxY));
        float halfMin = (float)(0.5 * MinimalRectDevicePixelsForInstances / Math.Max(scaleUm, 1e-12));
        float cx = (rect.Left + rect.Right) / 2f, cy = (rect.Top + rect.Bottom) / 2f;
        float w = Math.Max(rect.Width, halfMin * 2f), h = Math.Max(rect.Height, halfMin * 2f);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = InstanceLodMarkColor };
        canvas.DrawRect(new SKRect(cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f), paint);
    }

    private const double MinimalRectDevicePixelsForInstances = 1.0;

    // ── Selection outline + Instance-place ghost (§5/§6) ────────────────────────────────────────

    /// <summary>Accent outline around each selected instance's overall (array-expanded) bbox —
    /// mirrors <see cref="DrawSelectionOutlines"/> for shapes, but a simple bbox rect rather than the
    /// shape's own outline path, since R-L3a-5 selects the instance as a unit, not its contents.</summary>
    private static void DrawInstanceSelectionOutlines(SKCanvas canvas, LayoutView view, IReadOnlyList<int> selected,
        IReadOnlyDictionary<int, LayoutInstance> dragOverrides, LayoutRenderOptions opts, LayoutRenderTheme theme,
        PathSpace ps, double scaleUm)
    {
        string baseDir = opts.BaseDir ?? "";
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, SelectionStrokeDevicePixels),
            Color = theme.Selection,
        };
        foreach (var idx in selected)
        {
            if (idx < 0 || idx >= view.Instances.Count) continue;
            var inst = dragOverrides.TryGetValue(idx, out var ov) ? ov : view.Instances[idx];
            var bbox = CellHierarchy.InstanceBbox(inst, baseDir);
            if (bbox.IsEmpty) continue;
            var rect = NormalizedRect(ps.X(bbox.MinX), ps.Y(bbox.MinY), ps.X(bbox.MaxX), ps.Y(bbox.MaxY));
            canvas.DrawRect(rect, paint);
        }
    }

    /// <summary>The Instance-place tool's live ghost (§6), widened by brief-L3a-followups.md
    /// §4/R-fix-5 for the project-tree drag-and-drop entry point: when <paramref name="pending"/>'s
    /// <c>CellRef</c> RESOLVES, this draws the sub-cell's REAL compiled geometry (reusing
    /// <see cref="CompileCell"/> — the exact same per-layer aggregate paths a committed instance
    /// referencing the same cell already compiles, so this is not new per-frame cost for the common
    /// case of dragging a cell that is also placed elsewhere) under the placement matrix, at reduced
    /// opacity with a dashed accent outline so it still reads as provisional. When it does NOT resolve,
    /// this falls back to the SAME labelled dashed placeholder a committed unresolved instance gets
    /// (<see cref="DrawBrokenInstancePlaceholder"/>) — "matching R-L3a-1," per the brief — so the ghost
    /// never shows a placement it can't actually make. The Instance TOOL's own ghost (armed via the
    /// cell-picker dialog) always hits this same method — the box-only behavior it originally had is
    /// gone, not preserved as a separate code path, since a resolved cell's real geometry is strictly
    /// more informative for both entry points.</summary>
    private static void DrawPendingInstancePlacement(SKCanvas canvas, (LayoutInstance Instance, Bbox Bbox) pending,
        Technology? tech, string baseDir, LayoutRenderTheme theme, PathSpace ps, double scaleUm, LayoutFrameCounters counters)
    {
        if (pending.Bbox.IsEmpty) return;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = CellHierarchy.ResolveForWalk(pending.Instance, baseDir, visiting, 0);

        if (step.State != InstanceResolutionState.Resolved)
        {
            DrawBrokenInstancePlaceholder(canvas, pending.Instance, step.State, ps, scaleUm, theme, counters);
            return;
        }

        visiting.Add(step.ResolvedCellDir!);
        var compiled = CompileCell(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!), visiting, 1, counters);
        visiting.Remove(step.ResolvedCellDir!);

        var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(pending.Instance);
        int rows = Math.Max(1, pending.Instance.Rows), cols = Math.Max(1, pending.Instance.Cols);
        double strokeScale = scaleUm * Math.Max(Math.Abs(pending.Instance.Mag), 1e-9);

        using var ghostStroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = DevicePixelsToPathSpace(strokeScale, GeometryStrokeDevicePixels),
            Color = theme.Selection, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0),
        };
        using var ghostFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Selection.WithAlpha(60) };

        for (int r = 0; r < rows; r++)
        for (int col = 0; col < cols; col++)
        {
            var (originX, originY) = LayoutInstanceTransform.ArrayCellOrigin(pending.Instance, r, col);
            var m = new SKMatrix
            {
                ScaleX = (float)a, SkewX = (float)b, TransX = ps.X(originX),
                SkewY = (float)c, ScaleY = (float)d, TransY = ps.Y(originY),
                Persp2 = 1f,
            };
            canvas.Save();
            canvas.Concat(in m);
            foreach (var layer in compiled.Layers)
            {
                if (!layer.Fill.IsEmpty) canvas.DrawPath(layer.Fill, ghostFill);
                if (!layer.Stroke.IsEmpty) canvas.DrawPath(layer.Stroke, ghostStroke);
            }
            canvas.Restore();
        }

        // The overall (array-expanded) extent outline too, so the full footprint reads clearly even
        // for a sparse sub-cell — mirrors the original box ghost's own outline, now drawn over the
        // real geometry rather than instead of it.
        var rect = NormalizedRect(ps.X(pending.Bbox.MinX), ps.Y(pending.Bbox.MinY), ps.X(pending.Bbox.MaxX), ps.Y(pending.Bbox.MaxY));
        canvas.DrawRect(rect, ghostStroke);
    }
}
