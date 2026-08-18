using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-E M3 — Touchstone export, the one genuinely new capability of the phase.
///
/// <para><b>Nothing here asserts against a stored number.</b> Every gate is a round trip against the
/// model's own <see cref="WBondModel.ArrayImpedance"/>, or against a closed form the reduction is
/// independently known to produce — a golden file would only ever prove the export agrees with a
/// previous run of itself.</para>
///
/// <para><b>The oracle for the R-wbe-5 decision.</b> A wBond exports as an M-port, one port per wire
/// array, port <i>k</i> being that array's own two terminals. Its impedance matrix is then
/// <i>exactly</i> <c>ArrayImpedance(f)</c> by definition — so reading the written file back and
/// converting S→Z must reproduce it, and the check is a self-consistency one a transposed or
/// mis-scaled conversion cannot pass.</para>
/// </summary>
public class WBondTouchstoneExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wbond-snp-" + Guid.NewGuid().ToString("N")[..8]);

    public WBondTouchstoneExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A design of <paramref name="arrays"/> arrays, each of <paramref name="wires"/> wires, spaced
    /// so the arrays genuinely couple — which is what makes the off-diagonal gate mean something.
    /// </summary>
    private static WBondDesign Design(int arrays = 1, int wires = 3)
    {
        long loopNm = WBondUnits.ToNm(15.0, WBondUnit.Mil);
        var design = new WBondDesign();

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wires; w++)
            {
                double y = a * 30 + w * 6;   // mils — arrays 30 mil apart, wires 6 mil apart
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(60, y, 2),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            }
            design.Arrays.Add(array);
        }
        return design;
    }

    /// <summary>
    /// <b>17 significant digits, deliberately, and only in the tests.</b> A round-trip gate has to
    /// separate "the conversion is wrong" from "the file was written to fewer digits than the gate
    /// asserts"; <c>G17</c> is round-trip-exact for a double, so what is left is the arithmetic. The
    /// dialog's own default is 9 — plenty for a network anyone measures — and is not what this is
    /// testing.
    /// </summary>
    private static WBondTouchstoneExport.Options Options(
        double startHz = 1e9, double stopHz = 1e10, int points = 5, double z0 = 50.0) =>
        new(Z0Ohms: z0, StartHz: startHz, StopHz: stopHz, Points: points,
            Logarithmic: false, Digits: 17, DigitFormat: 'g', MatrixFormat: MatrixFormat.RI);

    private string Export(WBondDesign design, WBondTouchstoneExport.Options options, out SNP readBack)
    {
        string basePath = Path.Combine(_root, "wirebonds");
        var result = WBondTouchstoneExport.Export(design, options, basePath);

        Assert.Equal(RfCore.Export.TouchstoneExportStatus.Ok, result.Status);
        Assert.Single(result.WrittenPaths);

        string written = result.WrittenPaths[0];
        Assert.True(File.Exists(written), $"Nothing was written to {written}.");

        // Read it back with the ordinary reader, never with the writer's own intermediate.
        readBack = TouchstoneIO.ReadFile(written);
        return written;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  The frequency grid is the USER'S
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ALinearGrid_HitsBothEndpointsExactly()
    {
        var f = WBondTouchstoneExport.BuildFrequencies(1e9, 5e9, 5, logarithmic: false);

        Assert.Equal(5, f.Length);
        Assert.Equal(1e9, f[0], 6);
        Assert.Equal(5e9, f[^1], 6);
        Assert.Equal(3e9, f[2], 6);
    }

    [Fact]
    public void ALogGrid_IsGeometricallySpaced_AndStillHitsBothEndpoints()
    {
        var f = WBondTouchstoneExport.BuildFrequencies(1e8, 1e10, 3, logarithmic: true);

        Assert.Equal(1e8, f[0], 0);
        Assert.Equal(1e10, f[^1], 0);
        Assert.Equal(1e9, f[1], 0);   // the geometric mean, not the arithmetic one
    }

    /// <summary>A single-point sweep is legal — one frequency is a perfectly ordinary thing to ask for.</summary>
    [Fact]
    public void ASinglePointGrid_IsLegalAndIsThatOnePoint()
    {
        var f = WBondTouchstoneExport.BuildFrequencies(2.4e9, 2.4e9, 1, logarithmic: false);
        Assert.Equal([2.4e9], f);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M3's headline gates
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A one-array wBond exports an <c>.s1p</c> whose Z at each frequency equals
    /// <c>ArrayImpedance(f)[0]</c> after <c>SToZ</c> round-trips it back.</b>
    ///
    /// <para>This is a self-consistency check a transposed or mis-scaled conversion cannot pass: the
    /// value comes out of the model, goes through Z→S, through the writer, through the reader, and
    /// back through S→Z, and has to land on itself.</para>
    /// </summary>
    [Fact]
    public void AOneArrayDesign_ExportsAnS1p_WhoseZIsTheArrayImpedance()
    {
        var design = Design(arrays: 1, wires: 3);
        var options = Options();

        // The suffix is the exporter's to choose from the port count, so this asserts the file it
        // actually wrote rather than restating the convention.
        string written = Export(design, options, out var snp);
        Assert.EndsWith(".s1p", written, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, snp.Ports);

        var model = new WBondModel(design);

        for (int i = 0; i < snp.FrequencyCount; i++)
        {
            var z = RFNetwork.SToZ(snp.Matrices[i], new Complex(options.Z0Ohms, 0.0));
            var expected = model.ArrayImpedance(snp.Frequencies[i])[0];

            Assert.Equal(expected.Real,      z[0, 0].Real,      12);
            Assert.Equal(expected.Imaginary, z[0, 0].Imaginary, 12);
        }
    }

    /// <summary>
    /// <b>A two-array design's <c>.s2p</c> reproduces the full 2×2 <c>Z_arr</c> including its
    /// off-diagonal</b> — which is the whole point of exporting the array rather than the wires.
    ///
    /// <para>The off-diagonal is asserted to be non-negligible first, so the test cannot pass
    /// vacuously against a design whose arrays happen not to couple.</para>
    /// </summary>
    [Fact]
    public void ATwoArrayDesign_ReproducesTheFullMatrix_OffDiagonalIncluded()
    {
        var design = Design(arrays: 2, wires: 3);
        var options = Options();

        Export(design, options, out var snp);
        Assert.Equal(2, snp.Ports);

        var model = new WBondModel(design);
        bool sawRealCoupling = false;

        for (int i = 0; i < snp.FrequencyCount; i++)
        {
            var z = RFNetwork.SToZ(snp.Matrices[i], new Complex(options.Z0Ohms, 0.0));
            var expected = model.ArrayImpedance(snp.Frequencies[i]);

            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                {
                    Assert.Equal(expected[r * 2 + c].Real,      z[r, c].Real,      12);
                    Assert.Equal(expected[r * 2 + c].Imaginary, z[r, c].Imaginary, 12);
                }

            // Vacuity guard: an uncoupled pair would satisfy everything above with zeros.
            double mutual = expected[1].Magnitude;
            double self   = expected[0].Magnitude;
            if (self > 0 && mutual / self > 1e-3) sawRealCoupling = true;
        }

        Assert.True(sawRealCoupling,
            "The fixture's arrays do not couple, so the off-diagonal assertion proves nothing.");
    }

    /// <summary>
    /// <b>The closed-form anchor — WB19b's free cross-oracle, extended to the FILE.</b> The exported
    /// network's reactance must converge on the series-inductance network
    /// <see cref="WBondModel.InductanceOnly"/> describes, and the residual must be the RESISTANCE
    /// rather than anything else.
    ///
    /// <para><b>The brief's own framing of this is inverted, and the measurement is what says so.</b>
    /// It asks for "a frequency low enough that R ≪ ωL" — but a bond wire's R is roughly constant
    /// while ωL grows with frequency, so R/X is <i>largest</i> at DC and falls with frequency.
    /// Measured on this fixture: R/X = 7.3 at 1 MHz, 0.084 at 100 MHz, 0.020 at 1 GHz, 0.0061 at
    /// 10 GHz. So "R ≪ ωL" is a HIGH-frequency condition and the anchor converges upward.</para>
    ///
    /// <para><b>Which is why this asserts the MECHANISM rather than a fixed epsilon.</b> A tolerance
    /// picked to pass at one frequency says nothing; that the reactive error tracks R/X and halves
    /// with it says the residual really is the resistance the closed form leaves out. Measured
    /// relative error of <c>Im(Z)/ω</c> against <c>L_arr</c>: 1.85e-2 at 1 GHz against R/X = 2.04e-2,
    /// and 4.2e-3 at 20 GHz — the two track to within 10%.</para>
    /// </summary>
    [Fact]
    public void TheExportedReactance_ConvergesOnTheInductanceOnlyClosedForm_AndTheResidualIsTheResistance()
    {
        var design = Design(arrays: 2, wires: 4);
        var l = new WBondModel(design).InductanceOnly();

        double ErrorAt(double fHz, out double rOverX)
        {
            var options = Options(startHz: fHz, stopHz: fHz, points: 1);
            Export(design, options, out var snp);

            var z = RFNetwork.SToZ(snp.Matrices[0], new Complex(options.Z0Ohms, 0.0));
            double omega = 2.0 * Math.PI * fHz;

            rOverX = Math.Abs(z[0, 0].Real) / Math.Abs(z[0, 0].Imaginary);
            return Math.Abs(z[0, 0].Imaginary / omega - l[0, 0]) / Math.Abs(l[0, 0]);
        }

        double lowError  = ErrorAt(1e9,  out double lowRatio);
        double highError = ErrorAt(2e10, out double highRatio);

        // The residual IS the resistance: the reactive error tracks R/X rather than sitting at some
        // level of its own. A conversion that were merely wrong would not obey this.
        Assert.InRange(lowError,  0.5 * lowRatio,  1.5 * lowRatio);
        Assert.InRange(highError, 0.5 * highRatio, 1.5 * highRatio);

        // …and it genuinely converges as R/X falls, rather than plateauing.
        Assert.True(highError < lowError / 2.0,
            $"The reactive error did not halve with R/X: {lowError:E3} at 1 GHz, {highError:E3} at 20 GHz.");

        // A ceiling, so this can never pass while being wildly off.
        Assert.True(highError < 0.02, $"Reactive error {highError:E3} at 20 GHz is far too large.");
    }

    /// <summary>
    /// <b>The file says what its ports are</b>, in the form <see cref="TouchstonePortLabels"/> already
    /// reads on the way in. A Touchstone whose port order is undocumented is a file somebody wires
    /// backwards, so this reads the labels back off the parsed network rather than off the writer.
    /// </summary>
    [Fact]
    public void TheWrittenFile_NamesEveryPortAfterItsArray_ReadableByTheOrdinaryReader()
    {
        var design = Design(arrays: 3, wires: 2);
        Export(design, Options(points: 2), out var snp);

        var labels = TouchstonePortLabels.Read(snp);

        Assert.Equal(3, labels.Count);
        Assert.Equal(["G1", "G2", "G3"], labels.OrderBy(l => l.Port).Select(l => l.Name));
        Assert.Equal([1, 2, 3], labels.OrderBy(l => l.Port).Select(l => l.Port));
    }

    /// <summary>
    /// Every port names a different object, so the file correctly reports that nothing marks where the
    /// externally-connectable ports stop — which is the right answer here: they ALL are.
    /// </summary>
    [Fact]
    public void ThePortSplit_IsAmbiguousByDesign_BecauseEveryPortIsExternallyConnectable()
    {
        var design = Design(arrays: 2, wires: 2);
        Export(design, Options(points: 2), out var snp);

        var split = TouchstonePortLabels.SplitExternal(TouchstonePortLabels.Read(snp));

        Assert.Equal(PortSplitConfidence.Ambiguous, split.Confidence);
    }

    /// <summary>The reference impedance is the user's, and the file records it.</summary>
    [Fact]
    public void TheReferenceImpedance_IsTheUsersChoice_AndIsWrittenIntoTheFile()
    {
        var design = Design(arrays: 1, wires: 2);
        var options = Options(z0: 75.0, points: 3);

        Export(design, options, out var snp);

        Assert.Equal(75.0, snp.Z0.Real, 9);
        Assert.Equal(0.0, snp.Z0.Imaginary, 12);

        // …and the S it wrote is the S of that reference, not of 50 Ω relabelled.
        var model = new WBondModel(design);
        var z = RFNetwork.SToZ(snp.Matrices[0], new Complex(75.0, 0.0));
        Assert.Equal(model.ArrayImpedance(snp.Frequencies[0])[0].Imaginary, z[0, 0].Imaginary, 12);
    }

    /// <summary>
    /// <b>Publishing a design with no declared return path is REFUSED</b>, by the same rule the
    /// component's own stamp refuses it — and this is the more important half, because a file
    /// outlives the session that produced it. An inductance without a return is optimistically low,
    /// which is the worst direction to be wrong in.
    /// </summary>
    [Fact]
    public void ADesignWithNoDeclaredReturnPath_IsRefusedRatherThanPublishedOptimisticallyLow()
    {
        var design = Design(arrays: 1, wires: 2);
        design.GroundPlane.Enabled = false;

        var ex = Assert.Throws<InvalidOperationException>(
            () => WBondTouchstoneExport.Export(design, Options(points: 2), Path.Combine(_root, "bad")));

        Assert.Contains("return path", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_root, "bad*"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M5 — one file, two binaries, the same numbers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>M5's value half.</b> Build a design, save it, read it back FROM THE FILE ALONE with nothing
    /// carried over, and compare: wire count, array membership, every profile binding, the panel's
    /// own inductance readout, and an exported <c>.snp</c> entry for entry.
    ///
    /// <para>Both sides go through the same <c>WBondIo</c> and the same <see cref="WBondDocument.Open"/>
    /// circuitRF's tab uses, which is what makes this a statement about the FILE rather than about
    /// two objects that happen to be in the same process.</para>
    /// </summary>
    [Fact]
    public void OneFile_ReadBackFromDiskAlone_ProducesTheSameDesignAndTheSameNetwork()
    {
        var authored = Design(arrays: 2, wires: 3);
        string path = Path.Combine(_root, "roundtrip.wBond");
        WBondIo.WriteFile(path, authored);

        // Nothing carried over: a fresh document, opened from the path.
        var reopened = WBondDocument.Open(path).ViewModel.Editor.Design;

        // Structure — wire count, array membership, and every wire's points to the nanometre.
        Assert.Equal(authored.WireCount, reopened.WireCount);
        Assert.Equal(authored.Arrays.Count, reopened.Arrays.Count);

        for (int a = 0; a < authored.Arrays.Count; a++)
        {
            Assert.Equal(authored.Arrays[a].Name, reopened.Arrays[a].Name);
            Assert.Equal(authored.Arrays[a].Wires.Count, reopened.Arrays[a].Wires.Count);

            for (int w = 0; w < authored.Arrays[a].Wires.Count; w++)
                Assert.Equal(authored.Arrays[a].Wires[w].Points,
                             reopened.Arrays[a].Wires[w].Points);
        }

        // The panel's own readout — the number a user actually reads off the editor.
        var authoredL = new WBondModel(authored).InductanceOnly();
        var reopenedL = new WBondModel(reopened).InductanceOnly();

        for (int i = 0; i < authoredL.ArrayCount; i++)
            for (int j = 0; j < authoredL.ArrayCount; j++)
                Assert.Equal(authoredL.PicoHenries(i, j), reopenedL.PicoHenries(i, j), 6);

        // And the published network, entry for entry, out of two separately-written files.
        var options = Options(points: 4);

        var a1 = WBondTouchstoneExport.Export(authored, options, Path.Combine(_root, "authored"));
        var a2 = WBondTouchstoneExport.Export(reopened, options, Path.Combine(_root, "reopened"));

        var s1 = TouchstoneIO.ReadFile(a1.WrittenPaths[0]);
        var s2 = TouchstoneIO.ReadFile(a2.WrittenPaths[0]);

        Assert.Equal(s1.Ports, s2.Ports);
        Assert.Equal(s1.FrequencyCount, s2.FrequencyCount);

        for (int f = 0; f < s1.FrequencyCount; f++)
        {
            Assert.Equal(s1.Frequencies[f], s2.Frequencies[f], 9);
            for (int r = 0; r < s1.Ports; r++)
                for (int c = 0; c < s1.Ports; c++)
                {
                    Assert.Equal(s1.Matrices[f][r, c].Real,      s2.Matrices[f][r, c].Real,      12);
                    Assert.Equal(s1.Matrices[f][r, c].Imaginary, s2.Matrices[f][r, c].Imaginary, 12);
                }
        }
    }
}
