using CircuitRF.Core.Design;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The nets an extracted instance's terminals sit on, in the order its symbol draws them. For every part but one that
/// is <see cref="Instance.NetBindings"/>. An SnP with a reference terminal draws one pin more than it has ports, and its
/// extraction keeps that pin's net apart in <see cref="Instance.RefNetBinding"/> (the netlist's N-or-N+1 rule), so a
/// reader joining terminals to pads by index has to put it back last — the order the symbol and
/// <c>NetlistSchematic</c> both give it. Without it a three-pad footprint under a two-port SnP with a reference had
/// three pads against two nets, and nothing on the layout was named at all (brief-sdl-3).
/// </summary>
public static class TerminalNets
{
    public static IReadOnlyList<string> Of(Instance inst) =>
        inst.RefNetBinding is { } reference ? [.. inst.NetBindings, reference] : inst.NetBindings;
}
