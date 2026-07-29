using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported (indirectly, while asking how to switch MKlopf's entry mode): MTaper and MKlopf
/// were never added to <see cref="MicrostripSubstrateInjection"/>'s own microstrip-kind allow-list,
/// so — unlike MLIN/MBend/MTee/MCross — dropping one into a real workspace never picked up that
/// workspace's own substrate (H/T/Er/Sigma/TanD); both always simulated against
/// <c>ComponentModelFactory</c>'s hardcoded fallback substrate regardless of the open technology.
/// This also silently affected the default-parameter length-unit rewrite
/// (<see cref="MicrostripSubstrateInjection.ApplyTechnologyLengthUnit"/>), which is gated by the
/// SAME allow-list — a freshly-placed MTaper/MKlopf kept mm defaults even on a PCB (mil) or MMIC
/// (µm) workspace.
/// </summary>
public class MicrostripSubstrateInjectionKindsTests
{
    [Theory]
    [InlineData(SymbolKind.Mlin)]
    [InlineData(SymbolKind.MBend)]
    [InlineData(SymbolKind.MTee)]
    [InlineData(SymbolKind.MCross)]
    [InlineData(SymbolKind.Mtaper)]
    [InlineData(SymbolKind.Mklopf)]
    public void AllSixMicrostripComponents_AreRecognizedForSubstrateInjection(SymbolKind kind)
    {
        Assert.True(MicrostripSubstrateInjection.IsMicrostripKind(kind));
    }

    [Theory]
    [InlineData(SymbolKind.Resistor)]
    [InlineData(SymbolKind.Ground)]
    [InlineData(SymbolKind.Tline)]
    public void NonMicrostripComponents_AreNotRecognized(SymbolKind kind)
    {
        Assert.False(MicrostripSubstrateInjection.IsMicrostripKind(kind));
    }
}
