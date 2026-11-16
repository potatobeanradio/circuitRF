// ================================================================
//  EnvelopeBaseband.cs  —  a non-affine element across the baseband envelope
//
//  The baseband product (k = 0) recovers C from stamps at 1 Hz and 2 Hz,
//  which is exact for a stamp affine in jw.  A line's Y stamp is not: it
//  goes as 1/(jw L) there, so its slope at DC comes out ~1/(dlt^2 L) —
//  ~1e15 for a 25-ohm quarter-wave line at 1 GHz — and swamped the 0.02 S
//  terminations until the LU found no pivot (E = 90-450 deg failed; longer
//  lines, with more L, only just survived).  Such an element is HELD at
//  its DC stamp for the baseband product instead: exact at DC, where the
//  bias is, and the stamp the DC solve and the harmonic-balance start
//  already use, so the run still starts at its own equilibrium.  What is
//  lost is its frequency dependence across the baseband band, and the run
//  warns, naming it, when that is large enough to matter.
//
//  An element is held only when its own baseband tangent is not exact, so
//  R/L/C, the sources and a fitted block never are, and a lumped circuit's
//  run is unchanged bit for bit — and only when its DC stamp is closer to
//  it across the band than the tangent is, so a nearly affine element (a
//  wBond, whose skin effect bends it slightly) keeps its tangent.  A two-conductor line is never held in an
//  envelope run either: it is stepped as its delay (EnvelopeLineDelay),
//  exact across the baseband for a lossless line.  What is still held is
//  a block whose rational fit was refused, and a coupled line, which is
//  never fitted (held shorted, below).
//
//  A LINE is not held at its w = 0 stamp: that is the resonance clamp of
//  its 1/(jw L) pole, a ~2e7 S purely IMAGINARY admittance, and the k = 0
//  envelope is real everywhere else (the interface currents and the
//  injections are projected to their real part), so a complex baseband
//  matrix behind a real projection is not passive and grew x4.8 a step
//  (src/Engine/RESOLVED.md, brief ENV-TLIN).  A line is held at what it is
//  at DC instead (IDcShortedTerminals): its terminal pairs shorted, by a
//  branch V_a = V_b exactly as an inductor is at DC.  The branch exists in
//  every product — I = 0 while not holding — so every product and the
//  harmonic-balance start have the same unknowns.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.Envelope;

/// <summary>An element whose stamp is its own at every frequency except while <see cref="Holding"/>, when it is its DC stamp
/// — or, for a line (<see cref="Shorts"/>), its terminal pairs shorted.</summary>
public sealed class BasebandHeldModel : ComponentModel, IReportsWarnings
{
    private readonly ElaboratedComponent _original;

    public BasebandHeldModel(ComponentModel inner, ElaboratedComponent original)
    {
        Inner = inner;
        _original = original;
        Shorts = (inner as IDcShortedTerminals)?.DcShortedTerminals;
    }

    public ComponentModel Inner { get; }
    public bool Holding { get; set; }
    /// <summary>The terminal pairs a line is held shorted on, one branch each; null for an element held at its own ω = 0 stamp.</summary>
    public IReadOnlyList<(int A, int B)>? Shorts { get; }
    public override int PortCount => Inner.PortCount;
    public override ModelKind Kind => Inner.Kind;
    public override string[] TerminalNames => Inner.TerminalNames;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (Shorts is null) { Inner.Stamp(mna, c, Holding ? 0.0 : omega); return; }
        foreach (var (a, b) in Shorts)
        {
            int br = mna.AddBranch();
            int na = c.Nodes[a], nb = c.Nodes[b];
            if (Holding && na != nb)
            {
                mna.AddBranchCurrent(br, na, nb);           // V_a - V_b = 0, the current a free unknown
                mna.AddConstraint(br, na, Complex.One);
                mna.AddConstraint(br, nb, -Complex.One);
            }
            else mna.AddBranchConstraint(br, br, Complex.One);   // I = 0: no branch
        }
        // The line itself, multiplier and all (its replacement carries none: a branch cannot be multiplied, and m shorts
        // in parallel are one short).
        if (!Holding) _original.Stamp(mna, omega);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => Inner is IReportsWarnings rw ? rw.DrainWarnings() : [];
}

public static class EnvelopeBaseband
{
    /// <summary>A baseband tangent this far off (relative, terminated — <see cref="EnvelopeTangentCheck"/>'s measure) is not
    /// exact: round-off on an affine stamp is ~1e-12, a line's is ~1.</summary>
    public const double ExactBelow = 1e-6;

    /// <summary>The held elements of one run; <see cref="Holding"/> switches them all, <see cref="Dispose"/> puts them back.</summary>
    public sealed class Hold : IDisposable
    {
        private readonly ElaboratedNetlist _nl;
        private readonly List<(int Index, ElaboratedComponent Original, BasebandHeldModel Held)> _held = [];
        internal Hold(ElaboratedNetlist nl) => _nl = nl;
        internal void Add(int index, ElaboratedComponent original, BasebandHeldModel held, ElaboratedComponent replacement)
        {
            _held.Add((index, original, held));
            _nl.Components[index] = replacement;
        }
        public int Count => _held.Count;
        public bool Holding { set { foreach (var h in _held) h.Held.Holding = value; } }
        public void Dispose()
        {
            foreach (var (i, ec, _) in _held) _nl.Components[i] = ec;
            _held.Clear();
        }
    }

    /// <summary>
    /// Wraps every linear element whose baseband tangent is not exact, for one envelope run over a baseband band of
    /// ±<paramref name="bandRad"/>, and warns naming those whose DC stamp is off by more than
    /// <see cref="EnvelopeTangentCheck.WarnAbove"/> across it. Call it before the run's extractor is built (it replaces
    /// netlist entries) and before the products are stamped (stamping alone re-numbers a source's branch).
    /// </summary>
    public static Hold Apply(ElaboratedNetlist nl, double bandRad)
    {
        var hold = new Hold(nl);
        var costly = new List<(string Instance, double Error)>();
        for (int i = 0; i < nl.Components.Count; i++)
        {
            var ec = nl.Components[i];
            if (ec.IsNonlinear || ec.Model is ModSourceModel or IInductiveBranch or MutualInductanceModel or PortModel or TermModel
                    or VdcModel or IdcModel or WaveformSourceModel or ToneSourceModelBase or TunerModel or P1ToneModel or PnToneModel or DelayLineModel
                    // brief-pch-10b — stepped by EnvelopeChannel (H(0) = 0); stamped alone, an instance end's ray unknowns (two a ray) made
                    // this check a dense inversion of ~1000 unknowns per call: 19 s of a 2 × 2 array's CDL-C run
                    or PropagationChannelModel or PhasedArrayModel or PhasedArrayFeedModel)   // an array: as EnvelopeSnpFit.NotAffine says
                continue;
            var model = new BasebandHeldModel(ec.Model, ec);
            var replacement = new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, model)
            {
                ReferenceNode = ec.ReferenceNode,
                Multiplicity = model.Shorts is null ? ec.Multiplicity : 1.0,
            };
            double held;
            if (model.Shorts is not null)
            {
                // A line's baseband tangent is never exact (its Y has a pole at w = 0), so it is not measured: stamping it at
                // 1 Hz only reaches the resonance clamp. What it costs is measured against the short it is held at.
                try { held = EnvelopeTangentCheck.HeldError(replacement, model, bandRad); }
                catch (Exception) { continue; }
            }
            else
            {
                double tangent;
                try { (tangent, held) = EnvelopeTangentCheck.BasebandErrors(ec, bandRad); }
                catch (Exception) { continue; }     // an element that cannot be stamped alone is the run's to refuse
                // Held only when the hold is the closer of the two: a wBond's skin effect puts its tangent at 6e-6 to 8e-4,
                // and its DC stamp (the wires' resistance, their inductance gone) at 0.3 % to 2 % (src/Engine/RESOLVED.md).
                if (tangent <= ExactBelow || tangent <= held) continue;
            }
            hold.Add(i, ec, model, replacement);
            if (held > EnvelopeTangentCheck.WarnAbove) costly.Add((ec.InstancePath, held));
        }
        if (costly.Count > 0)
        {
            var inv = CultureInfo.InvariantCulture;
            string names = string.Join(", ", costly.OrderByDescending(x => x.Error).Select(x => string.Format(inv, "'{0}' ({1:0.#} %)", x.Instance, 100 * x.Error)));
            nl.AddWarningOnce("env-baseband", string.Format(inv,
                "envelope: {0} {1} held at its DC value across the ±{2:0.##} MHz baseband envelope, off by that much (RMS) across it; " +
                "the bias-path and baseband results through {3} are approximate",
                names, costly.Count == 1 ? "is" : "are", bandRad / (2 * Math.PI) / 1e6, costly.Count == 1 ? "it" : "them"));
        }
        return hold;
    }
}
