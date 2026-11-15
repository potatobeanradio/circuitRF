// brief-cosim-pattern-5 R-cp5-5 — a pattern cut over a sweep: the same cut at each value, on one plot, for comparing them. An ordinary family reads as
// one trace drawn N times, in one colour with no legend, which is right for a curve tracer whose curves are in order and wrong for curves that cross. So
// this one family has a colour per curve and a legend row per curve, and every other family draws as it did.

using System;
using System.Globalization;
using CircuitRF.Core.Expressions;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public partial class Trace
    {
        /// <summary>A family along an angle whose curves are values of something else. A family over φ is the whole pattern, one quantity, and trials are drawn as a cloud.
        /// brief-paa-6 R-paa6-4 — and a family over an array's elements, whatever runs along it: each curve is one amplifier's locus (its active reflection against
        /// a swept steer, on a Smith chart or over a loadpull contour), and the loci cross, so each is told apart by colour and named by its element. And
        /// (brief-paa-6b) a family over the named directions of a radiated envelope, whose spectra and metrics are compared direction against direction.</summary>
        public bool LabelsFamilyCurves =>
            IsFamily && FamilyCurves.Count > 0 && FamilyAxisName is { } family && family != Evaluator.TrialAxis
            && (family is ElementAxis or DirectionAxis
                || _cubeXAxisName is { } x && PolarPatternAngle.TryDegreesPerUnit(x, _cubeXUnit, out _)
                   && !IsPatternAngle(family));

        /// <summary>One of a pattern's own angles, over which a family is the pattern itself. A swept angle that is not one, a steering angle theta0, is
        /// something else's value: brief-paa-6's cuts at each scan angle are told apart as any sweep's are.</summary>
        private static bool IsPatternAngle(string family) => family is "theta" or "phi" or "cut" or "az" or "el";

        /// <summary>The axis an array's per-element cubes run over (a fields analysis's <c>array</c> group): element 1 to N.</summary>
        public const string ElementAxis = "element";

        /// <summary>brief-paa-6b — the axis a fields analysis's radiated envelopes run over: one named direction each, whose spectra and metrics cross.</summary>
        public const string DirectionAxis = "direction";

        /// <summary>The curve's colour: the trace's own first, then the colour wheel on from it, so a back branch authored from the same colour matches curve for curve.</summary>
        public SKColor FamilyCurveColor(int curve)
        {
            if (curve <= 0) return Properties.LineColor;
            int[] wheel = TraceProperties.LineColorOrder;
            int at = Math.Max(0, Array.IndexOf(wheel, Properties.LineColorIndex));
            return TraceProperties.ColorLUT[wheel[(at + curve) % wheel.Length]];
        }

        /// <summary>The curve's value and unit, as <c>L1 = 5 nH</c>.</summary>
        public string FamilyCurveLabel(int curve)
        {
            var fc = FamilyCurves[curve];
            if (fc.AxisLabel is { } label) return $"{FamilyAxisName} = {label}";
            // a swept angle reads in degrees, as the pattern's own do, whatever base unit the sweep stored it in
            if (FamilyAxisName is { } name && PolarPatternAngle.TryDegreesPerUnit(name, FamilyAxisUnit, out double perUnit))
                return $"{name} = {(fc.AxisValue * perUnit).ToString("0.###", CultureInfo.InvariantCulture)}°";
            int group = EngineeringFormat.EngineeringGroup(Math.Abs(fc.AxisValue));
            string mantissa = (fc.AxisValue / Math.Pow(10.0, group)).ToString("G4", CultureInfo.InvariantCulture);
            return $"{FamilyAxisName} = {mantissa} {EngineeringFormat.Prefix(group)}{FamilyAxisUnit}".TrimEnd();
        }
    }
}
