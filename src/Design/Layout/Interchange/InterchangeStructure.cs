// The shared, format-agnostic interchange layer (docs/design/layout-view.md §8 R15): "All three
// formats go through one neutral in-memory model and one shared layer-mapping dialog. Format-specific
// code touches only bytes and records, never editor state." InterchangeStructure IS that neutral
// model — deliberately just the existing, already format-agnostic LayoutShape/LayoutInstance/LayerKey
// types plus a name, not a parallel object model. GDSII (this brief) and, later, DXF/Gerber (L4b/L4c)
// import/export through this same shape.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One cell/structure worth of neutral geometry — the unit both readers and writers of every
/// interchange format exchange. <see cref="Name"/> is the circuitRF cell name on import, or the
/// (already-mangled) structure name on export — callers decide which.</summary>
public sealed record InterchangeStructure(string Name, List<LayoutShape> Shapes, List<LayoutInstance> Instances);
