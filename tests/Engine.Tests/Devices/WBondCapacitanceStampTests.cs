using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gates C1 and C8 of brief-wbond-capacitance §6, plus the guardrail that the panel's readout
/// frequency never reaches the stamp.
///
/// <para>The physics gates live in <c>WBond.Tests/CapacitanceTests</c>. <b>These check that the
/// capacitance actually reaches the matrix, and — the harder half — that turning it off leaves the
/// matrix exactly as it was.</b></para>
/// </summary>
public class WBondCapacitanceStampTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public WBondCapacitanceStampTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private WBondDesign Design(int arrays = 1, int wiresPerArray = 4, double arraySpacingMil = 30.0)
    {
        var design = new WBondDesign();

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wiresPerArray; w++)
            {
                double y = a * arraySpacingMil + w * 6.0;
                array.Wires.Add(new Wire
                {
                    Points = { Point3.Mils(0, y, 20.0), Point3.Mils(50.0, y, 40.0), Point3.Mils(100.0, y, 20.0) },
                    DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil),
                });
            }
            design.Arrays.Add(array);
        }

        return design;
    }

    private string Write(WBondDesign design)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wbond-cap-{Guid.NewGuid():N}.wBond");
        WBondIo.WriteFile(path, design);
        _temporaryFiles.Add(path);
        return path;
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    // ---------------------------------------------------------------- C1: off is exactly today

    /// <summary>
    /// <b>C1 — with the flag off, no capacitance is computed at all.</b> Not zeros: the reduction
    /// never fills <b>P</b> and never factorises it, which is the only way the flag-off answer can be
    /// bit-identical rather than merely close.
    /// </summary>
    [Fact]
    public void C1_WithTheFlagOff_TheCapacitanceIsNeverComputed()
    {
        var design = Design();

        Assert.Null(ImpedanceReduction.Create(design, includeCapacitance: false, parallel: false).Capacitance);
        Assert.NotNull(ImpedanceReduction.Create(design, includeCapacitance: true, parallel: false).Capacitance);

        // And the flag rides through the model, whichever way it is set — including the null case,
        // where the design's own flag decides.
        Assert.False(Model(design, false).IncludesCapacitance);
        Assert.True(Model(design, true).IncludesCapacitance);
        Assert.True(Model(design, null).IncludesCapacitance);

        design.IncludeCapacitance = false;
        Assert.False(Model(design, null).IncludesCapacitance);
        Assert.True(Model(design, true).IncludesCapacitance);
    }

    private static CircuitRF.Core.Devices.WBondModel Model(WBondDesign design, bool? includeCapacitance) =>
        new(design, "<inline>", referencePin: false, notes: null, includeCapacitance: includeCapacitance);

    /// <summary>
    /// <b>C1 — <c>ArrayImpedance</c> is bit-identical with the flag on and off, at every frequency.</b>
    ///
    /// <para>Capacitance is a separate set of stamps, not a term folded into the series arm, and this
    /// is what says so: the number the owner's requirement names cannot move because the capacitance
    /// path exists. A literal diff against the pre-change binary is not something a test can do; this
    /// is the strongest statement that is reproducible, and it fails the moment anyone routes the
    /// shunt through the reduction instead of alongside it.</para>
    /// </summary>
    [Fact]
    public void C1_ArrayImpedanceIsBitIdenticalWithTheFlagOnAndOff()
    {
        foreach (var design in new[] { Design(), Design(arrays: 3, wiresPerArray: 5) })
        {
            var with = ImpedanceReduction.Create(design, includeCapacitance: true, parallel: false);
            var without = ImpedanceReduction.Create(design, includeCapacitance: false, parallel: false);

            foreach (double frequency in new[] { 0.0, 1e8, 1e9, 1e10, 4e10 })
            {
                var a = with.ArrayImpedance(frequency);
                var b = without.ArrayImpedance(frequency);

                Assert.Equal(a.Length, b.Length);
                for (int i = 0; i < a.Length; i++)
                {
                    Assert.Equal(a[i].Real, b[i].Real);
                    Assert.Equal(a[i].Imaginary, b[i].Imaginary);
                }
            }
        }
    }

    /// <summary>
    /// <b>C1, through the matrix — with the flag off the component is a PURE series element.</b>
    ///
    /// <para>Driven at one end with the other end open, a pure series element draws no current at all,
    /// so <c>|S11| = 1</c> exactly. Any shunt to the reference breaks that. Turning the flag on must
    /// break it — otherwise nothing was stamped — and turning it off must restore it to the last
    /// bits, which is exactly the "off reproduces today's answer" requirement said in a form the
    /// matrix can be asked.</para>
    /// </summary>
    [Fact]
    public void C1_WithTheFlagOff_TheStampedComponentIsAPureSeriesElement()
    {
        string path = Write(Design());

        // The far end is left effectively open: a 1 T-ohm leak keeps the node solvable without
        // conducting anything a shunt capacitance could hide behind.
        string Cnl(string include) => $@"
Term:T1   p1 0   Num=1 Z=50
wBond:WB1 p1 p2   File=""{path}""  IncludeCapacitance={include}
R:RLeak   p2 0   R=1e12 Ohm
";
        const double frequency = 2e10;

        double openReflection = Reflection(Cnl("false"), frequency);
        double withCapacitance = Reflection(Cnl("true"), frequency);

        _out.WriteLine($"|S11| with an open far end: capacitance off {openReflection:F12}, " +
                       $"on {withCapacitance:F6}");

        Assert.Equal(1.0, openReflection, 1e-9);
        Assert.True(1.0 - withCapacitance > 1e-4,
            $"With capacitance on, the open-ended component must draw current; |S11| was " +
            $"{withCapacitance:F9}, indistinguishable from a pure series element.");
    }

    /// <summary>
    /// <b>The parameter default is ON</b>, so an instance that never mentions it stamps capacitance —
    /// which is the one wBond default that changes an existing design's answer, and is meant to.
    /// </summary>
    [Fact]
    public void AnInstanceThatNeverMentionsTheParameter_StampsCapacitance()
    {
        string path = Write(Design());

        string Cnl(string parameters) => $@"
Term:T1   p1 0   Num=1 Z=50
wBond:WB1 p1 p2   File=""{path}"" {parameters}
R:RLeak   p2 0   R=1e12 Ohm
";
        const double frequency = 2e10;

        Assert.Equal(Reflection(Cnl("IncludeCapacitance=true"), frequency),
                     Reflection(Cnl(""), frequency), 12);
    }

    /// <summary>
    /// The DESIGN's own flag decides when the instance does not state one — the relationship the
    /// wBond editor's toolbar toggle depends on.
    /// </summary>
    [Fact]
    public void TheDesignsOwnFlagDecidesWhenTheInstanceStatesNone()
    {
        var design = Design();
        design.IncludeCapacitance = false;
        string path = Write(design);

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
wBond:WB1 p1 p2   File=""{path}""
R:RLeak   p2 0   R=1e12 Ohm
";
        Assert.Equal(1.0, Reflection(cnl, 2e10), 1e-9);
    }

    // ---------------------------------------------------------------- C8: where the charge returns

    /// <summary>
    /// <b>C8 — with <c>RefPin</c> on, the shunt capacitance appears at the REF net, not at node 0.</b>
    ///
    /// <para>Verified through a solve rather than by reading the stamp: the same component is wired
    /// once with REF at ground and once with REF isolated behind a 1 T-ohm resistor. If the charge
    /// returns to REF the second circuit has no shunt path and reflects everything; if it silently
    /// returned to node 0 the two would be identical.</para>
    /// </summary>
    [Fact]
    public void C8_TheShuntCapacitanceReturnsToTheRefNet()
    {
        string path = Write(Design());

        string Cnl(string refNet, string extra) => $@"
Term:T1   p1 0   Num=1 Z=50
wBond:WB1 p1 p2 {refNet}   File=""{path}"" RefPin=true
R:RLeak   p2 0   R=1e12 Ohm
{extra}
";
        const double frequency = 2e10;

        double atGround = Reflection(Cnl("0", ""), frequency);
        double isolated = Reflection(Cnl("r", "R:RRef r 0 R=1e12 Ohm"), frequency);

        _out.WriteLine($"|S11| with REF at ground {atGround:F9}, with REF isolated {isolated:F9}");

        Assert.True(1.0 - atGround > 1e-4, "With REF at ground the shunt capacitance must conduct.");
        Assert.Equal(1.0, isolated, 1e-6);
        Assert.True(Math.Abs(atGround - isolated) > 1e-4,
            "Isolating REF must change the answer — otherwise the shunt is going to node 0 whatever " +
            "the pin says.");
    }

    /// <summary>
    /// <b>C8's other half — with <c>RefPin</c> OFF the shunt goes to node 0</b>, which is both the
    /// only defensible choice and exactly what the plane-enabled configuration already assumes. So
    /// exposing the pin and tying it to ground must be indistinguishable from not exposing it.
    /// </summary>
    [Fact]
    public void C8_WithNoRefPin_TheShuntGoesToNodeZero()
    {
        string path = Write(Design());
        const double frequency = 2e10;

        double noPin = Reflection($@"
Term:T1   p1 0   Num=1 Z=50
wBond:WB1 p1 p2   File=""{path}""
R:RLeak   p2 0   R=1e12 Ohm
", frequency);

        double pinAtGround = Reflection($@"
Term:T1   p1 0   Num=1 Z=50
wBond:WB1 p1 p2 0   File=""{path}"" RefPin=true
R:RLeak   p2 0   R=1e12 Ohm
", frequency);

        Assert.Equal(noPin, pinAtGround, 12);
        Assert.True(1.0 - noPin > 1e-4, "And both must actually be stamping the shunt.");
    }

    /// <summary>
    /// The undeclared-return-path refusal is unchanged and still fires FIRST: with the ground plane
    /// disabled there is no plane to be capacitive to, and the refusal already covers it.
    /// </summary>
    [Fact]
    public void TheReturnPathRefusalStillFiresFirst()
    {
        var design = Design();
        design.GroundPlane.Enabled = false;
        string path = Write(design);

        string cnl = $@"
Vdc:VS    in 0   Vdc=1
wBond:WB1 in mid   File=""{path}""
R:RL      mid 0  R=1 Ohm
";
        var ex = Assert.Throws<InvalidOperationException>(() => NonlinearDcEngine.Run(Elaborate(cnl)));
        Assert.Contains("no defined return path", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the readout frequency

    /// <summary>
    /// <b>The panel's readout frequency must never reach <c>Stamp</c></b> (§7). It decides which
    /// frequency the Array Inductance panel quotes its own number at, and nothing else; the
    /// schematic's analysis sweep is what the engine stamps against.
    ///
    /// <para>Asserted behaviourally rather than by reading the source: the same design is written at
    /// three wildly different readout frequencies and the solved S-parameters must be bit-identical
    /// across a sweep.</para>
    /// </summary>
    [Fact]
    public void TheReadoutFrequencyNeverReachesTheStamp()
    {
        var reference = new List<double>();

        foreach (double readoutGHz in new[] { 0.5, 10.0, 250.0 })
        {
            var design = Design(arrays: 2, wiresPerArray: 3);
            design.ReadoutFrequencyGHz = readoutGHz;
            string path = Write(design);

            string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 0 p2 0   File=""{path}""
";
            var ds = SParameterEngine.Run(Elaborate(cnl), [1e9, 1e10, 4e10]);
            var s = ds["S"];

            var values = new List<double>();
            for (int f = 0; f < 3; f++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                    {
                        var value = (Complex)s[f, i, j];
                        values.Add(value.Real);
                        values.Add(value.Imaginary);
                    }

            if (reference.Count == 0) reference.AddRange(values);
            else
                for (int i = 0; i < values.Count; i++)
                    Assert.Equal(reference[i], values[i]);
        }
    }

    // ---------------------------------------------------------------- passivity, with capacitance

    /// <summary>
    /// The stamped network stays <b>passive</b> with capacitance in it — including the negative end
    /// bridge, which is the element a reader will suspect first. It is one entry of a positive
    /// semi-definite two-port capacitance matrix, and this is the network-level statement of that.
    /// </summary>
    [Fact]
    public void TheStampedNetworkStaysPassiveWithCapacitance()
    {
        string path = Write(Design(arrays: 2, wiresPerArray: 3));

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 0 p2 0   File=""{path}""
";
        double[] frequencies = [1e8, 1e9, 5e9, 1e10, 2e10, 4e10];
        var ds = SParameterEngine.Run(Elaborate(cnl), frequencies);
        var s = ds["S"];

        for (int f = 0; f < frequencies.Length; f++)
        {
            for (int i = 0; i < 2; i++)
            {
                double rowPower = 0.0;
                for (int j = 0; j < 2; j++)
                {
                    double magnitude = Complex.Abs((Complex)s[f, i, j]);
                    Assert.True(magnitude <= 1.0 + 1e-9,
                        $"|S{i + 1}{j + 1}| = {magnitude:F6} at {frequencies[f]:E1} Hz — the network generates power.");
                    rowPower += magnitude * magnitude;
                }
                Assert.True(rowPower <= 1.0 + 1e-9,
                    $"Row {i + 1} carries {rowPower:F6} of the incident power at {frequencies[f]:E1} Hz.");
            }
        }
    }

    /// <summary>
    /// Reciprocity survives the capacitance stamps: <c>S = Sᵀ</c>. A transposed index in the
    /// inter-array half-capacitor loop breaks this and nothing else would catch it.
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(1e10)]
    [InlineData(4e10)]
    public void TheStampedNetworkStaysReciprocalWithCapacitance(double frequency)
    {
        string path = Write(Design(arrays: 2, wiresPerArray: 3));

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 0 p2 0   File=""{path}""
";
        var s = SParameterEngine.Run(Elaborate(cnl), [frequency])["S"];

        var s21 = (Complex)s[0, 1, 0];
        var s12 = (Complex)s[0, 0, 1];

        Assert.Equal(s21.Real, s12.Real, Math.Abs(s21.Real) * 1e-9 + 1e-15);
        Assert.Equal(s21.Imaginary, s12.Imaginary, Math.Abs(s21.Imaginary) * 1e-9 + 1e-15);
    }

    /// <summary>|S11| of a one-port-driven circuit at one frequency.</summary>
    private static double Reflection(string cnl, double frequency)
    {
        var ds = SParameterEngine.Run(Elaborate(cnl), [frequency]);
        return Complex.Abs((Complex)ds["S"][0, 0, 0]);
    }
}
