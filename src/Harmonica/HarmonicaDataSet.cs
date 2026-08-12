using System.Numerics;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;

namespace CircuitRF.Harmonica;

/// <summary>
/// R-hrf-10 — harmonicaRF publishes an ordinary <see cref="DataSet"/> of <see cref="DataCube"/>s, the
/// same contract every circuitRF analysis returns (harmonicarf.md §5).
///
/// <para><b>This is load-bearing rather than tidy.</b> H7's trace picker plots ANYTHING harmonicaRF
/// solved, copy/paste into a circuitRF Data Display then works for free, and the display layer
/// already knows how to consume it. Cube names and axis order are part of the deliverable because
/// H4–H7 are written against them.</para>
///
/// <para>Derived figures of merit are NOT computed here.
/// <c>RfCore.Loadpull.LoadpullPostProcessor.Enrich</c> already produces Pout / DE / PAE / Zin / IRL /
/// AM-PM, and re-deriving a single one of them in a second place is exactly what §0.3 item 5
/// forbids.</para>
/// </summary>
public static class HarmonicaDataSet
{
    /// <summary>The DEFAULT reference impedance, used only where no document is in scope. R-h9b-6 —
    /// a real document's own value is <c>CircuitModel.Settings.Z0</c>, threaded through explicitly
    /// everywhere one is in scope; this const is the fallback the optional parameter below defaults
    /// to, kept so a caller with no model in hand (a test, a tool) still gets the historical 50 Ω.</summary>
    public const double Z0 = 50.0;

    public static Complex GammaOf(Complex z, double z0 = Z0) => (z - z0) / (z + z0);

    /// <summary>
    /// Γ → Z. The pole at Γ = 1 is nudged rather than allowed to produce a non-finite impedance that
    /// would take the whole solve down; <c>|Γ| &gt; 1</c> is left alone, because an active termination
    /// is a legitimate thing for the inverse solve to land on (§6.6).
    /// </summary>
    public static Complex ImpedanceOf(Complex gamma, double z0 = Z0)
    {
        var d = Complex.One - gamma;
        if (d.Magnitude < 1e-12) d = new Complex(1e-12, d.Imaginary);
        return z0 * (Complex.One + gamma) / d;
    }

    /// <summary>
    /// The intrinsic plane at one operating point: <c>Z_intr</c>, <c>Gamma_intr</c> and the full
    /// source-side conversion matrix, indexed <c>[side, harmonic]</c>.
    ///
    /// <para><b>This is the ONE definition</b> (§4.5). <see cref="Build"/> publishes it as the
    /// <c>Gamma_intr</c> cube and H6's inverse solve reads it from here — neither re-derives it, which
    /// is what stops the §4.5.2 ratio error creeping back in at a second call site.</para>
    /// </summary>
    /// <param name="includeSource">
    /// False skips the §4.5.3 <c>J′</c> route entirely and leaves the source row NaN. The inverse
    /// solve sets this when no marked band is on the source side: the Schur route is the expensive
    /// half of an intrinsic evaluation and a residual that does not use it should not pay for it.
    /// </param>
    public readonly record struct IntrinsicValues(Complex[,] Z, Complex[,] Gamma, Complex[,]? ZsConv);

    public static IntrinsicValues Intrinsic(HarmonicaContext ctx, OperatingPoint point,
                                            in IntrinsicPlane.DeviceSpectra spectra,
                                            bool includeSource = true)
    {
        int k = ctx.Model.Settings.HarmonicCount;
        double z0 = ctx.Model.Settings.Z0;
        var map = ctx.IntrinsicPorts;

        // R-h8-3 — an intrinsic plane nobody has located is left EMPTY (NaN), never guessed at. The
        // two sides fail independently: §4.5.1's ratio needs only the drain port, while §4.5.3's J′
        // route additionally needs the gate port to be the gate-SOURCE port. See IntrinsicPortMap.
        var zLoad = map.LoadAvailable
            ? IntrinsicPlane.LoadImpedance(spectra, map.DrainPort)
            : null;
        Complex[,]? zSrc = includeSource && map.SourceAvailable
            ? IntrinsicPlane.SourceImpedance(ctx, point, map.GatePort)
            : null;

        var zIntr     = new Complex[2, k + 1];
        var gammaIntr = new Complex[2, k + 1];
        for (int h = 0; h <= k; h++)
        {
            zIntr[(int)TerminationSide.Source, h] =
                zSrc is null ? new Complex(double.NaN, double.NaN) : zSrc[h, h];
            zIntr[(int)TerminationSide.Load,   h] =
                zLoad is null ? new Complex(double.NaN, double.NaN) : Finite(zLoad[h]);
        }
        for (int s = 0; s < 2; s++)
            for (int h = 0; h <= k; h++)
                gammaIntr[s, h] = GammaOf(zIntr[s, h], z0);

        return new IntrinsicValues(zIntr, gammaIntr, zSrc);
    }

    /// <summary>The same, evaluating the device spectra itself — what a caller with only an operating
    /// point in hand needs.</summary>
    public static IntrinsicValues Intrinsic(HarmonicaContext ctx, OperatingPoint point,
                                            bool includeSource = true)
    {
        int k     = ctx.Model.Settings.HarmonicCount;
        int gridN = HbFft.GridSize(k, ctx.Model.Settings.FftOverSample);
        var spectra = IntrinsicPlane.Evaluate(ctx.DutComponent, point.V, ctx.Interface.DeviceNodes,
                                              k, gridN, ctx.Model.Settings.FrequencyHz,
                                              ctx.IntrinsicPorts.SourcePort);
        return Intrinsic(ctx, point, spectra, includeSource);
    }

    /// <summary>Builds the per-operating-point cubes of §5.</summary>
    public static DataSet Build(HarmonicaContext ctx, OperatingPoint point, TerminationSet terminations)
    {
        var model = ctx.Model;
        int k     = model.Settings.HarmonicCount;
        int n     = ctx.Interface.InterfaceCount;
        int gridN = HbFft.GridSize(k, model.Settings.FftOverSample);
        double f0 = model.Settings.FrequencyHz;
        double z0 = model.Settings.Z0;

        // R-h9b-13 — the loadline's own sample count, independent of the solve's FFT grid (gridN,
        // above): the spectrum carries every harmonic 0…K so it is re-evaluated exactly at whatever
        // density the user asked for, never at gridN's power-of-two solve size.
        int loadlineSamples = model.Settings.LoadlineSamples;

        var ds = new DataSet();

        // The harmonic axis carries integer ORDERS, not frozen k·f0 values — the convention
        // `HbEngine.BuildSingleToneDataSet` settled on, so a consumer reconstructs the physical
        // frequency as order × f0 exactly as it does for an ordinary HB run.
        var harmonic = new Axis("harmonic", [.. Enumerable.Range(0, k + 1).Select(i => (double)i)], "");
        var side     = new Axis("side", [0, 1], "", ["source", "load"]);
        var tsample  = new Axis("tsample",
                                [.. Enumerable.Range(0, loadlineSamples).Select(i => (double)i)], "");

        var nodeNames = ctx.Interface.DeviceNodes.Select(ctx.Netlist.Nodes.NameOf).ToArray();
        var node = new Axis("node", [.. Enumerable.Range(0, n).Select(i => (double)i)], "", nodeNames);

        ds.Add("V",   Cube2(point.V,   node, harmonic));
        ds.Add("INl", Cube2(point.INl, node, harmonic));

        // ── the intrinsic plane (§4.5) ────────────────────────────────────────
        var dut = ctx.DutComponent;
        var map = ctx.IntrinsicPorts;
        var spectra = IntrinsicPlane.Evaluate(dut, point.V, ctx.Interface.DeviceNodes, k, gridN, f0,
                                              map.SourcePort);

        var port = new Axis("port", [.. Enumerable.Range(0, spectra.PortCount).Select(i => (double)i)], "",
                            dut.Model.TerminalNames);

        ds.Add("V_intr",    Cube2(spectra.portVoltages,       port, harmonic));
        ds.Add("I_intr",    Cube2(spectra.portCurrents,       port, harmonic));
        ds.Add("Idisp_intr", Cube2(spectra.portChargeCurrents, port, harmonic));

        // An unlocated intrinsic plane publishes an EMPTY loadline rather than one drawn at a guessed
        // port — the same refusal the glyphs make, so the two panels can never disagree about whether
        // the plane is known.
        var (vds, ids) = map.LoadAvailable
            ? IntrinsicPlane.Loadline(dut, point.V, ctx.Interface.DeviceNodes, k, loadlineSamples,
                                      map.DrainPort, map.SourcePort)
            : (new double[loadlineSamples], new double[loadlineSamples]);
        if (map.LoadAvailable)
        {
            ds.Add("Vds_intr_t", new DataCube([tsample], vds));
            ds.Add("Ids_intr_t", new DataCube([tsample], ids));
        }

        // ── the terminations as set, and the glyph values ─────────────────────
        var zExt     = new Complex[2, k + 1];
        var gammaExt = new Complex[2, k + 1];
        for (int s = 0; s < 2; s++)
            for (int h = 0; h <= k; h++)
            {
                Complex z = h == 0 ? Complex.Zero : terminations.Z((TerminationSide)s, h);
                zExt[s, h] = z;
                gammaExt[s, h] = GammaOf(z, z0);
            }
        ds.Add("Z_ext",     Cube2(zExt,     side, harmonic));
        ds.Add("Gamma_ext", Cube2(gammaExt, side, harmonic));

        // §4.5's definitions live in ONE place — Intrinsic — which H6's inverse solve also reads.
        var intr = Intrinsic(ctx, point, spectra);

        ds.Add("Z_intr",     Cube2(intr.Z,     side, harmonic));
        ds.Add("Gamma_intr", Cube2(intr.Gamma, side, harmonic));

        // The FULL source-side conversion matrix (§4.5.3). Its diagonal is the glyph; its
        // off-diagonals measure how strongly the source network converts harmonic i into harmonic k,
        // which is a genuinely useful and rarely-visible quantity for source harmonic engineering.
        // Absent when the source plane could not be located — a cube of NaN would look like a
        // measurement that came out badly rather than one that was never taken.
        if (intr.ZsConv is { } zSrc)
        {
            var harmonicIn = new Axis("harmonic_in",
                                      [.. Enumerable.Range(0, k + 1).Select(i => (double)i)], "");
            ds.Add("Zs_conv", Cube2(zSrc, harmonic, harmonicIn));
        }

        // ── the extrinsic plane: Zin from the TRUE delivered current (§4.5.4) ──
        var (planeV, planeI) = ctx.Interface.PlaneState(
            terminations, HarmonicaContext.DriveVolts(terminations, point.PavlDbm),
            point.INlTotal, model.Settings.DcBlockFarads);

        var zin = new Complex[2, k + 1];
        for (int s = 0; s < 2; s++)
            for (int h = 0; h <= k; h++)
                zin[s, h] = planeI[s, h] == Complex.Zero
                    ? new Complex(double.NaN, double.NaN)
                    : planeV[s, h] / planeI[s, h];

        ds.Add("V_ext",  Cube2(planeV, side, harmonic));
        ds.Add("Iin",    Cube2(planeI, side, harmonic));
        ds.Add("Zin",    Cube2(zin,    side, harmonic));

        ds.Add("Converged", DataCube.Scalar(point.Converged ? 1.0 : 0.0));
        ds.Add("Residual",  DataCube.Scalar(point.Residual));
        ds.Add("Pavl_dBm",  DataCube.Scalar(point.PavlDbm));

        return ds;
    }

    private static Complex Finite(Complex z)
        => double.IsFinite(z.Real) && double.IsFinite(z.Imaginary) ? z : new Complex(double.NaN, double.NaN);

    private static DataCube Cube2(Complex[,] data, Axis a0, Axis a1)
    {
        int r = data.GetLength(0), c = data.GetLength(1);
        var flat = new Complex[r * c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                flat[i * c + j] = data[i, j];
        return new DataCube([a0, a1], flat);
    }
}
