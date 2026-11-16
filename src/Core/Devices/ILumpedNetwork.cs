namespace CircuitRF.Core.Devices;

/// <summary>
/// One element of a fixed lumped network: a resistor, inductor or capacitor between two of the network's nodes. A node is a
/// port index (0 to ports − 1, in the element's own net order), an internal node (ports and up), or
/// <see cref="LumpedNetwork.Ground"/>.
/// </summary>
public readonly record struct LumpedElement(char Kind, int A, int B, double Value);

/// <summary>A network of fixed R, L and C whose values depend on nothing but the element's own parameters.</summary>
/// <param name="Ports">How many of the element's nets the network attaches to.</param>
/// <param name="Internal">How many internal nodes it adds, numbered from <paramref name="Ports"/>.</param>
public sealed record LumpedNetwork(int Ports, int Internal, IReadOnlyList<LumpedElement> Elements)
{
    /// <summary>The node index that stands for ground.</summary>
    public const int Ground = -1;
}

/// <summary>
/// A component that can say whether it IS a fixed lumped network — values fixed by its geometry, not by frequency — and which
/// one (brief TRN-6 R-trn6-10). The SPICE deck writer writes such a component exactly as its L, C and R; a component that
/// returns null (an arm line, a dispersive section) varies with frequency and is not one. The network returned is the one the
/// component's own stamp stamps, built from the same arithmetic, never a second derivation of it.
/// </summary>
public interface ILumpedNetwork
{
    /// <summary>The network, or null when the component is not a fixed lumped network.</summary>
    LumpedNetwork? Lumped { get; }
}
