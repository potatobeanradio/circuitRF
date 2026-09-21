// What is never a device — brief-lvs-4-schematic-netlist.md R-lvs4-3.
//
// ── ONE LIST, TWO READERS, AND THAT IS THE WHOLE POINT ────────────────────────────────────────
//
// R-lvs4-3b. `NetExtractor` had these inline and LVS would otherwise have restated them. A
// component that is electrical to one and not the other produces a mismatch whose cause is in
// NEITHER document — the schematic is right, the layout is right, and the report names a part that
// should never have been compared. That is the worst kind of finding this tool can emit, because
// there is nothing for the user to look at.
//
// Keeping the set here also makes the rule READABLE, which it was not when it was four `continue`
// lines in the middle of a 200-line emission loop.

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The components that contribute no device to any netlist read from a schematic — the one list
/// <see cref="NetExtractor"/> and <c>SchematicRead</c> both consult (R-lvs4-3b).
/// </summary>
public static class SchematicExclusions
{
    /// <summary>
    /// The kinds that are never a device, whatever else is true of them.
    /// </summary>
    /// <remarks>
    /// <b><see cref="SymbolKind.Ground"/> is excluded as a device and is the reason net <c>"0"</c>
    /// exists</b> (R-lvs4-3c). <see cref="NetExtractor"/> resolves ground → <c>"0"</c> before net
    /// labels are applied, so <c>"0"</c> always wins a ground-label conflict; LVS inherits that
    /// rule and does not re-derive it.
    ///
    /// <para><b><see cref="SymbolKind.Pin"/> is a connectivity MARKER</b> (R-lvs4-3d) — the
    /// elaborator already skips it, and LVS skips it for the same reason rather than treating it
    /// as a zero-ohm device. What a <c>Pin</c> contributes is a cell PORT, which is a boundary net
    /// and not a part.</para>
    ///
    /// <para><see cref="SymbolKind.Var"/> and <see cref="SymbolKind.Meas"/> are rows routed to
    /// <c>Variables</c> and <c>Measurements</c>; they carry no terminal and stamp nothing.</para>
    /// </remarks>
    public static readonly IReadOnlySet<SymbolKind> NeverADevice = new HashSet<SymbolKind>
    {
        SymbolKind.Ground,
        SymbolKind.Pin,
        SymbolKind.Var,
        SymbolKind.Meas,
    };

    /// <summary>
    /// Whether <paramref name="comp"/> contributes no device — its kind, or the disable state the
    /// user gave it.
    /// </summary>
    /// <remarks>
    /// <b>Both disable states, not just Open.</b> A <see cref="DisableState.Short"/> component is
    /// still not a device: its terminals were unioned into one net upstream, which is what
    /// "shorted" means, and emitting it as well would put a part on a net the design does not
    /// have.
    /// </remarks>
    public static bool IsExcluded(EditableComponent comp)
    {
        ArgumentNullException.ThrowIfNull(comp);
        return comp.Disable is DisableState.Open or DisableState.Short
            || NeverADevice.Contains(comp.Symbol);
    }
}
