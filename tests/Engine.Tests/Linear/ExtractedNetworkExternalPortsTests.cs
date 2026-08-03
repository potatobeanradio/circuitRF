using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Pdk;
using CircuitRF.Engine;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// A network extracted from a physical structure exposes more ports than the part has pins: the
/// extra ports are openings left where lumped components attach. Building such a part means
/// stamping the whole N-port and connecting only the externally-connectable ports to the outside.
///
/// <para><b>The oracle is exact and needs no reference simulator.</b> S-parameters are defined with
/// every other port terminated in the reference impedance, so terminating the attachment ports in
/// Z0 and measuring the external ones must return the corresponding sub-block of the file's own
/// matrix, entry for entry. That makes this a real check on port ORDER, on the reference-node rule,
/// and on the Z-expansion — the three things most likely to be silently wrong, each of which
/// produces a circuit that still simulates.</para>
///
/// <para>Fixtures are synthesised here. The repo commits no kit data, and the rule being tested is
/// a property of the format rather than of any supplier's file.</para>
/// </summary>
public class ExtractedNetworkExternalPortsTests
{
    private const int    Ports        = 5;   // 3 externally connectable + one 2-terminal object
    private const int    ExternalPorts = 3;
    private const double Z0           = 50.0;
    private static readonly double[] FreqGHz = [1.0, 2.0, 3.0];

    // ── fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A deterministic, reciprocal S-matrix with modest magnitudes. It need not be passive: the
    /// identity under test is the definition of S-parameters, not a physical property.
    /// </summary>
    private static Complex SEntry(int f, int i, int j)
    {
        int a = Math.Min(i, j), b = Math.Max(i, j);            // reciprocal: S[i,j] == S[j,i]
        double seed = (f + 1) * 0.37 + a * 0.19 + b * 0.11;
        return new Complex(0.30 * Math.Cos(9.1 * seed), 0.30 * Math.Sin(4.7 * seed));
    }

    /// <summary>Writes a labelled N-port Touchstone file and returns its path.</summary>
    private static string WriteFixture(string dir)
    {
        string path = Path.Combine(dir, $"extracted.s{Ports}p");
        var w = new StringWriter(CultureInfo.InvariantCulture);

        w.WriteLine("! Synthesised for test — no kit data is committed to this repo.");
        w.WriteLine("# GHZ S RI R 50.0");
        w.WriteLine("! Port[1] = PAD_A_T1");
        w.WriteLine("! Port[2] = PAD_B_T1");
        w.WriteLine("! Port[3] = PAD_C_T1");
        w.WriteLine("! Port[4] = ATTACH_1_T1");
        w.WriteLine("! Port[5] = ATTACH_1_T2");

        for (int f = 0; f < FreqGHz.Length; f++)
        {
            for (int i = 0; i < Ports; i++)
            {
                var line = new List<string>();
                if (i == 0) line.Add(FreqGHz[f].ToString("R", CultureInfo.InvariantCulture));
                for (int j = 0; j < Ports; j++)
                {
                    var s = SEntry(f, i, j);
                    line.Add(s.Real.ToString("R", CultureInfo.InvariantCulture));
                    line.Add(s.Imaginary.ToString("R", CultureInfo.InvariantCulture));
                }
                w.WriteLine(string.Join(" ", line));
            }
        }

        File.WriteAllText(path, w.ToString());
        return path;
    }

    // ── the gate ──────────────────────────────────────────────────────────────

    [Fact]
    public void TerminatingTheAttachmentPorts_ReproducesTheFilesOwnExternalSubBlock()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf_extracted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = WriteFixture(dir);

            // The file itself says where the externally-connectable ports stop.
            var split = TouchstonePortLabels.SplitExternal(
                TouchstonePortLabels.Parse(File.ReadLines(file)));
            Assert.Equal(PortSplitConfidence.Structural, split.Confidence);
            Assert.Equal(ExternalPorts, split.ExternalPortCount);

            // Externals carry Terms; every attachment port is terminated in the reference impedance.
            var cnl = new System.Text.StringBuilder();
            cnl.AppendLine($"SnP:X1  n1 n2 n3 n4 n5  File=\"{file}\" NumPorts={Ports}");
            for (int p = 1; p <= split.ExternalPortCount; p++)
                cnl.AppendLine($"Term:T{p}  n{p} 0  Num={p} Z={Z0} Ohm");
            for (int p = split.ExternalPortCount + 1; p <= Ports; p++)
                cnl.AppendLine($"R:RT{p}  n{p} 0  R={Z0} Ohm");
            var (lib, tb) = new CnlReader().Read(cnl.ToString());
            var netlist   = new Elaborator(lib).Elaborate(tb);
            var freqs     = FreqGHz.Select(g => g * 1e9).ToArray();
            var ds        = SParameterEngine.Run(netlist, freqs);

            var S = ds["S"];
            for (int f = 0; f < FreqGHz.Length; f++)
            for (int i = 0; i < ExternalPorts; i++)
            for (int j = 0; j < ExternalPorts; j++)
            {
                Complex got      = (Complex)S[f, i, j];
                Complex expected = SEntry(f, i, j);
                Assert.True((got - expected).Magnitude < 1e-9,
                    $"S[{i + 1},{j + 1}] at {FreqGHz[f]} GHz: got {got}, expected {expected} " +
                    $"(the file's own entry). A mismatch here means the external ports are not " +
                    $"ports 1..{ExternalPorts}, or the N-port is not being stamped as written.");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The guard that gives the gate above its teeth: if the attachment ports were left OPEN
    /// instead of terminated, the external sub-block would differ. Without this, a stamp that
    /// ignored ports 4-5 entirely would pass the gate and look correct.
    /// </summary>
    [Fact]
    public void LeavingTheAttachmentPortsOpen_ChangesTheAnswer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf_extracted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = WriteFixture(dir);

            var cnl = new System.Text.StringBuilder();
            cnl.AppendLine($"SnP:X1  n1 n2 n3 n4 n5  File=\"{file}\" NumPorts={Ports}");
            for (int p = 1; p <= ExternalPorts; p++)
                cnl.AppendLine($"Term:T{p}  n{p} 0  Num={p} Z={Z0} Ohm");
            // Attachment ports deliberately left near-open rather than matched.
            for (int p = ExternalPorts + 1; p <= Ports; p++)
                cnl.AppendLine($"R:RT{p}  n{p} 0  R=1 GOhm");
            var (lib, tb) = new CnlReader().Read(cnl.ToString());
            var netlist   = new Elaborator(lib).Elaborate(tb);
            var freqs     = FreqGHz.Select(g => g * 1e9).ToArray();
            var ds        = SParameterEngine.Run(netlist, freqs);

            double worst = 0;
            for (int f = 0; f < FreqGHz.Length; f++)
            for (int i = 0; i < ExternalPorts; i++)
            for (int j = 0; j < ExternalPorts; j++)
                worst = Math.Max(worst, ((Complex)ds["S"][f, i, j] - SEntry(f, i, j)).Magnitude);

            Assert.True(worst > 1e-6,
                "Opening the attachment ports produced the same answer as matching them, so the " +
                "gate above is not actually exercising them.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
