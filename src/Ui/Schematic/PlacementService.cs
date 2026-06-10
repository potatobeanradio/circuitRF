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
