// Z(f) at every observation port, the mask, the aggressor coincidences, the anti-resonance table
// and the removal ranking — P1's whole answer to Q1 and Q2 of railrf.md §2.1
// (docs/sonnet-briefs/brief-railrf-12-impedance.md; railrf.md §2.4 "Over frequency", §4.3, §4.4).
//
// ── NOTHING HERE IS A SOLVER, AND NOTHING HERE INVENTS A RESULT TYPE ───────────────────────────
//
// §4.4: "Assemble one sparse complex MNA system per frequency and solve for the port impedances via
// CSparse's LU — THE SAME NUMERICAL LAYER every other circuitRF analysis uses." So this file builds
// an ElaboratedNetlist, hands it to SParameterEngine and reads the answer back; it writes no
// assembly code, no factorisation, and its result is a DataSet carrying a Z cube. That is the same
// contract PdnNetlist states for the DC side, and it is what makes §5's claim true: a PDN curve
// overlays a measurement in an ordinary Data Display with no special support, because it IS an
// ordinary DataSet.
//
// ── P1 IS THE LUMPED PDN, AND THE PROVENANCE SAYS SO ───────────────────────────────────────────
//
// §6: "P1 — Lumped PDN. … ARTWORK OPTIONAL: mounting inductances may be typed." The copper's own
// R-L mesh is brief 13 and the cavity is brief 14, so the rail is ONE node here and every part,
// every source and every observation port hangs on it. The consequence a reader has to be told
// rather than left to discover: with no copper between them, every observation port on a rail reads
// the SAME curve. That is honest at P1 — the lumped model contains nothing that could make them
// differ — and it is stated on the result rather than left to look like a bug.
//
// ── THE NETLIST IS REBUILT AT EVERY FREQUENCY, ON PURPOSE ──────────────────────────────────────
//
// A part's ESR is not a constant: a class default is DF/(2*pi*f*C) and falls as 1/f, and a measured
// one is Re Z out of the part's own file. A netlist assembled once with one ESR would be a
// different circuit from the one §2.2 describes, and the error would be largest exactly at the
// resonances this brief exists to find — the depth of every minimum and the height of every
// anti-resonance is set by ESR. So each point gets its own assembly with that point's own numbers,
// which is also precisely what §4.4's "one sparse complex MNA system per frequency" says.
//
// NUMBERS ARE BASE SI. Hertz, ohms, farads, henries.

using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Pdn;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.RailRf;

/// <summary>Everything one frequency run reads.</summary>
public sealed class PdnSweepRequest
{
    /// <summary>The rail. Its band, its loads (the observation ports), its impedance target and its
    /// aggressors all come from here.</summary>
    public required RailSpec Rail { get; init; }

    /// <summary>The parts, already resolved against the library at the rail's voltage
    /// (<see cref="RailPartResolver.ResolveAll(IEnumerable{RailPart}, double?)"/>).</summary>
    public required RailPartModelSet Parts { get; init; }

    /// <summary>The sources as elements over frequency (<see cref="RailSourceLife.Of"/>). Empty is
    /// legal: a rail with no source is the decoupling network on its own, which is a question a user
    /// may legitimately ask.</summary>
    public IReadOnlyList<RailSourceModel> Sources { get; init; } = [];

    /// <summary>The grid, or null to take <see cref="RailSpec.Band"/>'s own.</summary>
    public double[]? FrequenciesHz { get; init; }

    /// <summary>
    /// How a coordinate anchor is spelled on a port label — the artwork's own units rather than DBU
    /// (owner, 2026-09-18). Defaulted to <see cref="RailLengthFormat.Dbu"/>, which says so out loud.
    /// </summary>
    public RailLengthFormat LengthFormat { get; init; } = RailLengthFormat.Dbu;

    /// <summary>Which of §2.9's two readings this answer belongs beside. <b>Carried, not used</b> —
    /// P1's sweep is lumped either way (see this file's own header), and the kind is what lets the
    /// window keep a Fast curve beside an Accurate one rather than replacing it.</summary>
    public PdnModelKind Model { get; init; } = PdnModelKind.Fast;

    /// <summary>
    /// <b>R-rail14-4 — how the band is SAMPLED, and null is the requested grid exactly.</b>
    ///
    /// <para>§4.4: adaptive sampling "is not optional once the cavity band is in scope: plane
    /// resonances are narrow and a log grid steps straight over one." With settings here, the EM
    /// engine's own resonance search adds the frequencies the grid missed and says which
    /// (<see cref="PdnSweepResult.AddedHz"/>); with null, the axis is the grid asked for, point for
    /// point. It is a setting rather than always-on because the added points change the x axis of a
    /// curve somebody may be overlaying on a measurement taken at their own frequencies.</para>
    /// </summary>
    public PdnSamplingSettings? Sampling { get; init; }

    /// <summary>§2.4's "a stated fraction". See <see cref="PdnCoincidence.DefaultFraction"/>.</summary>
    public double CoincidenceFraction { get; init; } = PdnCoincidence.DefaultFraction;

    /// <summary>How far a local maximum must stand above its flanks to be an anti-resonance.</summary>
    public double ProminenceDb { get; init; } = PdnAntiResonance.DefaultProminenceDb;

    /// <summary>
    /// Whether to compute §2.4's capacitor ranking — one re-solve of the whole sweep per part.
    ///
    /// <para>On by default because Q-4 closed it as <i>priority one</i>, and a knob at all because
    /// the cost is <c>parts × points</c> solves: it is milliseconds on a decoupling bank and it is
    /// the one thing here a caller might want to skip while a user is dragging a value.</para>
    /// </summary>
    public bool RankRemovals { get; init; } = true;

    /// <summary>
    /// The port reference impedance, in OHMS.
    ///
    /// <para>50 Ω, and it is a REFERENCE rather than a termination: <c>SParameterEngine</c> stamps
    /// it, and the S-to-Z conversion takes it back out exactly, so the Z matrix below is the
    /// network's own and does not depend on this number. It is settable only because the
    /// cancellation in <c>Z = Z0(1+S)/(1−S)</c> costs precision when |Z| is milliohms against a
    /// 50 Ω reference — about eleven significant figures survive at 1 mΩ, which is ample, and the
    /// knob is here for the case somebody one day finds where it is not.</para>
    /// </summary>
    public double PortReferenceOhms { get; init; } = 50.0;
}

/// <summary>
/// One observation port's answer.
/// </summary>
/// <param name="Index">Its index in the rail's own load list, so a result row matches a document row.</param>
/// <param name="Name">What the report calls it — the anchor's own spelling.</param>
/// <param name="MagnitudeOhms">|Z| at each swept frequency.</param>
/// <param name="Mask">What this port is judged against, or null where nothing stated a target.</param>
/// <param name="MaskReport">The verdict, with every violation's margin in dB.</param>
/// <param name="Peaks">§2.4's anti-resonance table, each row naming its two contributors.</param>
/// <param name="Coincidences">§2.4's coincidence check — the sentence the tool exists to produce.</param>
public sealed record PdnPortImpedance(
    int Index,
    string Name,
    RailPortAnchor Anchor,
    double[] MagnitudeOhms,
    PdnMask? Mask,
    PdnMaskReport MaskReport,
    IReadOnlyList<PdnAntiResonancePeak> Peaks,
    IReadOnlyList<PdnCoincidenceRow> Coincidences)
{
    /// <summary>
    /// This port's name in <paramref name="format"/>'s units — <see cref="Name"/> where none is given.
    /// </summary>
    /// <remarks>
    /// <b>For the window, which outlives the run.</b> <see cref="Name"/> is this same anchor described
    /// in the units the sweep was asked for, and a user who changes the board's display unit afterwards
    /// has not changed any number in this result — only how it is spelled. So the ANCHOR travels with
    /// the row and the spelling is asked for again, through the one <c>Describe</c> the run itself used
    /// rather than a second one that could come to disagree with it.
    /// </remarks>
    public string NameIn(RailLengthFormat? format) =>
        format is { } f ? Anchor.Describe(f) : Name;
}

/// <summary>
/// Everything one frequency run produced. <see cref="Refusal"/> non-null means NOTHING was swept —
/// the contract <see cref="PdnExtraction"/> and <see cref="RailDcRunResult"/> already state.
/// </summary>
/// <param name="Refusal">Why nothing was swept, or null.</param>
/// <param name="Data">The run, as every other circuitRF analysis returns it: a Z cube over
/// <c>[freq, port, port]</c>, the S cube it was converted from, the per-port Z0, and brief 11's
/// part-provenance metadata cubes. Null exactly when <paramref name="Refusal"/> is not.</param>
/// <param name="FrequenciesHz">The grid actually swept.</param>
/// <param name="Ports">One entry per observation port, in the rail's own order.</param>
/// <param name="Removal">§2.4's capacitor ranking, or empty where it could not be computed.</param>
/// <param name="Notes">What railRF established that the document did not state.</param>
/// <param name="Warnings">What a reader should act on.</param>
public sealed record PdnSweepResult(
    string? Refusal,
    DataSet? Data,
    double[] FrequenciesHz,
    IReadOnlyList<PdnPortImpedance> Ports,
    IReadOnlyList<PdnRemovalRow> Removal,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// <b>R-rail14-4 — the frequencies the resonance search ADDED to the grid that was asked for.</b>
    ///
    /// <para>Empty where no sampling settings were given, which is the requested grid point for
    /// point. Reported rather than folded in silently: a curve whose x axis grew is one nobody can
    /// compare against a measurement taken on the axis they asked for, and "the tool found something
    /// between your points" is the useful half of the sentence.</para>
    /// </summary>
    public IReadOnlyList<double> AddedHz { get; init; } = [];

    /// <summary>What the search located, with each one's f₀, Q and the width it was bracketed to.
    /// <b>The modes themselves are brief 15</b>; these are the peaks of THIS curve at THIS port,
    /// which is a different claim and a weaker one.</summary>
    public IReadOnlyList<PlanarResonance> Resonances { get; init; } = [];

    /// <summary>Which of §2.9's two readings this belongs beside.</summary>
    public PdnModelKind ModelKind { get; init; } = PdnModelKind.Fast;

    /// <summary>The same fact as the sentence a report prints.</summary>
    public string Model { get; init; } = "";

    /// <summary>The rail this is of.</summary>
    public string RailName { get; init; } = "";

    /// <summary>
    /// The excitation set this answer was checked against.
    ///
    /// <para><b>On the RESULT, not read back off the document</b> — §2.4 draws the aggressors on the
    /// same axis as the curve, and a plot that re-read the rail's rows would draw the set as it is
    /// NOW beside a curve computed from the set as it WAS. On a window whose rows are being edited
    /// under the plot that is not a hypothetical.</para>
    /// </summary>
    public IReadOnlyList<PdnAggressorLine> Aggressors { get; init; } = [];

    /// <summary>Every aggressor line to draw: the fundamental and each harmonic, as its own row.</summary>
    public IEnumerable<(string Name, double FrequencyHz, int Harmonic)> AggressorLines =>
        Aggressors.Where(a => a.FundamentalHz > 0)
                  .SelectMany(a => Enumerable
                      .Range(1, Math.Max(1, a.Harmonics))
                      .Select(h => (a.Name, a.FundamentalHz * h, h)));

    /// <summary>
    /// <b>R-rail12-7.</b> True where any number behind these curves is a class-default ESR, so every
    /// peak height and every mask margin here is <see cref="RailEsrDefaults.Marking">indicative</see>.
    /// It is on every row as well as here — a margin type that cannot carry the flag is the wrong
    /// type — and this is what an export's provenance line reads.
    /// </summary>
    public bool Indicative { get; init; }

    /// <summary>The least margin over every port, or null where nothing was judged. <b>The number
    /// the removal ranking moves.</b></summary>
    public double? WorstMarginDb =>
        Ports.Select(p => p.MaskReport.WorstMarginDb)
             .Where(m => m is not null)
             .DefaultIfEmpty(null)
             .Min();

    /// <summary>Every coincidence on every port, worst first — §2.4's short list.</summary>
    public IReadOnlyList<PdnCoincidenceRow> Coincidences =>
        [.. Ports.SelectMany(p => p.Coincidences)
                 .OrderBy(r => r.Peak.MarginDb ?? double.PositiveInfinity)
                 .ThenByDescending(r => r.Peak.PeakOhms)];

    /// <summary>The parts §2.6 step 7 calls candidates for deletion.</summary>
    public IReadOnlyList<PdnRemovalRow> Redundant => PdnRemovalRanking.Redundant(Removal);

    /// <summary>
    /// This rail's own |Z| at one frequency, read off the curve that is already computed —
    /// <b>R-rail21-1c</b>. Null where nothing was swept, the port is not one of these, or the
    /// frequency is outside the grid.
    /// </summary>
    /// <remarks>
    /// <b>Interpolated in log |Z| against log f, which is the axis the curve is DRAWN on.</b> A PDN
    /// impedance spans decades between a bank's minimum and an anti-resonance, and a linear reading
    /// between two grid points straddling a peak is wrong by most of the peak. Nothing is
    /// extrapolated: outside the swept band there is no curve, and a number invented there is a
    /// number a reader believes.
    ///
    /// <para><b>It exists so the map's readout can print BOTH numbers</b> — the plane pair's ohms
    /// and the rail's, named, at the moment the question is asked rather than in a document (see
    /// <see cref="PdnImpedanceNames"/>). It costs one interpolation into a curve already in hand.
    /// </para>
    /// </remarks>
    /// <param name="portIndex">The port's index in the rail's own load list.</param>
    /// <param name="hz">The frequency to read at.</param>
    public double? MagnitudeAt(int portIndex, double hz)
    {
        if (Refusal is not null || !(hz > 0) || FrequenciesHz.Length == 0) return null;

        var port = Ports.FirstOrDefault(p => p.Index == portIndex);
        if (port is null || port.MagnitudeOhms.Length != FrequenciesHz.Length) return null;

        double[] f = FrequenciesHz, z = port.MagnitudeOhms;

        if (hz < f[0] || hz > f[^1]) return null;

        int i = Array.BinarySearch(f, hz);
        if (i >= 0) return z[i];

        i = ~i;                                   // the first point ABOVE hz; f[0] < hz < f[^1]
        if (i <= 0 || i >= f.Length) return null;

        double f0 = f[i - 1], f1 = f[i], z0 = z[i - 1], z1 = z[i];
        if (!(f0 > 0) || !(f1 > 0) || !(z0 > 0) || !(z1 > 0) || f1 <= f0)
            return f1 > f0 ? z0 + (z1 - z0) * (hz - f0) / (f1 - f0) : z0;

        double t = (Math.Log(hz) - Math.Log(f0)) / (Math.Log(f1) - Math.Log(f0));
        return Math.Exp(Math.Log(z0) + t * (Math.Log(z1) - Math.Log(z0)));
    }

    internal static PdnSweepResult Refused(string why) => new(why, null, [], [], [], [], []);
}

/// <summary>§2.4's frequency answer.</summary>
public static class PdnSweep
{
    /// <summary>
    /// Sweeps one rail, or refuses and says why.
    /// </summary>
    public static PdnSweepResult Run(PdnSweepRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rail = request.Rail;
        if (rail.Refusal() is { } railRefusal) return PdnSweepResult.Refused(railRefusal);

        var notes = new List<string>();
        var warnings = new List<string>();

        var branches = Branches(request, notes, warnings, out string? branchRefusal);
        if (branchRefusal is { } why) return PdnSweepResult.Refused(why);

        if (branches.Count == 0)
            return PdnSweepResult.Refused(
                $"Rail '{rail.Name}' has nothing to sweep: no source states an output impedance and " +
                "no part resolved to a capacitance. Add a source series resistance, or parts the " +
                "library has rows for.");

        if (rail.Loads.Count == 0)
            return PdnSweepResult.Refused(
                $"Rail '{rail.Name}' has no load, so it has no observation port and there is nothing " +
                "to measure an impedance at. Add the load whose impedance you want (a load with no " +
                "current is an observation port, which is exactly this case).");

        double[] freqs = request.FrequenciesHz is { Length: > 1 }
            ? [.. request.FrequenciesHz]
            : Grid(rail.Band);

        // ── R-rail14-4: the grid a resonance can hide between is not the grid that gets judged ──
        //
        // §4.4: "plane resonances are narrow and a log grid steps straight over one." Everything
        // below — the mask verdict, the anti-resonance table, the coincidence rows and the removal
        // ranking — is read off THIS axis, so a peak the grid stepped over is absent from all four
        // at once and nothing reports it. PdnAdaptiveSweep adds what the grid missed, through the EM
        // engine's own sampler; with no sampling settings the axis is the requested grid exactly.
        int ports = rail.Loads.Count;
        var sampled = PdnAdaptiveSweep.Run(
            freqs, new Complex(request.PortReferenceOhms, 0), request.Sampling,
            f => SolveOne(request, branches, f, ports));

        freqs = sampled.FrequenciesHz;
        notes.AddRange(sampled.Notes);

        var portZ0 = new Complex[ports];
        Array.Fill(portZ0, new Complex(request.PortReferenceOhms, 0));

        var zMatrices = new Mat<Complex>[freqs.Length];
        var sMatrices = sampled.S;
        var magnitudes = new double[ports][];
        for (int k = 0; k < ports; k++) magnitudes[k] = new double[freqs.Length];

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            zMatrices[fi] = RFNetwork.SToZ(sMatrices[fi], portZ0);
            for (int k = 0; k < ports; k++) magnitudes[k][fi] = zMatrices[fi][k, k].Magnitude;
        }

        var data = Pack(request, freqs, zMatrices, sMatrices);

        bool indicative = request.Parts.AnyIndicative;
        if (indicative && request.Parts.IndicativeLine is { } line) notes.Add(line);

        // ── the per-port answer ────────────────────────────────────────────────────────────────

        var portRows = new List<PdnPortImpedance>(rail.Loads.Count);
        var aggressors = rail.Aggressors
            .Select(a => new PdnAggressorLine(a.Name, a.FrequencyHz, a.Harmonics))
            .ToArray();

        for (int k = 0; k < rail.Loads.Count; k++)
        {
            var mask = MaskFor(rail, rail.Loads[k], notes, request.LengthFormat);
            var curve = magnitudes[k];
            var report = PdnMask.Judge(mask, freqs, curve, indicative);

            var peaks = new List<PdnAntiResonancePeak>();
            foreach (int i in PdnAntiResonance.FindPeaks(curve, request.ProminenceDb))
                peaks.Add(PdnAntiResonance.Attribute(
                    i, freqs[i], curve[i],
                    branches.Where(b => b.Group is not null)
                            .Select(b => new PdnBranchAdmittance(
                                b.Group!, b.Member!, Admittance(b, freqs[i]))),
                    mask?.LimitAt(freqs[i]), indicative));

            portRows.Add(new PdnPortImpedance(
                k, rail.Loads[k].Anchor.Describe(request.LengthFormat), rail.Loads[k].Anchor,
                curve, mask, report, peaks,
                PdnCoincidence.Find(peaks, aggressors, request.CoincidenceFraction)));
        }

        if (rail.Loads.Count > 1)
            notes.Add(
                "Every observation port on this rail reads the same curve. That is P1's lumped model " +
                "rather than a defect: there is no copper between the ports yet, so nothing in the " +
                "model can make them differ. The distributed low band is P2a.");

        // ── §2.4's capacitor ranking ───────────────────────────────────────────────────────────

        var removal = Rank(request, branches, freqs, portRows, notes);

        return new PdnSweepResult(null, data, freqs, portRows, removal, notes, warnings)
        {
            AddedHz = sampled.AddedHz,
            Resonances = sampled.Resonances,
            ModelKind = request.Model,
            Model = request.Model == PdnModelKind.Accurate ? "Accurate (mesh)" : "Fast (graph)",
            RailName = rail.Name,
            Indicative = indicative,
            Aggressors = aggressors,
        };
    }

    // ── the branches ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One two-terminal element between the rail and its reference.
    /// </summary>
    /// <param name="Path">Its instance path in the netlist.</param>
    /// <param name="Impedance">What it is at one frequency, in ohms.</param>
    /// <param name="Group">The bank it belongs to for R-rail12-5's attribution, or null where it is
    /// not a part — a source is never named as a contributor because it is not a thing a user can
    /// take off the board.</param>
    /// <param name="Member">Its own name within that bank.</param>
    /// <param name="PartName">The part this is, for the removal ranking, or null.</param>
    private sealed record Branch(
        string Path,
        Func<double, Complex> Impedance,
        string? Group,
        string? Member,
        string? PartName);

    private static Complex Admittance(Branch b, double f)
    {
        var z = b.Impedance(f);
        return z == Complex.Zero ? Complex.Zero : Complex.One / z;
    }

    /// <summary>
    /// §4.3's attachments, as impedances. A part is its own R(f)-L-C in series with its mounting
    /// loop; a source is its R-L or its own measured curve.
    /// </summary>
    private static List<Branch> Branches(
        PdnSweepRequest request, List<string> notes, List<string> warnings, out string? refusal)
    {
        refusal = null;
        var branches = new List<Branch>();

        for (int k = 0; k < request.Sources.Count; k++)
        {
            var source = request.Sources[k];

            if (source.Basis == RailSourceBasis.Rl &&
                source.SeriesResistanceOhms is not { } && source.SeriesInductanceHenries is not { })
            {
                // An ideal source is a SHORT at every frequency, so the rail's impedance would come
                // out identically zero and every mask would pass. That is not a small error to note
                // beside a number; it is the absence of an answer.
                refusal =
                    $"Source {source.Name} states no series resistance and no series inductance, so " +
                    "at every frequency it shorts the rail to its reference and |Z| is identically " +
                    "zero. State its series resistance — a cell's is ohms to hundreds of ohms across " +
                    "its life — or attach its published output-impedance curve.";
                return branches;
            }

            if (source.Basis == RailSourceBasis.Measured && source.Measured is null)
            {
                warnings.Add(
                    $"Source {source.Name} names a Touchstone file that could not be read, so it is " +
                    "not in this answer at all. It was not replaced by an R-L.");
                continue;
            }

            var band = source.Measured?.Band;
            branches.Add(new Branch(
                $"source{k + 1}",
                f => source.ImpedanceAt(band is { } b ? Math.Clamp(f, b.LowHz, b.HighHz) : f),
                Group: null, Member: null, PartName: null));

            if (band is { } sb && (sb.LowHz > request.Rail.Band.StartHz ||
                                   sb.HighHz < request.Rail.Band.StopHz))
                notes.Add(HeldSentence(source.Name, sb.LowHz, sb.HighHz));
        }

        int unmodelled = 0;

        foreach (var part in request.Parts.Models)
        {
            if (!part.IsResolved ||
                !(part.CapacitanceFarads > 0) ||
                part.TotalInductanceHenries is not { } l || !(l > 0) ||
                part.EsrBasis is null)
            {
                unmodelled++;
                continue;
            }

            double c = part.CapacitanceFarads;
            var measuredBand = part.Measured?.Band;

            // Q-15's class default falls as 1/f and a measured ESR is Re Z out of the part's own
            // file; both are read AT the frequency being solved, which is why the netlist is rebuilt
            // per point rather than assembled once.
            //
            // OUTSIDE a measured file's own band the ESR is HELD at the nearest end it states, and
            // the result says so once per part. Two alternatives were available and both are worse:
            // dropping the branch changes the topology between one sweep point and the next, which
            // manufactures an anti-resonance at the file's own band edge out of nothing; and a zero
            // ESR makes every peak the part takes part in unbounded. A held figure is wrong by a
            // bounded amount in a band where the part's reactance dominates it anyway, and the
            // sentence is what stops it being read as a measurement.
            Func<double, Complex> z = f =>
            {
                double w = 2.0 * Math.PI * f;
                double r = part.EsrOhmsAt(measuredBand is { } mb ? Math.Clamp(f, mb.LowHz, mb.HighHz) : f);
                return new Complex(double.IsFinite(r) ? r : 0.0, w * l - 1.0 / (w * c));
            };

            // The bank a part belongs to is its PART NUMBER — identity, never value, type or
            // proximity (R-rail12-5). Nine 100 nF parts of one part number are one bank because they
            // are one purchased item placed nine times, and the contributor label then reads as the
            // refdes range they span.
            string group = part.Row?.PartNumber is { Length: > 0 } pn ? pn : part.PartNumber;

            branches.Add(new Branch(
                $"part{branches.Count + 1}", z, group, part.Refdes ?? part.Name, part.Name));

            if (measuredBand is { } pb && (pb.LowHz > request.Rail.Band.StartHz ||
                                           pb.HighHz < request.Rail.Band.StopHz))
                notes.Add(HeldSentence(part.Name, pb.LowHz, pb.HighHz));
        }

        if (unmodelled > 0)
            warnings.Add(
                $"{unmodelled} part(s) on this rail are NOT in this answer: they resolved to no " +
                "capacitance, no inductance, or no ESR basis at all. An unstated value is never a " +
                "defaulted one, and a part stamped with no loss would make every peak it takes part " +
                "in unbounded. " + request.Parts.Summary);

        return branches;

        static string HeldSentence(string name, double lowHz, double highHz) =>
            $"{name}'s own file covers {PdnMask.Hertz(lowHz)} to {PdnMask.Hertz(highHz)}. " +
            "Outside that its ESR is HELD at the nearest end the file states — it is not a " +
            "measurement there, and its reactance is the model's own R-L-C.";
    }

    // ── the solve ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One assembly and one solve per frequency; |Z| per port out.
    /// </summary>
    private static double[][] Solve(
        PdnSweepRequest request, List<Branch> branches, double[] freqs,
        Mat<Complex>[] zOut, Mat<Complex>[] sOut)
    {
        int ports = request.Rail.Loads.Count;
        var magnitudes = new double[ports][];
        for (int k = 0; k < ports; k++) magnitudes[k] = new double[freqs.Length];

        var z0 = new Complex[ports];
        Array.Fill(z0, new Complex(request.PortReferenceOhms, 0));

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            sOut[fi] = SolveOne(request, branches, freqs[fi], ports);
            zOut[fi] = RFNetwork.SToZ(sOut[fi], z0);

            for (int k = 0; k < ports; k++) magnitudes[k][fi] = zOut[fi][k, k].Magnitude;
        }

        return magnitudes;
    }

    /// <summary>
    /// One assembly and one solve at ONE frequency. <b>Named because R-rail14-4's sampler is handed
    /// it as a probe</b> — the search decides which frequencies exist and this decides what is at
    /// them, and keeping the two apart is what lets the EM engine's own sampler drive a PDN sweep
    /// without knowing anything about one.
    /// </summary>
    private static Mat<Complex> SolveOne(
        PdnSweepRequest request, List<Branch> branches, double frequencyHz, int ports)
    {
        using var netlist = Assemble(request, branches, frequencyHz, ports);
        var raw = SParameterEngine.Run(netlist, [frequencyHz])["S"].ComplexValues;

        var s = new Mat<Complex>(ports, ports);
        for (int i = 0; i < ports; i++)
            for (int j = 0; j < ports; j++)
                s[i, j] = raw[i * ports + j];
        return s;
    }

    /// <summary>
    /// The lumped rail at one frequency: node 1 is the rail, node 0 is its reference, and every
    /// branch and every port is between them.
    /// </summary>
    /// <remarks>
    /// <b>A complex impedance is stamped as a resistance in series with one reactance of the right
    /// sign</b>, through an internal node. That is exact at the frequency being assembled — which is
    /// the only frequency this netlist is ever solved at — rather than a fit: there is no
    /// two-terminal model in circuitRF that takes an arbitrary <c>R + jX</c>, and synthesising a
    /// fixed R-L-C whose reactance happened to match at every point is precisely the thing a
    /// per-point assembly exists to avoid.
    /// </remarks>
    private static ElaboratedNetlist Assemble(
        PdnSweepRequest request, List<Branch> branches, double frequencyHz, int ports)
    {
        var netlist = new ElaboratedNetlist();
        int rail = netlist.Nodes.GetOrAssign("rail");
        double w = 2.0 * Math.PI * frequencyHz;

        foreach (var b in branches)
        {
            var z = b.Impedance(frequencyHz);
            if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary)) continue;

            // A passive branch cannot have a negative resistance; a measured file occasionally
            // states one within its own noise, and stamping it would put energy into the rail.
            double r = Math.Max(0.0, z.Real);
            double x = z.Imaginary;

            // The two elements meet at an internal node only when there are two of them. A branch
            // that is pure R, pure X, or an outright short is one element between the rail and its
            // reference — and a spare node carrying one element would show up in the node count
            // without changing an answer.
            int inner = r > 0 && x != 0 ? netlist.Nodes.GetOrAssign($"{b.Path}.i") : 0;

            if (r > 0) Add(netlist, "R", $"{b.Path}.r", [rail, inner], new ResistorModel(), ("R", r));

            int from = r > 0 ? inner : rail;

            if (x > 0)
                Add(netlist, "L", $"{b.Path}.l", [from, 0], new InductorModel(), ("L", x / w));
            else if (x < 0)
                Add(netlist, "C", $"{b.Path}.c", [from, 0], new CapacitorModel(), ("C", -1.0 / (w * x)));
            else if (r <= 0)
                Add(netlist, "R", $"{b.Path}.r", [rail, 0], new ResistorModel(), ("R", 0.0));
        }

        for (int k = 0; k < ports; k++)
            Add(netlist, "Port", $"port{k + 1}", [rail, 0], new PortModel(),
                ("Num", k + 1), ("Z", request.PortReferenceOhms));

        return netlist;
    }

    private static void Add(
        ElaboratedNetlist netlist, string type, string path, int[] nodes, ComponentModel model,
        params (string Key, double Value)[] parameters)
    {
        var resolved = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters) resolved[key] = new Value(value);
        netlist.AddComponent(new ElaboratedComponent(type, path, nodes, resolved, model));
    }

    // ── the result ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §4.4's DataSet: <b>a Z cube over <c>[freq, port, port]</c></b>, the S cube it was converted
    /// from, the per-port reference, and brief 11's part-provenance metadata.
    /// </summary>
    /// <remarks>
    /// <b>R-rail12-2: an integer on an <c>i</c>/<c>j</c> axis is a 1-based PORT NUMBER, not an
    /// index.</b> That is the <c>plot</c> verb's own recorded trap and it applies to every cube here
    /// — converting it draws S12 for <c>i=2,j=1</c> in silence, which is invisible on a reciprocal
    /// part, and a PDN Z matrix is reciprocal. So the port axes are built by
    /// <see cref="DataSetBuilder.FromSnp"/>'s own arithmetic for the S cube and by the identical
    /// arithmetic here for the Z cube, rather than by a second convention.
    /// </remarks>
    private static DataSet Pack(
        PdnSweepRequest request, double[] freqs, Mat<Complex>[] z, Mat<Complex>[] s)
    {
        int ports = request.Rail.Loads.Count;

        var snp = new SNP(freqs, s, MatrixType.S, MatrixFormat.RI,
                          new Complex(request.PortReferenceOhms, 0));
        var data = DataSetBuilder.FromSnp(snp);

        var portValues = new double[ports];
        for (int p = 0; p < ports; p++) portValues[p] = p + 1;

        var labels = new string[ports];
        for (int p = 0; p < ports; p++) labels[p] = request.Rail.Loads[p].Anchor.Describe(request.LengthFormat);

        var flat = new Complex[freqs.Length * ports * ports];
        for (int fi = 0; fi < freqs.Length; fi++)
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                    flat[(fi * ports + i) * ports + j] = z[fi][i, j];

        data.Add("Z", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", portValues, "port", labels),
             new Axis("j", (double[])portValues.Clone(), "port", (string[])labels.Clone())],
            flat)
        { Unit = "Ohm" });

        // R-rail11-4: the indicative marking has to cross the DataSet boundary or it reaches none of
        // §9's five places. This is the boundary.
        request.Parts.Annotate(data);

        return data;
    }

    // ── the target ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What one port is judged against — <b>its own mask where it has one</b> (§2.2: masks are per
    /// observation port, so a mask lives on the load row), and the rail's single-number target
    /// otherwise.
    /// </summary>
    private static PdnMask? MaskFor(RailSpec rail, RailLoad load, List<string> notes,
                                    RailLengthFormat format)
    {
        if (load.Mask?.Mask is { Count: >= 2 } points)
            return PdnMask.Piecewise(
                points.Select(p => new PdnMaskPoint(p.FrequencyHz, p.LimitOhms)),
                $"{load.Anchor.Describe(format)}'s own mask");

        if (rail.ImpedanceTarget is not { } target || target.FlatTargetOhms is not { } ohms)
            return null;

        // §2.2: a transient target derives BOTH the flat Z and the top of the band that matters.
        // Above the knee the classic PDN target says nothing, so the mask stops there rather than
        // being extended to the sweep's own top — which would report violations against a limit
        // nobody stated.
        double top = rail.Band.StopHz;
        if (target.BandTopHz is { } knee)
        {
            if (knee < top) top = knee;
            else if (knee > rail.Band.StopHz)
                notes.Add(
                    $"The transient target's knee is {PdnMask.Hertz(knee)}, above this rail's " +
                    $"band top of {PdnMask.Hertz(rail.Band.StopHz)}. The answer stops at the " +
                    "band; raise the band to judge the whole of what the load's own edge reaches.");
        }

        return PdnMask.Flat(ohms, rail.Band.StartHz, top);
    }

    // ── §2.4's capacitor ranking ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One re-solve per part, never a sensitivity (R-rail12-6).
    /// </summary>
    /// <remarks>
    /// The ranking needs something to move, so it is computed against the <b>worst mask margin over
    /// every port</b>. A rail whose ports state no target has nothing to rank and says so rather
    /// than ranking parts by peak height, which would be a different question with the same-looking
    /// answer.
    /// </remarks>
    private static IReadOnlyList<PdnRemovalRow> Rank(
        PdnSweepRequest request, List<Branch> branches, double[] freqs,
        IReadOnlyList<PdnPortImpedance> ports, List<string> notes)
    {
        if (!request.RankRemovals) return [];

        var parts = branches.Where(b => b.PartName is not null).ToList();
        if (parts.Count == 0) return [];

        double? baseline = ports.Select(p => p.MaskReport.WorstMarginDb)
                                .Where(m => m is not null)
                                .DefaultIfEmpty(null)
                                .Min();

        if (baseline is not { } worst)
        {
            notes.Add(
                "No port on this rail states an impedance target, so there is no margin for the " +
                "capacitor ranking to move and it was not computed. State a flat target, a transient " +
                "one, or a mask on the load row.");
            return [];
        }

        bool indicative = request.Parts.AnyIndicative;

        return PdnRemovalRanking.Rank(
            worst,
            [.. parts.Select(p => p.PartName!)],
            i =>
            {
                var without = branches.Where(b => !ReferenceEquals(b, parts[i])).ToList();
                var z = new Mat<Complex>[freqs.Length];
                var s = new Mat<Complex>[freqs.Length];
                var magnitudes = Solve(request, without, freqs, z, s);

                double? found = null;
                for (int k = 0; k < ports.Count; k++)
                {
                    var report = PdnMask.Judge(ports[k].Mask, freqs, magnitudes[k], indicative);
                    if (report.WorstMarginDb is { } m && (found is null || m < found)) found = m;
                }

                return found ?? double.NaN;
            });
    }

    // ── the grid ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rail's own band as a frequency array.
    ///
    /// <para><b>Log spacing is the default and the reason is in <see cref="RailBand"/>'s own
    /// header</b>: 10 kHz to 200 MHz is four decades and a linear sweep spends almost every point in
    /// the top one — which is the decade where a PDN curve has least structure.</para>
    /// </summary>
    public static double[] Grid(RailBand band)
    {
        int n = Math.Max(2, band.Points);
        var f = new double[n];

        if (band.Logarithmic && band.StartHz > 0)
        {
            double lo = Math.Log10(band.StartHz), hi = Math.Log10(band.StopHz);
            for (int i = 0; i < n; i++) f[i] = Math.Pow(10.0, lo + (hi - lo) * i / (n - 1.0));
            return f;
        }

        for (int i = 0; i < n; i++) f[i] = band.StartHz + (band.StopHz - band.StartHz) * i / (n - 1.0);
        return f;
    }
}
