// ================================================================
//  PathLoss.cs  —  the contract every large-scale propagation model meets
//
//  A model is a pure function from a frequency, a geometry and its own
//  parameters to a PathLossResult: the loss it gives (dB), that loss
//  broken down by term, the shadowing standard deviation, and — where the
//  model states them — the line-of-sight probability, the Rician K and
//  the statistics of the delay and angle spreads.
//
//  Every model names its document and edition, and REFUSES outside its
//  validity range with a sentence naming the range and the value
//  (PropagationRefusal). Nothing extrapolates silently: a refusal is the
//  only answer a model gives where its source document gives none.
//
//  Units at this boundary are SI: frequency in Hz, distances and heights
//  in metres, angles in degrees, percentages in %. Each model converts to
//  the units its document writes its equations in, internally.
//
//  docs/design/propagation-channel.md §3; brief PCH-1.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Diagnostics;

namespace RfCore.Propagation
{
    /// <summary>A model asked for a value outside the range its document states, or for something its document does not
    /// give. The message names the range and the value; nothing is extrapolated. It carries a coded
    /// <see cref="CircuitRF.Diagnostics.Diagnostic"/> so a caller posting it (the Chain Budget, a sweep of the
    /// Propagation Channel) can group a refusal repeated at every point into one line.</summary>
    public sealed class PropagationRefusal : Exception
    {
        public Diagnostic Diagnostic { get; }

        public PropagationRefusal(Diagnostic diagnostic) : base(diagnostic.Render()) { Diagnostic = diagnostic; }

        /// <summary>A refusal authored as a sentence, carried under the family's general id with the sentence as its
        /// argument (the range and positivity checks have their own ids, <see cref="PropagationDiagnostics"/>).</summary>
        public PropagationRefusal(string message)
            : this(Diagnostic.Create("propagation.refused", DiagnosticSeverity.Error, "{message}", ("message", message))) { }
    }

    /// <summary>The propagation models' coded refusals.</summary>
    public static class PropagationDiagnostics
    {
        /// <summary>A quantity outside the range the model's document states.</summary>
        public static Diagnostic OutOfRange(string model, string quantity, double value, double min, double max, string unit)
            => Diagnostic.Create("propagation.out-of-range", DiagnosticSeverity.Error,
                double.IsPositiveInfinity(max)
                    ? "{model}: {quantity} must be at least {minText}{unitText}; it is {valueText}{unitText}."
                    : "{model}: {quantity} must be between {minText} and {maxText}{unitText}; it is {valueText}{unitText}.",
                ("model", model), ("quantity", quantity), ("value", value), ("min", min), ("max", max), ("unit", unit),
                ("valueText", PropagationMath.F(value)), ("minText", PropagationMath.F(min)), ("maxText", PropagationMath.F(max)),
                ("unitText", unit.Length > 0 ? " " + unit : ""));

        /// <summary>A quantity that must be greater than zero and is not.</summary>
        public static Diagnostic NotPositive(string model, string quantity, double value, string unit)
            => Diagnostic.Create("propagation.not-positive", DiagnosticSeverity.Error,
                "{model}: {quantity} must be greater than 0; it is {valueText}{unitText}.",
                ("model", model), ("quantity", quantity), ("value", value), ("unit", unit),
                ("valueText", PropagationMath.F(value)), ("unitText", unit.Length > 0 ? " " + unit : ""));
    }

    /// <summary>One line of a loss breakdown: what it is and its value in dB.</summary>
    public sealed record LossTerm(string Name, double Db);

    /// <summary>The mean and standard deviation of a quantity's statistics as the source states them: of log10 for the
    /// spreads (log10(s) for a delay spread, log10(deg) for an angle spread), in dB for the K-factor.</summary>
    public readonly record struct Statistic(double Mean, double Std)
    {
        /// <summary>The median of a log10-normal quantity, in its linear unit: 10^Mean.</summary>
        public double Median => Math.Pow(10, Mean);
    }

    /// <summary>The large-scale parameter statistics a scenario model gives: delay spread (lgDS, log10 of seconds), the
    /// azimuth and zenith spreads of departure and arrival (log10 of degrees), and for line of sight the Rician K
    /// (dB).</summary>
    public sealed record SpreadStatistics(Statistic DelaySpread, Statistic AzimuthDeparture, Statistic AzimuthArrival,
                                          Statistic ZenithArrival, Statistic ZenithDeparture, Statistic? KFactorDb);

    /// <summary>What a model gives at one frequency and geometry.</summary>
    /// <param name="Model">The registry name (<c>38901</c>, <c>P1238</c> …).</param>
    /// <param name="Document">The document and edition the numbers come from.</param>
    /// <param name="LossDb">The loss the model gives, dB: its median, or its value at a stated percentage where the
    /// model takes one (a location or time percentage).</param>
    /// <param name="Terms">The breakdown; the terms sum to <paramref name="LossDb"/>.</param>
    /// <param name="SigmaDb">The shadowing (location variability) standard deviation, dB; 0 where the model states
    /// none.</param>
    /// <param name="LosProbability">The probability of line of sight, where the model gives one.</param>
    /// <param name="Spreads">The large-scale parameter statistics, where the model gives them.</param>
    /// <param name="Notes">What a reader of the number should know: a choice the model made, a default it used.</param>
    public sealed record PathLossResult(string Model, string Document, double LossDb, IReadOnlyList<LossTerm> Terms,
                                        double SigmaDb, double? LosProbability = null, SpreadStatistics? Spreads = null,
                                        IReadOnlyList<string>? Notes = null)
    {
        public IReadOnlyList<string> NoteList => Notes ?? Array.Empty<string>();

        /// <summary>A satellite path's geometry (slant range, latency, range rate, Doppler); null for a terrestrial path.
        /// The added losses take the Earth-space methods when it is set.</summary>
        public SlantGeometry? Geometry { get; init; }

        /// <summary>A single-term result.</summary>
        public static PathLossResult Single(string model, string document, string term, double db, double sigma = 0,
                                            double? pLos = null, SpreadStatistics? spreads = null,
                                            IReadOnlyList<string>? notes = null)
            => new(model, document, db, new[] { new LossTerm(term, db) }, sigma, pLos, spreads, notes);

        /// <summary>This result with further terms added to it (added losses), the total moved by their sum.</summary>
        public PathLossResult With(IEnumerable<LossTerm> more, IEnumerable<string>? notes = null)
        {
            var add = more.ToList();
            if (add.Count == 0 && notes is null) return this;
            return this with
            {
                LossDb = LossDb + add.Sum(t => t.Db),
                Terms = Terms.Concat(add).ToList(),
                Notes = NoteList.Concat(notes ?? Array.Empty<string>()).ToList(),
            };
        }
    }

    /// <summary>Speed of light and the conversions every model shares.</summary>
    public static class PropagationMath
    {
        /// <summary>The speed of light in vacuum, m/s (exact).</summary>
        public const double C = 299_792_458.0;

        /// <summary>Free-space basic transmission loss between isotropic antennas, dB: 20·log10(4πd/λ).</summary>
        public static double FreeSpaceDb(double frequencyHz, double distanceM)
            => 20 * Math.Log10(4 * Math.PI * distanceM * frequencyHz / C);

        /// <summary>Formats a number for a refusal or a note: up to six significant digits, invariant culture.</summary>
        public static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>Formats a frequency in GHz for a refusal.</summary>
        public static string Ghz(double hz) => F(hz / 1e9) + " GHz";

        /// <summary>Refuses unless lo ≤ v ≤ hi.</summary>
        public static void Range(string model, string what, double v, double lo, double hi, string unit)
        {
            if (double.IsNaN(v) || v < lo || v > hi)
                throw new PropagationRefusal(PropagationDiagnostics.OutOfRange(model, what, v, lo, hi, unit));
        }

        /// <summary>Refuses unless v &gt; 0.</summary>
        public static void Positive(string model, string what, double v, string unit)
        {
            if (!(v > 0)) throw new PropagationRefusal(PropagationDiagnostics.NotPositive(model, what, v, unit));
        }
    }
}
