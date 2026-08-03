using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Builds a placeable symbol from the terminals a kit's symbol LIBRARY declares.
///
/// <para><b>Why this exists beside <see cref="DsnSymbolReader"/>.</b> That reader takes a full
/// drawing — body and pins — from one file per symbol. A library gives only what its records state
/// unambiguously: named terminals at known positions, shared by many parts. So the pins are the
/// kit's, exactly where it put them, and the body is circuitRF's own — a box the pins lead into.
/// Drawing a body we cannot read would be inventing the kit's artwork; drawing none would leave
/// pins floating in space with nothing to grab.</para>
///
/// <para><b>Pin placement goes through the same scale, snap and axis flip the drawing reader
/// uses</b> — deliberately shared rather than reimplemented. Pins must land on exact multiples of
/// the connection grid or a wire will not attach, and two rules for that would drift apart at the
/// first change. It also means a part backed by a library and one backed by a drawing put their
/// pins in the same places.</para>
/// </summary>
internal static class KitTemplateSymbol
{
    /// <summary>Half-size the body never shrinks below, so a colinear part still has one.</summary>
    private const double MinHalfSpan = DsnSymbolReader.PinGrid;

    /// <summary>Null when the template declares no terminals — there is nothing to place.</summary>
    internal static Symbol? Build(IReadOnlyList<KitSymbolPin>? pins)
    {
        if (pins is null || pins.Count == 0) return null;

        double minX = pins.Min(p => (double)p.X), maxX = pins.Max(p => (double)p.X);
        double minY = pins.Min(p => (double)p.Y), maxY = pins.Max(p => (double)p.Y);

        // A power-of-ten scale, so a library authored in any drawing unit lands at a legible size
        // without this code knowing anything about that kit.
        double scale = DsnSymbolReader.ChooseScale(Math.Max(maxX - minX, maxY - minY));

        var placed = new List<SymbolPin>(pins.Count);
        for (int i = 0; i < pins.Count; i++)
            placed.Add(new SymbolPin(
                DsnSymbolReader.SnapToPinGrid(pins[i].X * scale),
                DsnSymbolReader.SnapToPinGrid(-pins[i].Y * scale),   // library is Y-up, symbols Y-down
                i + 1,
                string.IsNullOrWhiteSpace(pins[i].Name) ? (i + 1).ToString() : pins[i].Name));

        // The body: the pins' own bounding box drawn in one grid, never smaller than MinHalfSpan.
        // The floor is what keeps a two-terminal part — whose pins are colinear, so one dimension of
        // the box is zero — from collapsing into a line the user cannot see or click.
        double pMinX = placed.Min(p => p.LocalX), pMaxX = placed.Max(p => p.LocalX);
        double pMinY = placed.Min(p => p.LocalY), pMaxY = placed.Max(p => p.LocalY);
        double cx = (pMinX + pMaxX) / 2, cy = (pMinY + pMaxY) / 2;
        double hx = Math.Max((pMaxX - pMinX) / 2 - DsnSymbolReader.PinGrid, MinHalfSpan);
        double hy = Math.Max((pMaxY - pMinY) / 2 - DsnSymbolReader.PinGrid, MinHalfSpan);

        double bx0 = cx - hx, bx1 = cx + hx, by0 = cy - hy, by1 = cy + hy;

        var primitives = new List<SymbolPrimitive>
        {
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx0, by0, bx1, by0),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx1, by0, bx1, by1),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx1, by1, bx0, by1),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx0, by1, bx0, by0),
        };

        // A lead from each pin to the nearest point on the body. Clamping rather than picking a side
        // keeps this correct for every arrangement a library can state, including a pin that sits
        // inside the body — which draws nothing rather than a stray mark.
        foreach (var pin in placed)
        {
            double tx = Math.Clamp(pin.LocalX, bx0, bx1);
            double ty = Math.Clamp(pin.LocalY, by0, by1);
            if (Math.Abs(tx - pin.LocalX) > 0.5 || Math.Abs(ty - pin.LocalY) > 0.5)
                primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                                 pin.LocalX, pin.LocalY, tx, ty));
        }

        return new Symbol(primitives, placed);
    }
}
