// brief-cosim-pattern-3 R-cp3-1 — the far field a driven 3D run stored per port, as a schematic's fields analysis reads it: the
// "farfield" group of the .npy its result record names, with the S and Z0 of the same run. Overview §3 of the series, and docs/design/sar.md.
//
// A READ COSTS FAR MORE THAN THE SUM. One frequency at 1° is 65,160 directions in two components for each port, in a file read whole, and
// a slider evaluates many times. So the arrays are kept per path, length and write time, and only the arrays a sum reads survive the read:
// U, the metrics and the polarization are formed again from the sum. File times are a cache key here, never a verdict, as EmFieldDataRecord's are.

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Engine.Mom;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Em3d;

/// <summary>What a fields analysis needs of a result's far field.</summary>
public sealed class PortFarFieldSet
{
    /// <summary>How many results stay read at once: a sweep's pattern can run to hundreds of megabytes.</summary>
    private const int KeptResults = 4;

    private readonly Complex[] _eTheta, _ePhi;      // [freq, theta, phi, port], as the cubes are

    private readonly PlanarPortBox[]? _boxes;       // [freq, port]; null on a terminated result, or a planar one whose every port is a gap
    private readonly bool _pseudoWave;

    private PortFarFieldSet(PlanarFarFieldGrid grid, double[] hz, int[] ports, Complex[] eTheta, Complex[] ePhi, Complex[][,] s,
                            Complex z0, double referenceDbm, string solver, string name, string excitation, PlanarPortBox[]? boxes, bool pseudoWave)
    {
        Excitation = excitation;
        _boxes = boxes;
        _pseudoWave = pseudoWave;
        Grid = grid;
        FrequenciesHz = hz;
        PortNumbers = ports;
        _eTheta = eTheta;
        _ePhi = ePhi;
        S = s;
        Z0 = z0;
        ReferenceInputPowerDbm = referenceDbm;
        Solver = solver;
        Name = name;
    }

    /// <summary>The grid the solver wrote: a θ axis stopping at 90° is an openEMS hemisphere, kept as it is.</summary>
    public PlanarFarFieldGrid Grid { get; }

    public IReadOnlyList<double> FrequenciesHz { get; }

    public IReadOnlyList<int> PortNumbers { get; }

    public int PortCount => PortNumbers.Count;

    /// <summary>S at each of <see cref="FrequenciesHz"/>.</summary>
    public IReadOnlyList<Complex[,]> S { get; }

    /// <summary>The one reference impedance every port of the run has.</summary>
    public Complex Z0 { get; }

    public double ReferenceInputPowerDbm { get; }

    public string Solver { get; }

    /// <summary>The record's file name, for a sentence.</summary>
    public string Name { get; }

    public bool Hemisphere => Grid.ThetaDeg[^1] <= PlanarFarFieldGrid.MaxThetaDeg;

    /// <summary>What every other port was doing in a stored pattern (<see cref="EmFarFieldExcitation"/>).</summary>
    public string Excitation { get; }

    public bool Planar => Excitation == EmFarFieldExcitation.ShortedOthers;

    /// <summary>
    /// What a stored pattern at frequency <paramref name="i"/> is multiplied by, from the circuit's waves at the block on the scale of the waves given, and in
    /// <paramref name="structureW"/> what the solved structure takes under them on that scale. A terminated result weighs a port by its incident wave and has no
    /// structure beyond the block's S. A planar one weighs a port by its gap's voltage, and takes what its gaps do, which on a de-embedded port is not what its plane does.
    /// </summary>
    internal Complex[] Weights(int i, IReadOnlyList<Complex> a, IReadOnlyList<Complex> b, out double structureW)
    {
        int n = PortCount;
        double z0 = Z0.Real;
        var weights = new Complex[n];
        structureW = 0;
        if (!Planar)
        {
            Complex root = RfCore.RFNetwork.ComplexSqrt(Z0);
            var s = S[i];
            for (int k = 0; k < n; k++)
            {
                weights[k] = a[k] * root * (1 + s[k, k]);
                structureW += a[k].Magnitude * a[k].Magnitude - b[k].Magnitude * b[k].Magnitude;
            }
            return weights;
        }
        for (int k = 0; k < n; k++)
        {
            var (gapA, gapB) = _boxes is { } boxes ? boxes[i * n + k].ToGap(a[k], b[k], z0, _pseudoWave) : (a[k], b[k]);
            weights[k] = Math.Sqrt(z0) * (gapA + gapB);
            structureW += gapA.Magnitude * gapA.Magnitude - gapB.Magnitude * gapB.Magnitude;
        }
        return weights;
    }

    /// <summary>The index of <paramref name="hz"/> in <see cref="FrequenciesHz"/>, or −1: a pattern exists only at a frequency the run solved.</summary>
    public int IndexOf(double hz)
    {
        for (int i = 0; i < FrequenciesHz.Count; i++)
            if (SameFrequency(FrequenciesHz[i], hz)) return i;
        return -1;
    }

    /// <summary>Adds <paramref name="weight"/> times port <paramref name="port"/>'s stored pattern at frequency <paramref name="frequency"/> to the
    /// two components, each in the grid's [theta, phi] order.</summary>
    internal void AddWeighted(int frequency, int port, Complex weight, Complex[] eTheta, Complex[] ePhi)
    {
        int n = Grid.DirectionCount, q = PortCount;
        for (int d = 0; d < n; d++)
        {
            int at = (frequency * n + d) * q + port;
            eTheta[d] += weight * _eTheta[at];
            ePhi[d] += weight * _ePhi[at];
        }
    }

    private static bool SameFrequency(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(a, b);

    // ── the read, kept ──────────────────────────────────────────────────────────────────────────────

    private sealed record Kept(string Path, long Ticks, long Length, PortFarFieldSet? Set, string? Refusal);

    private static readonly List<Kept> _kept = [];
    private static readonly object _gate = new();
    private static long _reads;

    /// <summary>The <c>.npy</c> reads made in this process: what a second evaluation of an unchanged result must not add to.</summary>
    public static long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// The far field of the record at <paramref name="recordPath"/>, or the sentence refusing it: the record's own refusal, an <c>.npy</c> with
    /// no far field, or ports whose Z0 differ. Each names the file and the remedy.
    /// </summary>
    public static (PortFarFieldSet? Set, string? Refusal) Open(string recordPath)
    {
        var (record, refused) = EmFieldDataRecord.Open(recordPath);
        if (record is null) return (null, refused);
        var info = new FileInfo(record.NpyPath);
        lock (_gate)
        {
            int at = _kept.FindIndex(k => k.Path == info.FullName && k.Ticks == info.LastWriteTimeUtc.Ticks && k.Length == info.Length);
            if (at >= 0)
            {
                var hit = _kept[at];
                _kept.RemoveAt(at);
                _kept.Add(hit);
                return (hit.Set, hit.Refusal);
            }
            var (set, refusal) = Read(record);
            Interlocked.Increment(ref _reads);
            _kept.RemoveAll(k => k.Path == info.FullName);
            _kept.Add(new Kept(info.FullName, info.LastWriteTimeUtc.Ticks, info.Length, set, refusal));
            if (_kept.Count > KeptResults) _kept.RemoveAt(0);
            return (set, refusal);
        }
    }

    private static (PortFarFieldSet? Set, string? Refusal) Read(EmFieldDataRecord record)
    {
        string name = Path.GetFileName(record.RecordPath), npy = Path.GetFileName(record.NpyPath);
        DataSet data;
        try { data = DataSetImporter.Import(record.NpyPath).DataSet; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
        {
            return (null, $"'{npy}' could not be read ({e.Message}). Run its EM setup again.");
        }

        string noFarField = $"'{npy}' holds no far field: the run behind '{name}' did not have the setup's Radiation pattern on. Turn it on and run it again.";
        if (!data.Groups.Contains(PlanarFarField.Group)) return (null, noFarField);
        var group = data.CubesIn(PlanarFarField.Group);
        if (!group.TryGetValue("Etheta", out var eTheta) || !group.TryGetValue("Ephi", out var ePhi)) return (null, noFarField);
        string[] order = ["freq", "theta", "phi", "port"];
        if (!eTheta.Axes.Select(a => a.Name).SequenceEqual(order) || !ePhi.Axes.Select(a => a.Name).SequenceEqual(order))
            return (null, $"'{npy}' holds a far field on axes [{string.Join(", ", eTheta.Axes.Select(a => a.Name))}], not [{string.Join(", ", order)}]. Run its EM setup again.");

        if (OlderRefusal(record.Solver, eTheta.Axes[3].Values.Length, group.ContainsKey(Em3dRadiation.PortsTerminatedCube)) is { } older)
            return (null, $"'{name}' {older}");

        double[] hz = eTheta.Axes[0].Values;
        var grid = new PlanarFarFieldGrid(eTheta.Axes[1].Values, eTheta.Axes[2].Values);
        int[] ports = [.. eTheta.Axes[3].Values.Select(p => (int)Math.Round(p))];

        if (data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S")) is not { } sGroup)
            return (null, $"'{npy}' holds a far field and no S. Run its EM setup again.");
        var sCube = data.CubesIn(sGroup)["S"];
        if (sCube.Rank != 3 || sCube.Axes[1].Values.Length != ports.Length || sCube.Axes[2].Values.Length != ports.Length)
            return (null, $"'{npy}' holds {ports.Length} ports' far field and an S of another shape. Run its EM setup again.");
        var sweep = sCube.Axes[0].Values;
        var sValues = sCube.ComplexValues;
        int n = ports.Length;
        var s = new Complex[hz.Length][,];
        for (int i = 0; i < hz.Length; i++)
        {
            int row = Array.FindIndex(sweep, f => SameFrequency(f, hz[i]));
            if (row < 0)
                return (null, $"'{npy}' holds a far field at {Layout.MaterialDispersion.Hz(hz[i])} and no S there. Run its EM setup again.");
            var m = new Complex[n, n];
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++) m[a, b] = sValues[(row * n + a) * n + b];
            s[i] = m;
        }

        if (data.CubesIn(sGroup).TryGetValue("Z0", out var z0Cube) is false)
            return (null, $"'{npy}' states no port Z0. Run its EM setup again.");
        var z0 = z0Cube.DataKind == DataKind.Complex ? z0Cube.ComplexValues : [.. z0Cube.RealValues.Select(v => new Complex(v, 0))];
        if (z0.Length == 0) return (null, $"'{npy}' states no port Z0. Run its EM setup again.");
        // The S of a block is read in one reference, and a port's pattern is weighed in its own: a Touchstone file records one.
        if (z0.Any(z => z != z0[0]) || z0[0].Imaginary != 0 || !(z0[0].Real > 0))
            return (null, $"'{npy}' holds ports of Z0 {string.Join(", ", z0.Select(Text).Distinct())}, and a block's ports must all have one real Z0, because a Touchstone " +
                          "file records one. Give every port the same Z0 and run the EM setup again.");

        // Asked of the data as well as the record: a record from before it stated this is still a planar result.
        string excitation = record.FarFieldExcitation ?? (data.Groups.Contains(PlanarKernel.DiagnosticsGroup) ? EmFarFieldExcitation.ShortedOthers : EmFarFieldExcitation.TerminatedZ0);
        PlanarPortBox[]? boxes = null;
        bool pseudoWave = false;
        if (excitation == EmFarFieldExcitation.ShortedOthers)
        {
            var planar = data.Groups.Contains(PlanarKernel.DiagnosticsGroup) ? data.CubesIn(PlanarKernel.DiagnosticsGroup) : new Dictionary<string, DataCube>();
            var (read, refusal) = PlanarBoxes(record, planar, hz.Length, ports.Length, name);
            if (refusal is not null) return (null, refusal);
            boxes = read;
            pseudoWave = planar.TryGetValue(PlanarPortBoxes.PseudoWaveCube, out var pseudo) && pseudo.RealValues[0] != 0;
        }

        double reference = group.TryGetValue("ReferenceInputPowerDbm", out var refCube) && refCube.DataKind == DataKind.Real && refCube.BufferLength > 0
            ? refCube.RealValues[0] : 0.0;
        string solver = record.Solver.Length > 0 ? record.Solver : "the solver";
        return (new PortFarFieldSet(grid, hz, ports, eTheta.ComplexValues, ePhi.ComplexValues, s, z0[0], reference, solver, name, excitation, boxes, pseudoWave), null);
    }

    /// <summary>
    /// A planar result's way from its planes to its gaps. A result from before the run kept this can be recombined only when every port is a gap, whose plane is its
    /// gap; returns null boxes then. Calibration groups' boxes are not kept, and an edge port's cannot be recovered from the rest of the file.
    /// </summary>
    private static (PlanarPortBox[]? Boxes, string? Refusal) PlanarBoxes(EmFieldDataRecord record, IReadOnlyDictionary<string, DataCube> group, int frequencies, int ports, string name)
    {
        if (group.ContainsKey(PlanarPortBoxes.GroupCube))
            return (null, $"'{name}' is a planar result with ports in a calibration group, and a fields analysis cannot recombine its pattern: that group's calibration is not kept with the " +
                          "result. Calibrate those ports one at a time, or use a 3D setup with component ports.");
        if (!group.TryGetValue(PlanarPortBoxes.A11Cube, out var a11))
            return record.PortKinds.Count == ports && record.PortKinds.All(k => k == "Gap")
                ? (null, null)
                : (null, $"'{name}' is an older planar result that does not keep what relates its edge ports to their reference planes. Run its EM setup again to recombine it.");
        DataCube[] cubes = [a11, group[PlanarPortBoxes.A21Cube], group[PlanarPortBoxes.A22Cube], group[PlanarPortBoxes.ZcCube], group[PlanarPortBoxes.PeelCube]];
        if (cubes.Any(c => c.BufferLength != frequencies * ports))
            return (null, $"'{name}' holds port calibrations of another shape than its far field. Run its EM setup again.");
        var boxes = new PlanarPortBox[frequencies * ports];
        for (int i = 0; i < boxes.Length; i++)
            boxes[i] = new PlanarPortBox(cubes[0].ComplexValues[i], cubes[1].ComplexValues[i], cubes[2].ComplexValues[i], cubes[3].ComplexValues[i], cubes[4].ComplexValues[i]);
        return (boxes, null);
    }

    /// <summary>
    /// The sum needs each stored pattern to be the field its S describes, referred to the voltage its S states. Palace's always were. openEMS's and EMerge's are from
    /// the run that writes <see cref="Em3dRadiation.PortsTerminatedCube"/>, and one written before it cannot be corrected from its <c>.npy</c>: running it again is the remedy.
    /// </summary>
    internal static string? OlderRefusal(string solver, int ports, bool marked)
    {
        if (marked) return null;
        // One openEMS port has no other ports to reflect, and its voltage is the probes' own.
        if (solver.StartsWith("openEMS", StringComparison.OrdinalIgnoreCase) && ports > 1)
            return "is an older openEMS result, whose stored patterns also hold the small waves its other ports reflected. Run its EM setup again to recombine it.";
        if (solver.StartsWith("EMerge", StringComparison.OrdinalIgnoreCase))
            return "is an EMerge result whose stored patterns are referred to a port voltage that disagrees with its S: one from before that was corrected, or one with a " +
                   "wave port, or with a pattern at a frequency outside its sweep. Run its EM setup again, with lumped ports and the pattern at a sweep frequency, to recombine it.";
        return null;
    }

    private static string Text(Complex z) => z.Imaginary == 0 ? $"{z.Real:G6} Ω" : $"{z.Real:G6}{(z.Imaginary < 0 ? "-" : "+")}j{Math.Abs(z.Imaginary):G6} Ω";
}
