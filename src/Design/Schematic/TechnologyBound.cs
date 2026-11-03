namespace CircuitRF.Design.Schematic;

/// <summary>
/// The components that take their substrate or process from the technology rather than from their own parameters
/// (brief vendorlib-11 R-vl11-1a): the union of the kinds the six injection seams handle — the microstrip family and
/// the GSG pad (<see cref="MicrostripSubstrateInjection"/>), CPWG and SLIN (<see cref="PlanarLineSubstrateInjection"/>),
/// the broadside pairs (<see cref="BroadsideSubstrateInjection"/>), the vias (<see cref="ViaSubstrateInjection"/>), the
/// MMIC passives (<see cref="MmicPassiveInjection"/>) and the test pad (<see cref="TestPadInjection"/>).
///
/// <para>Written as a call to each seam's own predicate and never as a list of kinds, so a kind a seam gains is in it
/// with nothing to keep in step. A seventh seam has to be added here; the test that every seam's kinds are inside it
/// is what says so.</para>
/// </summary>
public static class TechnologyBound
{
    public static bool Is(SymbolKind kind)
        => MicrostripSubstrateInjection.IsMicrostripKind(kind)
           || PlanarLineSubstrateInjection.IsPlanarLineKind(kind)
           || BroadsideSubstrateInjection.IsBroadsideKind(kind)
           || ViaSubstrateInjection.IsViaKind(kind)
           || MmicPassiveInjection.IsMmicKind(kind)
           || TestPadInjection.IsTestPadKind(kind);
}
