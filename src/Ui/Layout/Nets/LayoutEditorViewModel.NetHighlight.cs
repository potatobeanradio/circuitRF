// brief-ipx-1 R-ipx1-2 — what the Nets panel asks of the editor. The highlight is overlay state: it is not the selection and no
// command writes it, so it adds no undo entry and marks nothing dirty. Select shapes is the selection, through the path every
// other multi-select takes.

using CircuitRF.Design.Layout;
using CircuitRF.Render;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    private IReadOnlyList<LayoutNetHighlight> _netHighlights = [];
    private bool _dimOtherCopper;

    /// <summary>Outlines <paramref name="nets"/> on the canvas; none clears the outline and the dimming with it.</summary>
    public void SetNetHighlights(IReadOnlyList<LayoutNetHighlight> nets, bool dimOtherCopper)
    {
        dimOtherCopper &= nets.Count > 0;
        if (nets.Count == 0 && _netHighlights.Count == 0 && dimOtherCopper == _dimOtherCopper) return;
        _netHighlights = nets;
        _dimOtherCopper = dimOtherCopper;
        RebuildOverlay();
    }

    /// <summary>Frames <paramref name="shapes"/>: those on visible layers where any are, since a hidden shape draws nothing.</summary>
    public void ZoomToShapes(IEnumerable<int> shapes)
    {
        Bbox visible = Bbox.Empty, all = Bbox.Empty;
        foreach (int i in shapes)
        {
            var shape = Model.Shapes[i];
            var box = LayoutGeometry.BboxOf(shape);
            all = all.Union(box);
            if (ResolveLayerDef(shape.Layer).Visible) visible = visible.Union(box);
        }
        RequestZoomToRegion(visible.IsEmpty ? all : visible);
    }

    /// <summary>Makes <paramref name="shapes"/> the selection, leaving out what a click could not select: a hidden or locked
    /// layer's. Properties and Name Net then work on them as on any multi-select.</summary>
    public void SelectShapes(IEnumerable<int> shapes)
    {
        SetSelection(shapes.Where(i =>
        {
            var def = ResolveLayerDef(Model.Shapes[i].Layer);
            return def.Visible && def.Selectable;
        }));
        _cycleCache.Clear();
    }
}
