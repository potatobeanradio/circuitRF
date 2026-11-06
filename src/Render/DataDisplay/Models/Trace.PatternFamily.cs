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
        /// <summary>A family along an angle whose curves are values of something else. A family over φ is the whole pattern, one quantity, and trials are drawn as a cloud.</summary>
        public bool LabelsFamilyCurves =>
            IsFamily && FamilyCurves.Count > 0 && FamilyAxisName is { } family && family != Evaluator.TrialAxis
            && _cubeXAxisName is { } x && PolarPatternAngle.TryDegreesPerUnit(x, _cubeXUnit, out _)
            && !PolarPatternAngle.TryDegreesPerUnit(family, FamilyAxisUnit, out _);

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
            int group = EngineeringFormat.EngineeringGroup(Math.Abs(fc.AxisValue));
            string mantissa = (fc.AxisValue / Math.Pow(10.0, group)).ToString("G4", CultureInfo.InvariantCulture);
            return $"{FamilyAxisName} = {mantissa} {EngineeringFormat.Prefix(group)}{FamilyAxisUnit}".TrimEnd();
        }
    }
}
