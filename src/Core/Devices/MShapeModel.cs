using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Drawn metal, MSHAPE (brief-pcd-0 §4.3, PCD-3): one terminal per <c>PinX[k]</c> and nothing between them. It stamps
/// nothing, so its terminals are open in a circuit simulation; whether that counts as a circuit model is the instance's
/// <c>Model</c> word, which the EM switch reads (EmModelSwitch.WithoutCircuitModel), not this.
/// </summary>
public sealed class MShapeModel(int pinCount) : ComponentModel
{
    public override int       PortCount => pinCount;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary>The pin count an instance's keys state: k = 1, 2, … while <c>PinX[k]</c> is one of them.</summary>
    public static int PinCountOf(IEnumerable<string> keys)
    {
        var set = keys as IReadOnlySet<string> ?? keys.ToHashSet(StringComparer.Ordinal);
        int k = 0;
        while (set.Contains($"PinX[{k + 1}]")) k++;
        return k;
    }
}
