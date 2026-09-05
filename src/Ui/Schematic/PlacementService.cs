using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// App-level singleton that tracks the armed placement state.  Framework-free (no Avalonia).
/// The palette ARMS a placement here; every schematic canvas READS this state.
/// </summary>
public sealed partial class PlacementService : ObservableObject
{
    [ObservableProperty]
    private PendingPlacement? _pending;

    /// <summary>
    /// Arm <paramref name="kind"/>/<paramref name="portCount"/>.
    /// If already armed with the same kind+portCount, disarm (toggle off).
    /// If armed with a different kind, switch.
    /// </summary>
    public void Toggle(SymbolKind kind, int portCount)
    {
        Pending = (Pending?.Kind == kind && Pending?.PortCount == portCount)
            ? null
            : new PendingPlacement(kind, portCount);
    }

    /// <summary>
    /// Arm a palette entry. A built-in entry behaves exactly as <see cref="Toggle(SymbolKind,int)"/>;
    /// an entry from an imported kit is identified by its kit+part id, since every kit part shares
    /// one <see cref="SymbolKind"/> and comparing kinds would treat them all as the same entry.
    /// </summary>
    public void Toggle(PaletteItem item)
    {
        if (item.Pdk is not { } pdk)
        {
            Toggle(item.Kind, item.PortCount);
            return;
        }

        bool alreadyArmed = Pending?.Pdk is { } cur &&
                            string.Equals(cur.KitName, pdk.KitName, StringComparison.Ordinal) &&
                            string.Equals(cur.PartId,  pdk.PartId,  StringComparison.Ordinal);

        Pending = alreadyArmed ? null : new PendingPlacement(item.Kind, item.PortCount, SymbolRotation.R0, pdk);
    }

    /// <summary>
    /// Arm an ordinary workspace CELL by its absolute folder — the cell picker's answer, and the
    /// click-to-place counterpart of dragging that same cell out of the Project Tree.
    ///
    /// <para>Always arms, never toggles. The picker is a deliberate act that has just cost the user a
    /// dialog; disarming because the same cell happened to be armed already would answer a click with
    /// nothing at all.</para>
    /// </summary>
    public void ArmCell(string cellAbsDir)
    {
        if (string.IsNullOrWhiteSpace(cellAbsDir)) return;
        // SymbolKind.Generic is the same placeholder a kit part is armed with — the real symbol is
        // resolved from the cell itself, both for the ghost and at the commit.
        Pending = new PendingPlacement(SymbolKind.Generic, 2, SymbolRotation.R0, null, cellAbsDir);
    }

    /// <summary>Clear the armed state.</summary>
    public void Disarm() => Pending = null;

    /// <summary>
    /// Rotate the armed placement by one step.
    /// R (clockwise=false) = CCW: R0→R90→R180→R270.
    /// Ctrl+R (clockwise=true) = CW: R0→R270→R180→R90.
    /// No-op when nothing is armed.
    /// </summary>
    public void Rotate(bool clockwise)
    {
        if (Pending is null) return;
        Pending = Pending with { Rotation = Step(Pending.Rotation, clockwise) };
    }

    private static SymbolRotation Step(SymbolRotation r, bool cw) => cw
        ? r switch
        {
            SymbolRotation.R0   => SymbolRotation.R270,
            SymbolRotation.R270 => SymbolRotation.R180,
            SymbolRotation.R180 => SymbolRotation.R90,
            _                   => SymbolRotation.R0,
        }
        : r switch
        {
            SymbolRotation.R0   => SymbolRotation.R90,
            SymbolRotation.R90  => SymbolRotation.R180,
            SymbolRotation.R180 => SymbolRotation.R270,
            _                   => SymbolRotation.R0,
        };
}
