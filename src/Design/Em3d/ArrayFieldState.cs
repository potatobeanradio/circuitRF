// brief-paa-9 R-paa9-2…R-paa9-6 — what one fields analysis on a Phased Antenna Array leaves for a view of the array: the lattice as built, the
// elements' referred patterns, the waves on the elements, the weights and the record. The Phased Array tool draws its aperture, (u,v) map,
// cuts and readouts from these and from nothing else, so it shows the run's own numbers and never a second solve of its own.
//
// Everything here is a VALUE the run computed (or a closure over the model it ran), and nothing refers to the run's netlist, which is
// disposed when the run ends.

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-paa-9 — one array fields run's state, at its first frequency.</summary>
public sealed class ArrayFieldState
{
    internal ArrayFieldState(string analysis, string block, PhasedArrayModel pa, PortFarFieldSet far, double hz, Complex[] a, Complex[] b,
                             double availableW, double acceptedW, ArrayElements elements, PhasedArraySolvePath? path, bool estimated, bool harmonicBalance)
    {
        Analysis = analysis;
        Block = block;
        Lattice = pa.Lattice;
        Level = pa.Level;
        LevelNote = pa.LevelNote;
        Hz = hz;
        A = a;
        B = b;
        AvailableW = availableW;
        AcceptedW = acceptedW;
        Elements = elements;
        Grid = far.Grid;
        RecordFrequenciesHz = [.. far.FrequenciesHz];
        RecordPath = pa.RecordPath;
        Path = path;
        Estimated = estimated;
        HarmonicBalance = harmonicBalance;
        Feed = pa.Feed is not null;
        Z0 = pa.Z0;
        if (pa.Feed is { } feed)
        {
            Requested = [.. Enumerable.Range(0, pa.PortCount).Select(e => feed.Requested(e, hz))];
            Quantised = [.. Enumerable.Range(0, pa.PortCount).Select(e => feed.Quantised(e, hz))];
            _requestedAt = feed.Requested;
        }
        _elementsAt = f => FieldCosimulation.Elements(pa, far, f);
    }

    private readonly Func<int, double, Complex>? _requestedAt;
    private readonly Func<double, ArrayElements> _elementsAt;

    /// <summary>The fields analysis and the array instance it names.</summary>
    public string Analysis { get; }
    public string Block { get; }

    /// <summary>The lattice as built, with the elements switched off.</summary>
    public ArrayLattice Lattice { get; }

    public PhasedArrayLevel Level { get; }

    /// <summary>The run's own sentence for the level (D3).</summary>
    public string LevelNote { get; }

    /// <summary>The frequency of the pattern, Hz.</summary>
    public double Hz { get; }

    /// <summary>The waves incident on and leaving each element, row-major, on the scale the pattern sums them (peak waves, ½|a|² watts, the
    /// drives making <see cref="AvailableW"/> available); zero at an element switched off.</summary>
    public Complex[] A { get; }
    public Complex[] B { get; }

    /// <summary>The power the circuit's drive makes available and the power the circuit accepts from it, W.</summary>
    public double AvailableW { get; }
    public double AcceptedW { get; }

    /// <summary>Each element's referred embedded pattern at <see cref="Hz"/>.</summary>
    public ArrayElements Elements { get; }

    /// <summary>The record's far-field grid, the overview's.</summary>
    public PlanarFarFieldGrid Grid { get; }

    /// <summary>Every frequency the record holds a pattern at, Hz.</summary>
    public IReadOnlyList<double> RecordFrequenciesHz { get; }

    public string RecordPath { get; }

    /// <summary>A Feed-mode array's solve path (PAA-8): whether a move of the weights is a back-substitution or a full solve. Null in
    /// Elements mode.</summary>
    public PhasedArraySolvePath? Path { get; }

    /// <summary>True when the run held the circuit at its last solve and applied new weights (a drag off the fast path).</summary>
    public bool Estimated { get; }

    /// <summary>True when the waves are a harmonic-balance solution's (waves=).</summary>
    public bool HarmonicBalance { get; }

    /// <summary>True in Feed mode.</summary>
    public bool Feed { get; }

    public Complex Z0 { get; }

    /// <summary>A Feed-mode array's weights per element at <see cref="Hz"/>, requested and quantised; null in Elements mode.</summary>
    public Complex[]? Requested { get; }
    public Complex[]? Quantised { get; }

    /// <summary>Element <paramref name="element"/>'s requested weight at <paramref name="hz"/> (its delay turned at that frequency); null in
    /// Elements mode.</summary>
    public Complex? RequestedAt(int element, double hz) => _requestedAt?.Invoke(element, hz);

    /// <summary>The elements' referred patterns at another of the record's frequencies.</summary>
    public ArrayElements ElementsAt(double hz) => hz == Hz ? Elements : _elementsAt(hz);

    /// <summary>The power each element accepts, ½(|a|² − |b|²), W.</summary>
    public double AcceptedBy(int element) => 0.5 * (A[element].Magnitude * A[element].Magnitude - B[element].Magnitude * B[element].Magnitude);

    /// <summary>Element <paramref name="element"/>'s active reflection, null where it is off or carries no wave.</summary>
    public Complex? ActiveGamma(int element) => Lattice.IsOn(element) && A[element] != Complex.Zero ? B[element] / A[element] : null;
}

/// <summary>brief-paa-9 — a fields run's metrics object that also carries the array's state.</summary>
public interface IArrayFieldStateSource
{
    ArrayFieldState State { get; }
}
