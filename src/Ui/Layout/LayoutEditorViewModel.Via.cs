using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands.Layout;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase brief-via-primitive-and-stackup.md §4.2/R-via-6 — "Convert to Via": the recovery path for the
/// intuitive-but-unpaired "draw a bare Circle on the drill layer" gesture (§4.2's own MMIC-genuine
/// example). Mirrors <c>.Flatten.cs</c>'s split-out-of-the-main-file convention.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>R-via-5's own identity for "drill-function layer" — any layer named in a
    /// <see cref="StackupKind.Via"/> stackup entry's <c>DrawingLayers</c>, the same set
    /// <see cref="ViaToolAvailability"/> and <c>GerberExport</c>'s unpaired-circle report both use.</summary>
    private HashSet<LayerKey> DrillLayerKeys() =>
        Technology is { } tech
            ? [.. tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).SelectMany(l => l.DrawingLayers)]
            : [];

    /// <summary>R-via-6: enabled for a selection of exactly one Circle on a drill layer, or that
    /// Circle plus one concentric (same center) second Circle to use as the pad. Any other selection
    /// shape — an instance, a non-Circle shape, two non-concentric circles, two circles both/neither on
    /// a drill layer — is disabled with a reason (R13a), never a silent no-op.</summary>
    public LayoutCommandAvailability ConvertToViaAvailability
    {
        get
        {
            const string usage = "Convert to Via: select one circle on a drill layer (optionally with a concentric pad circle).";

            if (_selectedInstanceIndices.Count > 0 || SelectedIndices.Count is not (1 or 2))
                return LayoutCommandAvailability.Disabled(usage);

            var shapes = SelectedIndices.Where(i => i >= 0 && i < Model.Shapes.Count).Select(i => Model.Shapes[i]).ToList();
            if (shapes.Count != SelectedIndices.Count || shapes.Any(s => s is not CircleShape))
                return LayoutCommandAvailability.Disabled(usage);

            var drillKeys = DrillLayerKeys();
            if (drillKeys.Count == 0)
                return LayoutCommandAvailability.Disabled("Convert to Via: this technology's stackup has no via layer.");

            var circles = shapes.Cast<CircleShape>().ToList();
            var drillCircles = circles.Where(c => drillKeys.Contains(c.Layer)).ToList();
            if (drillCircles.Count != 1)
                return LayoutCommandAvailability.Disabled("Convert to Via: select exactly one circle on a drill layer.");

            if (circles.Count == 2)
            {
                var other = circles.Single(c => !ReferenceEquals(c, drillCircles[0]));
                if (other.Cx != drillCircles[0].Cx || other.Cy != drillCircles[0].Cy)
                    return LayoutCommandAvailability.Disabled(
                        "Convert to Via: the second circle must be concentric with the drill circle to use as its pad.");
            }

            return LayoutCommandAvailability.Enabled;
        }
    }

    /// <summary>R-via-6: "produce a ViaShape using the circle's diameter as the barrel and the
    /// technology's default pad — one undoable action, via ReplaceShapesCommand. If a concentric pad
    /// circle is selected too, use its diameter as the pad." One <see cref="ReplaceShapesCommand"/> —
    /// undo restores the original circle(s) at their original index(es), exactly like every other
    /// N-removed/M-added shape transform (Flatten to Polygon, booleans).</summary>
    public void CommitConvertToVia()
    {
        if (!ConvertToViaAvailability.CanExecute) return;

        var drillKeys = DrillLayerKeys();
        var indexed = SelectedIndices
            .Where(i => i >= 0 && i < Model.Shapes.Count)
            .Select(i => (Index: i, Shape: (CircleShape)Model.Shapes[i]))
            .ToList();

        var drillEntry = indexed.First(e => drillKeys.Contains(e.Shape.Layer));
        var padEntry = indexed.FirstOrDefault(e => e.Index != drillEntry.Index);

        long padSize = padEntry.Shape is { } pad ? Math.Max(pad.R, 1) * 2
            : Technology is { DefaultViaPadDbu: > 0 } t ? t.DefaultViaPadDbu : 500_000; // 0.5 mm fallback

        var via = new ViaShape
        {
            Layer     = drillEntry.Shape.Layer,
            Net       = drillEntry.Shape.Net,
            X         = drillEntry.Shape.Cx,
            Y         = drillEntry.Shape.Cy,
            DrillSize = Math.Max(drillEntry.Shape.R, 1) * 2,
            PadSize   = padSize,
        };

        var removed = indexed.Select(e => (e.Index, (LayoutShape)e.Shape)).ToList();
        Execute(new ReplaceShapesCommand(Model, removed, [via], "Convert to Via"));

        int newIndex = removed.Min(r => r.Index);
        SetSelection([newIndex]);
    }
}
