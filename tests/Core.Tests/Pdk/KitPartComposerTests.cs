using System.Numerics;
using CircuitRF.Core.Pdk;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// Composing a part from a kit's own data: the network says where the devices sit, the kit's library
/// says how many and of what kind, and the caller supplies the name mapping as runtime data.
///
/// <para>Most of these tests are about the REFUSALS. A part composed past a disagreement between the
/// two sources simulates perfectly and is a different circuit, which is the one outcome that must
/// not happen quietly.</para>
/// </summary>
public class KitPartComposerTests
{
    /// <summary>3 pins + <paramref name="sites"/> bridged pairs — the shape of an extracted part.</summary>
    private static SNP Network(int ports, (int a, int b)[] sites, int externals = 3)
    {
        var y = new Mat<Complex>(ports, ports);
        for (int e = 0; e < externals; e++) y[e, e] = new Complex(0.02 + 0.001 * e, 0);

        for (int i = 0; i < externals; i++)
        for (int j = i + 1; j < externals; j++)
        {
            var c = new Complex(0.003 * (i + 1) * (j + 1), 0.0007);
            y[i, j] -= c; y[j, i] -= c; y[i, i] += c; y[j, j] += c;
        }

        int k = 0;
        foreach (var (a, b) in sites)
        {
            int ia = a - 1, ib = b - 1;
            var g = new Complex(0.01 * (k + 1), 0.002 * (k + 1));
            y[ia, ia] += g; y[ib, ib] += g; y[ia, ib] -= g; y[ib, ia] -= g;

            if (externals > 0)
            {
                int ext = k % externals;                 // differential coupling — see the pairs tests
                var t = new Complex(0.004, 0.0011);
                y[ia, ext] += t; y[ext, ia] += t;
                y[ib, ext] -= t; y[ext, ib] -= t;
            }
            k++;
        }
        return SNP.FromYSweep([1e9, 5e9], [y, y]);
    }

    private static readonly Dictionary<string, string> Map =
        new() { ["KIT_DIODE"] = "Diode" };

    [Fact]
    public void APart_IsComposed_FromTheNetworkAndTheDeclaredInventory()
    {
        var snp = Network(11, [(4, 5), (6, 7), (8, 9), (10, 11)]);

        var part = KitPartComposer.Compose("SOMEPART", snp,
                                           new Dictionary<string, int> { ["KIT_DIODE"] = 4 },
                                           Map, out var failure);

        Assert.Null(failure);
        Assert.NotNull(part);
        Assert.Equal(["P1", "P2", "P3"], part.Pins);
        Assert.Equal(11, part.NetworkPorts.Count);
        Assert.Equal(["P1", "P2", "P3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11"],
                     part.NetworkPorts);
        Assert.Equal([("D4", "D5"), ("D6", "D7"), ("D8", "D9"), ("D10", "D11")],
                     part.Devices.Select(d => (d.NetA, d.NetB)));
        Assert.All(part.Devices, d => Assert.Equal("Diode", d.ComponentType));
    }

    /// <summary>
    /// The nets follow the MEASURED sites, so a network whose port order does not pair up two at a
    /// time still composes correctly — which is the case a port-order convention gets wrong while
    /// still producing something that simulates.
    /// </summary>
    [Fact]
    public void ThePortORDER_DoesNotDecideTheWiring()
    {
        var snp = Network(11, [(4, 11), (5, 6), (7, 8), (9, 10)]);

        var part = KitPartComposer.Compose("SOMEPART", snp,
                                           new Dictionary<string, int> { ["KIT_DIODE"] = 4 },
                                           Map, out var failure);

        Assert.Null(failure);
        Assert.NotNull(part);
        Assert.Equal([("D4", "D11"), ("D5", "D6"), ("D7", "D8"), ("D9", "D10")],
                     part.Devices.Select(d => (d.NetA, d.NetB)));
    }

    // ── the refusals ──────────────────────────────────────────────────────────

    /// <summary>
    /// The load-bearing check: the library's count and the network's site count are independent
    /// statements about one part, and a disagreement means one reading is wrong.
    /// </summary>
    [Fact]
    public void ADisagreementBetweenTheLibraryAndTheNetwork_RefusesToCompose()
    {
        var snp = Network(11, [(4, 5), (6, 7), (8, 9), (10, 11)]);   // the network shows 4 sites

        var part = KitPartComposer.Compose("SOMEPART", snp,
                                           new Dictionary<string, int> { ["KIT_DIODE"] = 6 },
                                           Map, out var failure);

        Assert.Null(part);
        Assert.NotNull(failure);
        Assert.Contains("disagree", failure.Reason);
    }

    [Fact]
    public void AnUnmappedPrimitive_RefusesRatherThanLeavingComponentsOut()
    {
        var snp = Network(11, [(4, 5), (6, 7), (8, 9), (10, 11)]);

        var part = KitPartComposer.Compose(
            "SOMEPART", snp,
            new Dictionary<string, int> { ["KIT_DIODE"] = 4, ["KIT_CAP"] = 8 },
            Map, out var failure);

        Assert.Null(part);
        Assert.NotNull(failure);
        Assert.Contains("KIT_CAP", failure.Reason);
    }

    [Fact]
    public void MoreThanOnePlaceableKind_RefusesBecauseCountingCannotAssignSites()
    {
        var snp = Network(11, [(4, 5), (6, 7), (8, 9), (10, 11)]);
        var map = new Dictionary<string, string> { ["KIT_DIODE"] = "Diode", ["KIT_CAP"] = "C" };

        var part = KitPartComposer.Compose(
            "SOMEPART", snp,
            new Dictionary<string, int> { ["KIT_DIODE"] = 2, ["KIT_CAP"] = 2 },
            map, out var failure);

        Assert.Null(part);
        Assert.NotNull(failure);
        Assert.Contains("more than one placeable device kind", failure.Reason);
    }

    [Fact]
    public void ANetworkWithNoDifferentialSites_RefusesInsteadOfPlacingDevicesAnyway()
    {
        var y = new Mat<Complex>(6, 6);
        for (int i = 0; i < 6; i++) y[i, i] = new Complex(0.02 * (i + 1), 0.001);

        var part = KitPartComposer.Compose("SOMEPART", SNP.FromYSweep([1e9], [y]),
                                           new Dictionary<string, int> { ["KIT_DIODE"] = 2 },
                                           Map, out var failure);

        Assert.Null(part);
        Assert.NotNull(failure);
        Assert.Contains("no differential port pairs", failure.Reason);
    }

    // ── emission ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheEmittedNetlist_IsAReadableDefineBlock_CarryingItsOwnDerivation()
    {
        var snp = Network(11, [(4, 5), (6, 7), (8, 9), (10, 11)]);
        var part = KitPartComposer.Compose("SOMEPART", snp,
                                           new Dictionary<string, int> { ["KIT_DIODE"] = 4 },
                                           Map, out _)!;

        string cnl = KitPartComposer.ToCnl(part, "SOMEPART.s11p", "Is=1e-6  N=1.05");

        Assert.Contains("define SOMEPART ( P1 P2 P3 )", cnl);
        Assert.Contains("SnP:NET  P1 P2 P3 D4 D5 D6 D7 D8 D9 D10 D11  NumPorts=11 " +
                        "File=\"SOMEPART.s11p\"", cnl);
        Assert.Contains("Diode:D1  D4 D5  Is=1e-6  N=1.05", cnl);
        Assert.Contains("Diode:D4  D10 D11  Is=1e-6  N=1.05", cnl);
        Assert.Contains("end SOMEPART", cnl);
        // The evidence travels with the artefact: a generated netlist should say how it was derived.
        Assert.Contains("MEASURED", cnl);
    }

    [Fact]
    public void WithNoDeviceParameters_TheModelsOwnDefaultsAreLeftInPlace()
    {
        var snp = Network(11, [(4, 5), (6, 7), (8, 9), (10, 11)]);
        var part = KitPartComposer.Compose("SOMEPART", snp,
                                           new Dictionary<string, int> { ["KIT_DIODE"] = 4 },
                                           Map, out _)!;

        string cnl = KitPartComposer.ToCnl(part, "SOMEPART.s11p");

        Assert.Contains("Diode:D1  D4 D5\n", cnl);
    }
}
