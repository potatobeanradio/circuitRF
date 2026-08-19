using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>The TERMINAL port basis — the one that carries the capacitance</b> (owner, 2026-08-18).
///
/// <para>The array-pair basis gives each array one port whose ± are its own two terminals. That is a
/// floating pair, so a shunt to the ground plane has no terminal to leave by and the file carries the
/// series arm only. <b>The fix is a different port basis, not a limitation:</b> give every terminal
/// its own port and let Touchstone's implicit common reference node BE the ground plane, and three
/// arrays export as a 6-port with the shunt capacitors sitting exactly where the stamp puts
/// them.</para>
///
/// <para><b>The headline gate is a round trip against a real solve</b>, not a self-consistency check:
/// the file is written, read back with the ordinary reader, and compared against what
/// <see cref="SParameterEngine"/> produces for the same component driven at the same 2M terminals.
/// Nothing else can catch a sign, a factor of two, or a transposed terminal in the shunt block.</para>
/// </summary>
public class WBondTouchstoneTerminalBasisTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wbond-term-" + Guid.NewGuid().ToString("N")[..8]);

    public WBondTouchstoneTerminalBasisTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static WBondDesign Design(int arrays = 2, int wires = 3, bool capacitance = true)
    {
        long loopNm = WBondUnits.ToNm(15.0, WBondUnit.Mil);
        var design = new WBondDesign { IncludeCapacitance = capacitance };

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wires; w++)
            {
                double y = a * 30 + w * 6;
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(60, y, 2),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            }
            design.Arrays.Add(array);
        }

        return design;
    }

    private static WBondTouchstoneExport.Options Options(
        double startHz = 1e9, double stopHz = 2e10, int points = 4,
        WBondPortBasis basis = WBondPortBasis.Terminals) =>
        new(Z0Ohms: 50.0, StartHz: startHz, StopHz: stopHz, Points: points,
            Logarithmic: false, Digits: 17, DigitFormat: 'g', MatrixFormat: MatrixFormat.RI,
            PortBasis: basis);

    private string Export(WBondDesign design, WBondTouchstoneExport.Options options, out SNP readBack)
    {
        string basePath = Path.Combine(_root, "wirebonds-" + Guid.NewGuid().ToString("N")[..6]);
        var result = WBondTouchstoneExport.Export(design, options, basePath);

        Assert.Equal(RfCore.Export.TouchstoneExportStatus.Ok, result.Status);
        Assert.Single(result.WrittenPaths);

        string written = result.WrittenPaths[0];
        readBack = TouchstoneIO.ReadFile(written);
        return written;
    }

    /// <summary>Writes the design to a temporary <c>.wBond</c> a netlist can name.</summary>
    private string WriteDesignFile(WBondDesign design)
    {
        string path = Path.Combine(_root, "design-" + Guid.NewGuid().ToString("N")[..6] + ".wBond");
        WBondIo.WriteFile(path, design);
        return path;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  The headline gate
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The exported file IS the network the engine solves</b> — including the capacitance.
    ///
    /// <para>The wBond is driven at all 2M of its terminals, each through a ground-referenced port,
    /// which is precisely what the terminal basis claims to describe. Every entry of the 2M × 2M
    /// S-matrix must agree.</para>
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    public void TheExportedNetwork_MatchesTheEngineSolve(int arrays, bool capacitance)
    {
        var design = Design(arrays, wires: 3, capacitance);
        var options = Options();

        Export(design, options, out var snp);
        Assert.Equal(2 * arrays, snp.Ports);

        // The same component, driven at the same 2M terminals through the engine.
        string path = WriteDesignFile(design);
        var s = SParameterEngine.Run(Elaborate(Netlist(path, arrays)), snp.Frequencies)["S"];

        int n = 2 * arrays;
        double worst = 0.0;

        for (int f = 0; f < snp.FrequencyCount; f++)
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var solved = (Complex)s[f, i, j];
                    var exported = snp.Matrices[f][i, j];
                    worst = Math.Max(worst, (solved - exported).Magnitude);
                }
        }

        Assert.True(worst < 1e-9,
            $"The exported {n}-port disagrees with the engine's own solve by {worst:E3}. The file is " +
            "supposed to BE that network.");
    }

    /// <summary>
    /// The vacuity guard for the gate above: with capacitance on, the exported network must actually
    /// differ from the series-only one. Otherwise the round trip would pass with the capacitance
    /// silently absent from both sides.
    /// </summary>
    [Fact]
    public void CapacitanceVisiblyChangesTheExportedNetwork()
    {
        var with = Design(arrays: 2, wires: 3, capacitance: true);
        var without = Design(arrays: 2, wires: 3, capacitance: false);

        Export(with, Options(), out var snpWith);
        Export(without, Options(), out var snpWithout);

        double worst = 0.0;
        for (int f = 0; f < snpWith.FrequencyCount; f++)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    worst = Math.Max(worst,
                        (snpWith.Matrices[f][i, j] - snpWithout.Matrices[f][i, j]).Magnitude);

        Assert.True(worst > 1e-3,
            $"Capacitance moved the exported network by only {worst:E3} — the gate above would pass " +
            "vacuously.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  What the basis means
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Three arrays export as a 6-port</b>, named for the terminals in the component's own order.
    /// </summary>
    [Fact]
    public void ThreeArrays_ExportAsASixPort_NamedByTerminal()
    {
        var design = Design(arrays: 3, wires: 2);

        string written = Export(design, Options(), out var snp);

        Assert.EndsWith(".s6p", written, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, snp.Ports);

        Assert.Equal(
            new[] { "G1.i", "G1.o", "G2.i", "G2.o", "G3.i", "G3.o" },
            WBondTouchstoneExport.PortNames(design, WBondPortBasis.Terminals).ToArray());

        // ...and the component's own terminal list agrees, so a file and a symbol cannot disagree.
        Assert.Equal(
            new WBondModel(design).TerminalNames,
            WBondTouchstoneExport.PortNames(design, WBondPortBasis.Terminals).ToArray());
    }

    /// <summary>
    /// <b>Without capacitance the terminal-basis network is genuinely FLOATING</b> — every row of Y
    /// sums to zero, because nothing connects to the reference. That is the structural statement of
    /// why the array-pair basis loses nothing in that case, and of what it loses when capacitance
    /// exists.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TheTerminalAdmittanceTouchesTheReference_OnlyWhenThereIsCapacitance(
        bool capacitance, bool expectFloating)
    {
        var design = Design(arrays: 2, wires: 3, capacitance);
        var y = WBondTouchstoneExport.TerminalAdmittances(design, [1e10]);

        double worstRowSum = 0.0;
        double scale = 0.0;

        for (int i = 0; i < 4; i++)
        {
            Complex sum = Complex.Zero;
            for (int j = 0; j < 4; j++)
            {
                sum += y[0][i, j];
                scale = Math.Max(scale, y[0][i, j].Magnitude);
            }
            worstRowSum = Math.Max(worstRowSum, sum.Magnitude);
        }

        if (expectFloating)
            Assert.True(worstRowSum < scale * 1e-12,
                $"With no capacitance nothing may connect to the reference; a row summed to {worstRowSum:E3}.");
        else
            Assert.True(worstRowSum > scale * 1e-6,
                "With capacitance the shunts MUST make the rows sum to something — that current is " +
                "exactly what leaves through the reference node.");
    }

    /// <summary>
    /// The terminal basis reduces to the array-pair one: the differential impedance between an
    /// array's two terminals, taken from the 2M-port, is <c>Z_arr</c>.
    ///
    /// <para>Asserted with capacitance OFF, where the two bases describe the same network — which is
    /// what makes it a cross-check of the expansion's sign pattern rather than a restatement.</para>
    /// </summary>
    [Fact]
    public void WithNoCapacitance_TheTerminalBasisReducesToTheArrayPairOne()
    {
        var design = Design(arrays: 2, wires: 3, capacitance: false);
        double[] freqs = [5e9];

        var y = WBondTouchstoneExport.TerminalAdmittances(design, freqs)[0];
        var model = new WBondModel(design);
        var zArr = model.ArrayImpedance(freqs[0]);

        // Y_arr sits in the 2x2 sign pattern (+ − / − +) of each array pair, so reading Y[2k, 2j]
        // straight back must give Z_arr^-1.
        var yArr = new NumFlat.Mat<Complex>(2, 2);
        for (int k = 0; k < 2; k++)
            for (int j = 0; j < 2; j++)
                yArr[k, j] = y[2 * k, 2 * j];

        var recovered = RFNetwork.YToZ(yArr);

        for (int k = 0; k < 2; k++)
            for (int j = 0; j < 2; j++)
            {
                Assert.Equal(zArr[k * 2 + j].Real, recovered[k, j].Real, 9);
                Assert.Equal(zArr[k * 2 + j].Imaginary, recovered[k, j].Imaginary, 9);
            }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  What the file says about itself
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The written file states its basis and its reference, and an array-pair file written from a
    /// design that HAS capacitance warns that it left it out. An exported file outlives the session
    /// that produced it.
    /// </summary>
    [Fact]
    public void TheFileSaysWhichBasisItIsAndWhatItLeftOut()
    {
        var design = Design(arrays: 2, wires: 2, capacitance: true);

        string terminals = File.ReadAllText(Export(design, Options(), out _));
        Assert.Contains("one port per TERMINAL", terminals, StringComparison.Ordinal);
        Assert.Contains("common reference node", terminals, StringComparison.Ordinal);
        Assert.Contains("Includes the wires' capacitance", terminals, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", terminals, StringComparison.Ordinal);

        string pairs = File.ReadAllText(
            Export(design, Options(basis: WBondPortBasis.ArrayPairs), out _));
        Assert.Contains("one port per wire array", pairs, StringComparison.Ordinal);
        Assert.Contains("WARNING", pairs, StringComparison.Ordinal);
        Assert.Contains("SERIES arm only", pairs, StringComparison.Ordinal);

        // ...and a design with no capacitance to lose says so plainly instead of warning.
        string quiet = File.ReadAllText(Export(
            Design(arrays: 2, wires: 2, capacitance: false),
            Options(basis: WBondPortBasis.ArrayPairs), out _));
        Assert.Contains("Series arm only", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", quiet, StringComparison.Ordinal);
    }

    /// <summary>The shipped default is the complete basis, not the compact one.</summary>
    [Fact]
    public void TheDefaultBasisIsTheOneThatCarriesEverything()
    {
        Assert.Equal(WBondPortBasis.Terminals, new WBondTouchstoneExport.Options().PortBasis);
    }

    // ═══════════════════════════════════════════════════════════════════════════

    private static string Netlist(string wbondPath, int arrays)
    {
        var text = new System.Text.StringBuilder();
        for (int p = 1; p <= 2 * arrays; p++)
            text.Append(CultureInfo.InvariantCulture, $"Term:T{p} n{p} 0 Num={p} Z=50\n");

        text.Append("wBond:WB1");
        for (int p = 1; p <= 2 * arrays; p++) text.Append(CultureInfo.InvariantCulture, $" n{p}");
        text.Append(CultureInfo.InvariantCulture, $" File=\"{wbondPath}\"\n");

        return text.ToString();
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }
}
