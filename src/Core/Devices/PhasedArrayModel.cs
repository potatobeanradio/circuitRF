// brief-paa-3 R-paa3-1 / R-paa3-4 / R-paa3-5 — the Phased Antenna Array (.cnl PhasedArray) in Elements mode: one pin per element,
// row-major from the top left seen from boresight (D6), and the N-port those pins see. docs/design/phased-array.md §4.
//
// THE S IT STAMPS IS ONE OF THREE, NAMED (D3):
//   exact     the record's own S, its ports put in the array's element order (the record IS the array);
//   embedded  the record's S extended by element offset (ArrayCoupling.Extend, PAA-2), the diagonal its centre's or its class's;
//   isolated  a one-port record's S₁₁ on every element, and no coupling.
// Coupling=off keeps the diagonal of whichever it is. The level defaults from the record: one port → isolated, a record whose lattice is
// the array's → exact, any other lattice → embedded; an explicit exact on another lattice is refused naming both.
//
// IT IS INTERPOLATED AS AN EM FIELD DATA BLOCK IS. The network is built on the record's own frequencies from its Touchstone — the same
// file, read through TouchstoneCache — and evaluated between them through an SnpInterpolator with SnpModel's defaults (cubic spline,
// magnitude/angle, clamped and warned outside). At exact level on the record's lattice it is therefore that block's S at every frequency.
//
// AN ELEMENT SWITCHED OFF (Off) is terminated in Z0 inside — so the others see the S of the array with it matched — and its pin is left
// open: the stamped matrix has S = 1 on its diagonal and nothing coupling to it.
//
// The pattern half (a fields analysis on this block) is FieldCosimulation's, in src/Design, which reads the record's far field; this model
// keeps what it needs: the record, the lattices, the level and which record port each element's pattern is taken from.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using NumFlat;
using RfCore;

namespace CircuitRF.Core.Devices;

/// <summary>D3 — the three levels of fidelity.</summary>
public enum PhasedArrayLevel { Isolated, Embedded, Exact }

/// <summary>R-paa3-1 — a Phased Antenna Array in Elements mode: the N-port of its elements.</summary>
public sealed class PhasedArrayModel : ComponentModel, IReportsWarnings
{
    public const string TypeName = "PhasedArray";
    public const string DisplayName = "Phased Antenna Array";

    /// <summary>D1 — the most elements Elements mode takes: brute force is a small-array tool.</summary>
    public const int ElementsLimit = 64;

    /// <summary>R-paa4-1 — the most elements Feed mode takes (D9).</summary>
    public const int FeedLimit = 4096;

    /// <summary>R-paa4-1 — Mode=feed: the elements are internal nodes behind the beamformer, and an element switched off is terminated by
    /// the beamformer's matched, zero-weight port rather than opened at a pin, so the network is stamped whole.</summary>
    public bool FeedMode { get; private init; }

    /// <summary>R-paa4-1 — the beamformer of a Feed-mode array, with each element's requested and quantised weight; null in Elements mode.</summary>
    public PhasedArrayFeedModel? Feed { get; internal set; }

    /// <summary>R-paa4-1 / R-paa4-4 — Feed mode's sentence for check and explain: the pin, the element count and the quantisation; null in
    /// Elements mode.</summary>
    public string? FeedNote { get; internal set; }

    /// <summary>R-paa4-5 — what was placed per element, for the run's notes and explain; null in Elements mode.</summary>
    public PhasedArrayChannels? Channels { get; internal set; }

    /// <summary>PAA-1's decision 4: a pitch further than this many free-space wavelengths from the record's is warned about (D5).</summary>
    public const double PitchWarningWavelengths = 0.02;

    /// <summary>The parameters whose values are words or paths, never expressions.</summary>
    public static readonly IReadOnlySet<string> WordParameters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Record", "Level", "Patterns", "Mode", "Lattice", "Coupling", "Off", "Channel" };

    /// <summary>The length parameters: a value with a unit means it, a bare number is metres.</summary>
    public static readonly IReadOnlySet<string> LengthParameters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dx", "dy", "RecordDx", "RecordDy" };

    /// <summary>The prefix of the hidden value carrying the unit a length was typed in, for a sentence that quotes it back.</summary>
    public const string TypedUnitPrefix = "__unit_";

    private readonly SnpInterpolator _interp;
    private readonly Complex[] _z0;
    private readonly List<(string Key, string Message)> _pending = [];
    private bool _warned;

    private PhasedArrayModel(EmFieldDataRecord record, ArrayLattice lattice, RecordLatticeFit fit, PhasedArrayLevel level, bool levelStated,
                             bool classes, bool coupled, SNP network, Complex z0, string? note, string? pitchWarning, int[] patternPort)
    {
        Record = record;
        Lattice = lattice;
        RecordFit = fit;
        Level = level;
        LevelStated = levelStated;
        Classes = classes;
        Coupled = coupled;
        Network = network;
        CouplingNote = note;
        PitchWarning = pitchWarning;
        _patternPort = patternPort;
        _interp = new SnpInterpolator(network, InterpolationMethod.CubicSpline, InterpolationFormat.MagPhase, MatrixType.S, OutOfRangePolicy.WarnClamp);
        _z0 = [.. Enumerable.Repeat(z0, lattice.Count)];
        PortBranchIndices = [.. Enumerable.Repeat(-1, lattice.Count)];
    }

    private readonly int[] _patternPort;

    public override int PortCount => Lattice.Count;
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>The result record the array is built from.</summary>
    public EmFieldDataRecord Record { get; }

    public string RecordPath => Record.RecordPath;

    /// <summary>The array's lattice, with its elements switched off.</summary>
    public ArrayLattice Lattice { get; }

    /// <summary>The record's own lattice, fitted from its positions or stated by RecordRows/Cols/Dx/Dy.</summary>
    public RecordLatticeFit RecordFit { get; }

    public ArrayLattice RecordLattice => RecordFit.Lattice!;

    public PhasedArrayLevel Level { get; }

    /// <summary>Whether Level was stated rather than taken from the record.</summary>
    public bool LevelStated { get; }

    /// <summary>Patterns=classes: each element takes its position class's pattern and S_cc (embedded only).</summary>
    public bool Classes { get; }

    /// <summary>Coupling=on.</summary>
    public bool Coupled { get; }

    /// <summary>The array's S on the record's frequencies, before any element is switched off.</summary>
    public SNP Network { get; }

    /// <summary>What the coupling extension measured (embedded); null at the other levels.</summary>
    public string? CouplingNote { get; }

    /// <summary>D5 — the warning that dx or dy is away from the record's pitch; null when it is not.</summary>
    public string? PitchWarning { get; }

    /// <summary>Branch indices per pin, set by each <see cref="Stamp"/>; what a fields analysis reads the block's currents from.</summary>
    public int[] PortBranchIndices { get; }

    /// <summary>The 0-based record port element <paramref name="element"/>'s pattern is taken from: its own at exact, its class's or the
    /// centre's when embedded, the one port when isolated.</summary>
    public int PatternPort(int element) => _patternPort[element];

    /// <summary>The level, what it was chosen from and what it uses, as a run's notes say it.</summary>
    public string LevelNote
    {
        get
        {
            string why = LevelStated ? "as stated" : Level switch
            {
                PhasedArrayLevel.Isolated => "because the record has one port",
                PhasedArrayLevel.Exact => "because the record's lattice is the array's",
                _ => "because the record is a sub-array of another lattice",
            };
            string uses = Level switch
            {
                PhasedArrayLevel.Exact => "each element's own port, pattern and coupling from the record",
                PhasedArrayLevel.Isolated => "the record's one element on every position, with no coupling",
                _ => (Classes ? "each element's position-class pattern and S_cc" : "the centre element's pattern and S_cc") +
                     (Coupled ? " from the sub-array, its coupling extended by element offset" : " from the sub-array, with no coupling"),
            };
            string coupling = !Coupled && Level != PhasedArrayLevel.Isolated ? " Coupling=off: every coupling between elements is dropped." : "";
            return $"Level {Level.ToString().ToLowerInvariant()} ({why}): {uses}. The record '{Path.GetFileName(RecordPath)}' stands on a {RecordLattice}; " +
                   $"the array is {Lattice}.{coupling}";
        }
    }

    /// <summary>The S stamped at <paramref name="hz"/>: the network interpolated, each element switched off left open at its pin.</summary>
    public Mat<Complex> SAt(double hz)
    {
        var s = _interp.Evaluate(hz);
        if (FeedMode) return s;
        foreach (int n in Lattice.Off)
        {
            int k = n - 1;
            for (int j = 0; j < PortCount; j++) { s[k, j] = 0; s[j, k] = 0; }
            s[k, k] = 1;
        }
        return s;
    }

    /// <summary>The reference impedance of every pin.</summary>
    public Complex Z0 => _z0[0];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (!_warned)
        {
            _warned = true;
            if (PitchWarning is { } w) _pending.Add(($"phased-array-pitch:{c.InstancePath}", $"{Describe(c.InstancePath)}: {w}"));
        }
        var s = SAt(Math.Abs(omega) / (2 * Math.PI));
        int n = PortCount;
        var nodes = new int[2 * n];
        var arr = new Complex[n, n];
        for (int k = 0; k < n; k++)
        {
            nodes[2 * k] = c.Nodes[k];
            nodes[2 * k + 1] = c.ReferenceNode;
            for (int j = 0; j < n; j++) arr[k, j] = s[k, j];
        }
        IdealSBlockModel.StampWaveConstraints(mna, nodes, arr, _z0, PortBranchIndices);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        var list = _pending.ToList();
        _pending.Clear();
        return list;
    }

    /// <summary>A record's S is passive or it is not; measured on the network's own frequencies, as an SnP's file is.</summary>
    public override Activity Activity => Activity.BlackBox;

    public override string? PassivationNote => _passivationNote;
    private string? _passivationNote;

    public override Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz)
    {
        double worst = RFNetwork.Passivity(Network).DefaultIfEmpty(0).Max();
        _passivationNote = $"σ_max = {worst:G4} over the array's S at its record's {Network.Frequencies.Length} frequencies";
        return worst <= 1 + 1e-6 ? Activity.Passive : Activity.BlackBox;
    }

    /// <summary>"Phased Antenna Array 'PA1'".</summary>
    public static string Describe(string instancePath)
        => string.IsNullOrWhiteSpace(instancePath) ? DisplayName : $"{DisplayName} '{instancePath}'";

    // ── building one from its parameters ──────────────────────────────────────────────────────────

    /// <summary>R-paa3-1 / R-paa3-4 — the model of resolved <paramref name="p"/>; every refusal is a
    /// <see cref="ParameterRefusalException"/> in the user's terms, which the elaborator prefixes with the instance.</summary>
    public static PhasedArrayModel Create(IReadOnlyDictionary<string, Value> p)
    {
        string mode = Word(p, "Mode", "elements");
        bool feed = mode.Equals("feed", StringComparison.OrdinalIgnoreCase);
        if (!feed && !mode.Equals("elements", StringComparison.OrdinalIgnoreCase))
            throw new ParameterRefusalException($"Mode={mode} is not a mode: it is elements (one pin per element) or feed.");

        int rows = Count(p, "Rows"), cols = Count(p, "Cols");
        if (!feed && rows * cols > ElementsLimit)
            throw new ParameterRefusalException($"a {rows} × {cols} array has {rows * cols} elements, and Elements mode — one pin per element — takes at most {ElementsLimit}. " +
                                                "A larger array is Feed mode's: one feed pin driving every element through the array's own beamformer.");
        if (feed && rows * cols > FeedLimit)
            throw new ParameterRefusalException($"a {rows} × {cols} array has {rows * cols} elements, and Feed mode takes at most {FeedLimit} (64 × 64).");

        string latticeWord = Word(p, "Lattice", "rect");
        var kind = latticeWord.ToLowerInvariant() switch
        {
            "rect" or "rectangular" => ArrayLatticeKind.Rect,
            "tri" or "triangular" => ArrayLatticeKind.Tri,
            _ => throw new ParameterRefusalException($"Lattice={latticeWord} is not a lattice: it is rect or tri (every other row offset by half a column pitch)."),
        };
        double dx = Length(p, "dx", required: cols > 1 || kind == ArrayLatticeKind.Tri), dy = Length(p, "dy", required: rows > 1);
        var off = Off(p, rows * cols);
        var lattice = new ArrayLattice(rows, cols, dx, dy, kind, off);

        string path = Word(p, "Record", "");
        var (record, refusal) = EmFieldDataRecord.Open(path);
        if (record is null) throw new ParameterRefusalException(refusal ?? $"Record '{path}' could not be opened.");
        var source = TouchstoneCache.Get(record.SnpPath);
        if (source.Ports != record.PortCount)
            throw new ParameterRefusalException($"'{Path.GetFileName(record.SnpPath)}' holds {source.Ports} ports and its record lists {record.PortCount}. Run its EM setup again.");

        int? rRows = OptionalCount(p, "RecordRows"), rCols = OptionalCount(p, "RecordCols");
        double? rDx = OptionalLength(p, "RecordDx"), rDy = OptionalLength(p, "RecordDy");
        var fit = record.PortCount == 1
            ? new RecordLatticeFit(new ArrayLattice(1, 1, rDx ?? dx, rDy ?? dy), [0], [record.PortPositions.FirstOrDefault() ?? (0, 0)],
                                   record.PortPositions.FirstOrDefault() is not null, null, "One port: a single element.")
            : ArrayLatticeFit.Of(record.PortPositions, rRows, rCols, rDx, rDy);
        if (fit.Lattice is not { } sub)
            throw new ParameterRefusalException($"its record '{Path.GetFileName(record.RecordPath)}' is not a lattice it can place: {fit.Refusal}");

        bool sameLattice = sub.Rows == rows && sub.Cols == cols && sub.Kind == kind;
        string levelWord = Word(p, "Level", "");
        PhasedArrayLevel level = levelWord.ToLowerInvariant() switch
        {
            "" or "auto" => record.PortCount == 1 ? PhasedArrayLevel.Isolated : sameLattice ? PhasedArrayLevel.Exact : PhasedArrayLevel.Embedded,
            "isolated" => PhasedArrayLevel.Isolated,
            "embedded" => PhasedArrayLevel.Embedded,
            "exact" => PhasedArrayLevel.Exact,
            _ => throw new ParameterRefusalException($"Level={levelWord} is not a level: it is auto (from the record), isolated, embedded or exact."),
        };
        if (level == PhasedArrayLevel.Exact && !sameLattice)
            throw new ParameterRefusalException($"Level=exact takes each element's own port from the record, so the array must be the record's lattice: " +
                                                $"the record '{Path.GetFileName(record.RecordPath)}' is a {sub} and the array is a {lattice}. Use Level=embedded, or Rows={sub.Rows} Cols={sub.Cols}" +
                                                (sub.Kind != kind ? $" Lattice={sub.Kind.ToString().ToLowerInvariant()}" : "") + ".");
        if (level == PhasedArrayLevel.Isolated && record.PortCount != 1)
            throw new ParameterRefusalException($"Level=isolated takes a record of one element, and '{Path.GetFileName(record.RecordPath)}' has {record.PortCount} ports. " +
                                                "Use Level=embedded, with Coupling=off to drop the coupling.");

        string patternsWord = Word(p, "Patterns", "classes");
        bool classes = patternsWord.ToLowerInvariant() switch
        {
            "classes" => true,
            "centre" or "center" => false,
            _ => throw new ParameterRefusalException($"Patterns={patternsWord} is not a choice: it is classes (each element its position class's pattern) or centre."),
        };
        string couplingWord = Word(p, "Coupling", "on");
        bool coupled = couplingWord.ToLowerInvariant() switch
        {
            "on" or "yes" or "true" => true,
            "off" or "no" or "false" => false,
            _ => throw new ParameterRefusalException($"Coupling={couplingWord} is not a choice: it is on or off."),
        };

        // The record's S in the record lattice's element order, per frequency, and the array's from it.
        int ns = record.PortCount, n = lattice.Count;
        var portOf = fit.PortOfElement;
        var matrices = new Mat<Complex>[source.Frequencies.Length];
        string? note = null;
        for (int f = 0; f < matrices.Length; f++)
        {
            var m = source.Matrices[f];
            var sub2 = new Complex[ns, ns];
            for (int i = 0; i < ns; i++)
                for (int j = 0; j < ns; j++) sub2[i, j] = m[portOf[i], portOf[j]];
            var out_ = new Mat<Complex>(n, n);
            switch (level)
            {
                case PhasedArrayLevel.Exact:
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++) out_[i, j] = coupled || i == j ? sub2[i, j] : 0;
                    break;
                case PhasedArrayLevel.Isolated:
                    for (int i = 0; i < n; i++) out_[i, i] = sub2[0, 0];
                    break;
                default:
                    var ext = ArrayCoupling.Extend(sub2, sub, lattice, coupled, classes);
                    note = ext.Note;
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++) out_[i, j] = ext.S[i, j];
                    break;
            }
            matrices[f] = out_;
        }
        var network = new SNP(source.Frequencies, matrices, MatrixType.S, MatrixFormat.RI, source.Z0);

        // Z0 defaults to the record's; another one renormalises the array's S to it.
        Complex z0 = source.Z0;
        if (p.TryGetValue("Z0", out var z0v) && z0v.Kind is ValueKind.Real or ValueKind.Complex)
        {
            var stated = z0v.Kind == ValueKind.Real ? new Complex(z0v.AsReal(), 0) : z0v.AsComplex();
            if (!(stated.Real > 0)) throw new ParameterRefusalException($"Z0={stated} is not a reference impedance: its real part must be positive.");
            if (stated != z0)
            {
                for (int f = 0; f < matrices.Length; f++) matrices[f] = RFNetwork.ZToS(RFNetwork.SToZ(matrices[f], z0), stated);
                network = new SNP(source.Frequencies, matrices, MatrixType.S, MatrixFormat.RI, stated);
                z0 = stated;
            }
        }

        int[] patternPort = [.. Enumerable.Range(0, n).Select(e => level switch
        {
            PhasedArrayLevel.Exact => portOf[e],
            PhasedArrayLevel.Isolated => portOf[0],
            _ => portOf[classes ? ArrayCoupling.SourceIndex(e / cols, e % cols, rows, cols, sub) : ArrayCoupling.SourceIndex(1, 1, 3, 3, sub)],
        })];

        string? pitch = level == PhasedArrayLevel.Isolated ? null
            : PitchSentence(lattice, sub, source.Frequencies.Max(), TypedUnit(p, "dx"), TypedUnit(p, "dy"));
        bool levelStated = levelWord.Length > 0 && !levelWord.Equals("auto", StringComparison.OrdinalIgnoreCase);
        return new PhasedArrayModel(record, lattice, fit, level, levelStated, classes, coupled, network, z0, note, pitch, patternPort) { FeedMode = feed };
    }

    /// <summary>R-paa4-1 — whether an instance's Mode override says feed (the elaborator places a Feed-mode array itself).</summary>
    public static bool IsFeed(IEnumerable<ParameterAssignment> overrides)
        => overrides.FirstOrDefault(o => o.Name.Equals("Mode", StringComparison.OrdinalIgnoreCase)) is { } m
           && m.Expression.Trim().Trim('"').Trim().Equals("feed", StringComparison.OrdinalIgnoreCase);

    /// <summary>R-paa4-2 — the per-element weight parameters, evaluated in element scope: Amp, Phase, Delay and their [n] overrides.</summary>
    public static bool IsElementParameter(string name, out string baseName, out int? index)
    {
        index = null;
        int open = name.IndexOf('[');
        baseName = open < 0 ? name : name[..open];
        if (!(baseName.Equals("Amp", StringComparison.OrdinalIgnoreCase) || baseName.Equals("Phase", StringComparison.OrdinalIgnoreCase)
              || baseName.Equals("Delay", StringComparison.OrdinalIgnoreCase))) return false;
        if (open < 0) return true;
        if (!name.EndsWith(']') || !int.TryParse(name[(open + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int k)) return false;
        index = k;
        return true;
    }

    /// <summary>D5 — the sentence when dx or dy is further than <see cref="PitchWarningWavelengths"/> of a wavelength at
    /// <paramref name="hz"/> from the record's; null when neither is. Each pitch is given in the unit it was typed in.</summary>
    public static string? PitchSentence(ArrayLattice array, ArrayLattice record, double hz, string? dxUnit, string? dyUnit)
    {
        double lambda = Evaluator.SpeedOfLight / hz;
        var parts = new List<string>();
        void Check(string name, double mine, double theirs, bool applies, string? unit)
        {
            if (!applies || !(Math.Abs(mine - theirs) > PitchWarningWavelengths * lambda)) return;
            parts.Add($"{name} is {InUnit(mine, unit)} and the record's is {InUnit(theirs, unit)}, {Math.Abs(mine - theirs) / lambda:0.###} λ apart at {hz / 1e9:G4} GHz");
        }
        Check("dx", array.Dx, record.Dx, array.Cols > 1 && record.Cols > 1, dxUnit);
        Check("dy", array.Dy, record.Dy, array.Rows > 1 && record.Rows > 1, dyUnit);
        if (parts.Count == 0) return null;
        return $"{string.Join("; ", parts)}. The coupling and the element patterns were solved at the record's pitch, so they are approximations away from it " +
               $"(this warns past {PitchWarningWavelengths} λ); the array factor is exact at any pitch.";
    }

    /// <summary>A length in the unit it was typed in (millimetres when none was, or a unit that is not a length).</summary>
    public static string InUnit(double metres, string? unit)
    {
        string u = unit is { Length: > 0 } && Units.Scale(unit) is { } s && s > 0 && s < 10 ? unit : "mm";
        double scale = Units.Scale(u) ?? 1e-3;
        return $"{(metres / scale).ToString("G6", CultureInfo.InvariantCulture)} {u}";
    }

    private static string? TypedUnit(IReadOnlyDictionary<string, Value> p, string name)
        => p.TryGetValue(TypedUnitPrefix + name, out var v) && v.Kind == ValueKind.String ? v.AsString() : null;

    private static string Word(IReadOnlyDictionary<string, Value> p, string name, string fallback)
        => p.TryGetValue(name, out var v) ? (v.Kind == ValueKind.String ? v.AsString().Trim() : v.ToString()) : fallback;

    private static int Count(IReadOnlyDictionary<string, Value> p, string name)
        => OptionalCount(p, name) ?? throw new ParameterRefusalException($"{name} is not set: the array has {name} as a whole number of elements.");

    private static int? OptionalCount(IReadOnlyDictionary<string, Value> p, string name)
    {
        if (!p.TryGetValue(name, out var v)) return null;
        double x = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
        if (!(x >= 1) || x != Math.Round(x) || x > ArrayLattice.MaxSide)
            throw new ParameterRefusalException($"{name}={v} is not a number of elements: it is a whole number from 1 to {ArrayLattice.MaxSide}.");
        return (int)x;
    }

    private static double Length(IReadOnlyDictionary<string, Value> p, string name, bool required)
        => OptionalLength(p, name) ?? (required ? throw new ParameterRefusalException($"{name} is not set: it is the element pitch, a length such as 25.86 mm or 0.5*lambda(f0).") : 0);

    private static double? OptionalLength(IReadOnlyDictionary<string, Value> p, string name)
    {
        if (!p.TryGetValue(name, out var v)) return null;
        double x = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
        if (!(x > 0)) throw new ParameterRefusalException($"{name}={v} is not a pitch: it is a positive length.");
        return x;
    }

    private static int[] Off(IReadOnlyDictionary<string, Value> p, int count)
    {
        string text = Word(p, "Off", "");
        var list = new List<int>();
        foreach (string t in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || k < 1 || k > count)
                throw new ParameterRefusalException($"Off={text} lists '{t}', which is not an element: elements are numbered 1 to {count}, row-major from the top left.");
            list.Add(k);
        }
        return [.. list.Distinct()];
    }
}

/// <summary>
/// R-paa4-5 / R-paa4-6 — the channel cell a Feed-mode array places per element: its name, whether it is IDENTICAL across elements (its
/// parameters read none of n, row, col, x, y — the fact PAA-8's fast path reads), which of the five it reads when it is not, and the
/// elements switched off whose channel was omitted (only an identical channel is omitted: a different one still carries its own noise).
/// </summary>
public sealed record PhasedArrayChannels(string Cell, string In, string Out, bool Identical, IReadOnlyList<string> Reads, IReadOnlyList<int> Omitted)
{
    /// <summary>The instance path of element <paramref name="n"/>'s (1-based) channel under array <paramref name="array"/>.</summary>
    public static string PathOf(string array, int n) => $"{array}.ch[{n}]";

    /// <summary>One sentence for a run's notes and explain.</summary>
    public string Sentence(string array)
    {
        string what = Identical
            ? $"identical across elements (its parameters read none of n, row, col, x, y)"
            : $"different per element (it reads {string.Join(", ", Reads)})";
        string off = Omitted.Count == 0 ? ""
            : $" The channel of {(Omitted.Count == 1 ? "element" : "elements")} {string.Join(", ", Omitted)}, switched off, is omitted and the element terminated in Z0 " +
              "at the beamformer; a channel that differs per element would be kept, its own noise still reaching the feed.";
        return $"{PhasedArrayModel.Describe(array)} places channel cell '{Cell}' once per element as {PathOf(array, 1)} … ({In} from the beamformer, {Out} to the element); it is {what}.{off}";
    }
}
