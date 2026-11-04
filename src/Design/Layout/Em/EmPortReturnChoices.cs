// brief-dfr34-1 — the rows the .cem 3D port table's Return column offers: Automatic, then every conductor in the stack top
// to bottom with the ground-reference ones marked, then the coplanar return. A .c3d's port has no Return: it returns
// through what its (−) end touches.

using CircuitRF.Core.Design;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One row of a Return picker: the value written (empty for Automatic) and what the row shows.</summary>
public sealed record EmPortReturnChoice(string Name, string Display);

public static class EmPortReturnChoices
{
    /// <summary>The first row: the port walks the artwork to its return.</summary>
    public const string AutomaticDisplay = "Automatic";

    /// <summary>Automatic, each conductor of <paramref name="tech"/>'s stack in its order (top to bottom), a ground
    /// reference marked, then <see cref="EmPort3D.SameLayerReturn"/>.</summary>
    public static IReadOnlyList<EmPortReturnChoice> Of(Technology tech)
    {
        var rows = new List<EmPortReturnChoice> { new("", AutomaticDisplay) };
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Conductor)
                rows.Add(new EmPortReturnChoice(l.Name, l.IsGroundReference ? $"{l.Name}  — ground reference" : l.Name));
        rows.Add(new EmPortReturnChoice(EmPort3D.SameLayerReturn, EmPort3D.SameLayerDisplay));
        return rows;
    }
}
