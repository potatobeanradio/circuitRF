// Owner: PCD-4 (brief-pcd-0 §5, seam 8; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

public sealed record PinDropResult(SchematicEditModel? Schematic, string? InstanceName, string? Refusal);

/// <summary>brief-pcd-0 R-pcd4-2: layout-first wiring, a part dropped on a pin.</summary>
public static class PinDrop
{
    /// <summary>part, as the Library palette makes it for placement, added with its pin partPin on targetInstance's pin
    /// targetPin's net; a new designator; through Merge. Never mutates current.</summary>
    public static PinDropResult Apply(SchematicEditModel current, EditableComponent part, int partPin,
                                      string targetInstance, int targetPin)
        => throw new NotImplementedException("PCD-4");
}
