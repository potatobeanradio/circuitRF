// ================================================================
//  ItuP681.Model.cs  —  P.681's state averages (§6.1 Steps 0-2) and the stateless state series of one drop (§6.2)
//
//  See ItuP681.cs for the model and its sources.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    public static partial class ItuP681
    {
        /// <summary>§6.1 Step 0: of <paramref name="environment"/>'s parameter sets, the one at the nearest frequency, then the nearest
        /// elevation. Refuses a frequency outside 1.5-20 GHz and an elevation outside 20°-90°.</summary>
        public static P681Set Select(P681Environment environment, double fHz, double elevationDeg)
        {
            PropagationMath.Range(M, "the frequency", fHz / 1e9, 1.5, 20, "GHz");
            PropagationMath.Range(M, "the elevation", elevationDeg, 20, 90, "deg");
            var mine = Sets.Where(s => s.Environment == environment).ToList();
            if (mine.Count == 0) throw new PropagationRefusal($"{M}: there is no parameter set for the {Name(environment)} environment.");
            double f = mine.Select(s => s.FrequencyHz).Distinct().MinBy(x => Math.Abs(x - fHz));
            return mine.Where(s => s.FrequencyHz == f).MinBy(s => Math.Abs(s.ElevationDeg - elevationDeg))!;
        }

        /// <summary>The MA range a state's draws are kept within: µ ± 1.645σ for GOOD, the [pB,min, pB,max] quantiles for BAD.</summary>
        public static (double Min, double Max) MaRange(P681Set set, bool good)
        {
            var s = good ? set.Good : set.Bad;
            return good ? (s.MuMa - 1.645 * s.SigmaMa, s.MuMa + 1.645 * s.SigmaMa)
                        : (s.MuMa + s.SigmaMa * Normal.InverseCdf(set.PbMin), s.MuMa + s.SigmaMa * Normal.InverseCdf(set.PbMax));
        }

        /// <summary>Eq. 17a: a state's mean event length (m), the mean of its lognormal truncated at durmin.</summary>
        public static double MeanDuration(P681State s)
        {
            double a = (Math.Log(s.DurMin) - s.Mu) / s.Sigma;
            return Math.Exp(s.Mu + s.Sigma * s.Sigma / 2) * Normal.Cdf(s.Sigma - a) / Normal.Cdf(-a);
        }

        /// <summary>The mean of MA over a state's kept range (the truncated normal's mean).</summary>
        public static double MeanMa(P681Set set, bool good)
        {
            var s = good ? set.Good : set.Bad;
            if (s.SigmaMa == 0) return s.MuMa;
            var (lo, hi) = MaRange(set, good);
            double a = (lo - s.MuMa) / s.SigmaMa, b = (hi - s.MuMa) / s.SigmaMa;
            return s.MuMa + s.SigmaMa * (Normal.Pdf(a) - Normal.Pdf(b)) / (Normal.Cdf(b) - Normal.Cdf(a));
        }

        /// <summary>Eq. 17b: the mean transition length (m), f1·(µMA,G − ⟨MA⟩B) + f2, ⟨MA⟩B the BAD state's truncated mean.</summary>
        public static double MeanTransition(P681Set set) => set.F1 * (set.Good.MuMa - MeanMa(set, false)) + set.F2;

        /// <summary>Eq. 19: the probabilities of the GOOD and BAD states, a transition counted half to each.</summary>
        public static (double Good, double Bad) Occupancy(P681Set set)
        {
            double g = MeanDuration(set.Good), b = MeanDuration(set.Bad), t = MeanTransition(set);
            return ((g + t) / (g + b + 2 * t), (b + t) / (g + b + 2 * t));
        }
    }

    /// <summary>The Loo triplet in force at a point: MA and ΣA of the direct signal (dB), MP the diffuse multipath's power (dB).</summary>
    public readonly record struct LooTriplet(double Ma, double SigmaA, double Mp)
    {
        public static LooTriplet Lerp(LooTriplet a, LooTriplet b, double s)
            => new(a.Ma + (b.Ma - a.Ma) * s, a.SigmaA + (b.SigmaA - a.SigmaA) * s, a.Mp + (b.Mp - a.Mp) * s);
    }

    /// <summary>
    /// One drop's P.681 state series along the route, from the point the drop starts at (x = 0): stateless, a function of (seed,
    /// drop, stream) alone, extended on demand. See ItuP681.cs for the construction.
    /// </summary>
    public sealed class P681Series
    {
        /// <summary>One stretch of the route: a GOOD or BAD event, or the transition between two (From → To).</summary>
        public sealed record Segment(double StartM, double EndM, bool? Good, LooTriplet From, LooTriplet To, bool FromGood);

        public P681Set Set { get; }
        private readonly ulong _seed;
        private readonly long _drop;
        private readonly string _stream;
        private readonly List<Segment> _segments = [];
        private int _next;                 // the next event's index
        private bool _nextGood;
        private LooTriplet _last;          // the last event's triplet
        private bool _lastGood;

        // the direct signal's correlated shadowing: a unit Gaussian in x with correlation e^(−|Δx|/Lcorr), a sum of sinusoids
        private const int Sinusoids = 32;
        private readonly double[] _kGood, _kBad, _phi;

        /// <summary>The events generated so far, the first (the one a drop starts in, length-biased) included.</summary>
        public IEnumerable<Segment> Events => _segments.Where(s => s.Good is not null);

        public P681Series(P681Set set, ulong seed, long drop, string stream)
        {
            Set = set; _seed = seed; _drop = drop; _stream = stream;
            _kGood = new double[Sinusoids]; _kBad = new double[Sinusoids]; _phi = new double[Sinusoids];
            ulong ks = StatDraws.Id(stream + ":p681:direct");
            double off = StatDraws.Uniform(seed, drop, ks, 0);
            for (int n = 0; n < Sinusoids; n++)
            {
                double c = Math.Tan(Math.PI * ((n + off) / Sinusoids - 0.5)) / (2 * Math.PI);   // the exponential correlation's spectrum
                _kGood[n] = 2 * Math.PI * c / set.Good.Lcorr;
                _kBad[n] = 2 * Math.PI * c / set.Bad.Lcorr;
                _phi[n] = 2 * Math.PI * StatDraws.Uniform(seed, drop, ks, 1 + n);
            }
            Start();
        }

        private double U(int eventIndex, int k) => StatDraws.Uniform(_seed, _drop, StatDraws.Id($"{_stream}:p681:{eventIndex}"), k);

        private P681State Of(bool good) => good ? Set.Good : Set.Bad;

        /// <summary>An event's length (m): its lognormal truncated at durmin, length-biased (µ + σ²) for the event a drop starts in.</summary>
        private double Length(bool good, double u, bool biased)
        {
            var s = Of(good);
            double mu = s.Mu + (biased ? s.Sigma * s.Sigma : 0);
            double lo = Normal.Cdf((Math.Log(s.DurMin) - mu) / s.Sigma);
            double q = lo + u * (1 - lo);
            return Math.Max(s.DurMin, Math.Exp(mu + s.Sigma * Normal.InverseCdf(Math.Min(q, 1 - 1e-16))));
        }

        private LooTriplet Triplet(bool good, double u)
        {
            var s = Of(good);
            double ma = s.MuMa;
            if (s.SigmaMa > 0)
            {
                var (lo, hi) = ItuP681.MaRange(Set, good);
                double a = Normal.Cdf((lo - s.MuMa) / s.SigmaMa), b = Normal.Cdf((hi - s.MuMa) / s.SigmaMa);
                ma = s.MuMa + s.SigmaMa * Normal.InverseCdf(a + u * (b - a));
            }
            return new LooTriplet(ma, Math.Abs(s.G1 * ma + s.G2), s.H1 * ma + s.H2);
        }

        private double Transition(LooTriplet a, LooTriplet b) => Math.Max(0, Set.F1 * Math.Abs(a.Ma - b.Ma) + Set.F2);

        private void Start()
        {
            double g = ItuP681.MeanDuration(Set.Good), b = ItuP681.MeanDuration(Set.Bad), t = Math.Max(0, ItuP681.MeanTransition(Set));
            double pick = U(-2, 0) * (g + b + 2 * t), offset = U(-2, 1);
            if (pick < g + b)
            {
                bool good = pick < g;
                double len = Length(good, U(0, 0), biased: true);
                var tri = Triplet(good, U(0, 1));
                double x0 = -offset * len;
                _segments.Add(new Segment(x0, x0 + len, good, tri, tri, good));
                _last = tri; _lastGood = good; _nextGood = !good; _next = 1;
                return;
            }
            // in a transition: GOOD → BAD or BAD → GOOD, equally often
            bool leavingGood = U(-2, 2) < 0.5;
            var from = Triplet(leavingGood, U(-1, 1));
            var to = Triplet(!leavingGood, U(0, 1));
            double lt = Transition(from, to), xs = -offset * lt;
            _segments.Add(new Segment(xs, xs + lt, null, from, to, leavingGood));
            double el = Length(!leavingGood, U(0, 0), biased: false);
            _segments.Add(new Segment(xs + lt, xs + lt + el, !leavingGood, to, to, !leavingGood));
            _last = to; _lastGood = !leavingGood; _nextGood = leavingGood; _next = 1;
        }

        private void Extend()
        {
            var tri = Triplet(_nextGood, U(_next, 1));
            double len = Length(_nextGood, U(_next, 0), biased: false);
            double x = _segments[^1].EndM, lt = Transition(_last, tri);
            if (lt > 0) _segments.Add(new Segment(x, x + lt, null, _last, tri, _lastGood));
            _segments.Add(new Segment(x + lt, x + lt + len, _nextGood, tri, tri, _nextGood));
            _last = tri; _lastGood = _nextGood; _nextGood = !_nextGood; _next++;
        }

        /// <summary>The segment holding the point <paramref name="xM"/> (m from the drop's start; before the first one, the first).</summary>
        public Segment At(double xM)
        {
            lock (_segments)
            {
                while (_segments[^1].EndM <= xM) Extend();
                if (xM < _segments[0].StartM) return _segments[0];
                int lo = 0, hi = _segments.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_segments[mid].EndM <= xM) lo = mid + 1; else hi = mid;
                }
                return _segments[lo];
            }
        }

        /// <summary>The segments covering [0, <paramref name="lengthM"/>].</summary>
        public IReadOnlyList<Segment> Over(double lengthM)
        {
            At(lengthM);
            lock (_segments) return [.. _segments.Where(s => s.EndM > 0 && s.StartM <= lengthM)];
        }

        /// <summary>The state at <paramref name="xM"/>: GOOD or BAD, a transition the state it leaves over its first half.</summary>
        public bool GoodAt(double xM)
        {
            var s = At(xM);
            if (s.Good is bool g) return g;
            return xM < 0.5 * (s.StartM + s.EndM) ? s.FromGood : !s.FromGood;
        }

        /// <summary>The Loo triplet at <paramref name="xM"/> (linear in dB across a transition).</summary>
        public LooTriplet TripletAt(double xM)
        {
            var s = At(xM);
            if (s.Good is not null || s.EndM <= s.StartM) return s.From;
            return LooTriplet.Lerp(s.From, s.To, Math.Clamp((xM - s.StartM) / (s.EndM - s.StartM), 0, 1));
        }

        /// <summary>The direct signal's level at <paramref name="xM"/>, dB: MA + ΣA·u(x), u the drop's unit correlated shadowing.</summary>
        public double DirectDb(double xM)
        {
            var tri = TripletAt(xM);
            if (tri.SigmaA == 0) return tri.Ma;
            var k = GoodAt(xM) ? _kGood : _kBad;
            double u = 0;
            for (int n = 0; n < Sinusoids; n++) u += Math.Cos(k[n] * xM + _phi[n]);
            return tri.Ma + tri.SigmaA * u * Math.Sqrt(2.0 / Sinusoids);
        }

        /// <summary>The fraction of [0, <paramref name="lengthM"/>] in the GOOD state.</summary>
        public double GoodFraction(double lengthM)
        {
            if (!(lengthM > 0)) return GoodAt(0) ? 1 : 0;
            double good = 0;
            foreach (var s in Over(lengthM))
            {
                double a = Math.Max(0, s.StartM), b = Math.Min(lengthM, s.EndM);
                if (b <= a) continue;
                if (s.Good is bool g) { if (g) good += b - a; continue; }
                double mid = 0.5 * (s.StartM + s.EndM);
                double first = Math.Max(0, Math.Min(b, mid) - a), second = Math.Max(0, b - Math.Max(a, mid));
                good += s.FromGood ? first : second;
            }
            return good / lengthM;
        }
    }

    /// <summary>
    /// A ray's gain under P.681's states (R-pch10-3): at channel time t the terminal is v·t along its route, and the gain is
    /// 10^(A/20) + 10^(MP/20)·w(t), A the direct signal's level and w unit Rayleigh multipath with the classical spectrum at f_m = v·f/c.
    /// </summary>
    public sealed class P681Gain
    {
        public P681Series Series { get; }
        public double SpeedMps { get; }
        public double FmHz { get; }
        private readonly FadingProcess _multipath;

        public P681Gain(P681Set set, double fHz, double speedMps, ulong seed, long drop, string stream, int sinusoids)
        {
            Series = new P681Series(set, seed, drop, stream);
            SpeedMps = speedMps;
            FmHz = speedMps * fHz / PropagationMath.C;
            _multipath = new FadingProcess(new TapFading(FadingKind.Rayleigh, DopplerShape.Classical, FmHz), seed, drop, stream + ":p681:multipath", sinusoids);
        }

        public double PositionM(double t) => SpeedMps * t;

        public Complex Gain(double t)
        {
            double x = PositionM(t);
            var tri = Series.TripletAt(x);
            return Math.Pow(10, Series.DirectDb(x) / 20) + Math.Pow(10, tri.Mp / 20) * _multipath.Gain(t);
        }

        /// <summary>A FadingProcess carrying this gain, described for the regime by a Rician K of the state's mean direct over multipath power.</summary>
        public FadingProcess AsFading(int sinusoids)
        {
            var tri = Series.TripletAt(0);
            double k = Math.Pow(10, (tri.Ma - tri.Mp) / 10);
            // the direct signal 10^(A/20) is real: its line sits at 0 Hz, not at 0.7 f_m
            return new FadingProcess(new TapFading(FadingKind.Rician, DopplerShape.Classical, FmHz, k, LosDopplerFactor: 0), Gain, sinusoids);
        }
    }
}
