// The canvas's "Trace Impedance" command (round-7 field report): right-click copper, get its Z0.
// The work is TraceImpedanceProbe in src/Design; this file picks the layer the click means, hands
// the probe the same flattened artwork a DRC run checks, and posts the answer to Messages.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Ui.Layout;

public partial class LayoutEditorViewModel
{
    /// <summary>
    /// The copper layer a probe at (<paramref name="x"/>, <paramref name="y"/>) means, or null where
    /// there is no stackup-bound copper there. The CURRENT drawing layer when it has copper at the
    /// point — that is how a user says which of two stacked traces they mean — otherwise the highest
    /// visible copper in the stackup, never the draw order, which an imported technology can invert.
    /// </summary>
    public LayerKey? TraceImpedanceLayerAt(long x, long y)
    {
        if (Technology is not { } tech) return null;

        var under = Regions.CopperLayersAt(Model.Shapes, tech, x, y);
        if (under.Count == 0) return null;
        if (under.Contains(CurrentLayerKey) && ResolveLayerDef(CurrentLayerKey).Visible) return CurrentLayerKey;

        int Rank(LayerKey k)
        {
            for (int i = 0; i < tech.Stackup.Layers.Count; i++)
                if (tech.Stackup.Layers[i].DrawingLayers.Contains(k)) return i;
            return int.MaxValue;
        }

        return under.OrderBy(k => ResolveLayerDef(k).Visible ? 0 : 1).ThenBy(Rank)
                    .Select(k => (LayerKey?)k).FirstOrDefault();
    }

    /// <summary>Probes the trace at the point and posts the result to Messages. Null when there is
    /// no technology or no copper there — the menu item is not offered then.</summary>
    public TraceImpedanceResult? ProbeTraceImpedance(long x, long y)
    {
        if (Technology is not { } tech || TraceImpedanceLayerAt(x, y) is not { } layer) return null;

        // The artwork a DRC run sees, so a trace drawn inside a placed cell is probed too.
        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", tech, ResolveTechAt, resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? Model.Shapes : flat.Shapes;

        var result = TraceImpedanceProbe.Probe(shapes, tech, Model.DbuPerMicron, x, y, layer, DisplayUnit);

        if (_messageSink is { } messages)
        {
            // ONE line (owner, 2026-09-24): each warning rides on it as a short tag.
            if (result.Ok && result.Flags.Count == 0) messages.Info(result.Summary());
            else messages.Warning(result.Summary());
        }
        return result;
    }
}
