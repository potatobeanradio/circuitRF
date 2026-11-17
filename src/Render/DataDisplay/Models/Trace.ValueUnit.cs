// ================================================================
//  Trace.ValueUnit.cs  —  the unit of the value a trace plots, where it is known
//  (brief-run-display-hb-preset-and-units R-rdh-4)
//
//  A marker readout printed a bare number: on a spectrum of dB(fft(TRAN1.V[:, 1], 50)) the DC stem read 19.67, which is
//  19.67 dBV, 9.63 V — and beside a waveform in volts, a bare 19.67 reads as twice the waveform. Every marker row that
//  prints a value appends this unit. It is said only when it is KNOWN; where it is not, nothing is appended, because a
//  guessed unit is worse than none.
// ================================================================

using System;
using System.Collections.Generic;
using CircuitRF.Core.Expressions;
using RfCore;

namespace CircuitRF.Render.DataDisplay
{
    public partial class Trace
    {
        private string? _valueUnitKey;
        private string? _valueUnit;

        /// <summary>
        /// The unit of the value this trace plots, or null when it is not known. Known means: the cube's own
        /// <see cref="CubeValueUnit"/>; a dB function or transform of a quantity whose unit is known (<c>dBm(…)</c> is dBm;
        /// <c>dB</c>/<c>dB20</c> of a voltage dBV, of a current dBA, of a ratio — an S-parameter — dB; <c>dB10</c> of a
        /// power dBW); or the quantity itself — a transient or harmonic-balance <c>V</c> is V, <c>I</c> is A, a power
        /// (<c>P</c>, <c>Pt</c>, <c>Pprobe_W</c>) is W. The one place the marker rows read it.
        /// </summary>
        public string? ValueUnit
        {
            get
            {
                string key = $"{IsCubeBound}|{Expression}|{CubeName}|{DisplayTransform}|{CubeValueUnit}|{MatrixType}|{Derived}";
                if (key != _valueUnitKey)
                {
                    _valueUnitKey = key;
                    _valueUnit = InferValueUnit();
                }
                return _valueUnit;
            }
        }

        /// <summary><paramref name="readout"/> with this trace's <see cref="ValueUnit"/> after it, when there is one and the
        /// readout is a number (not NaN, not empty).</summary>
        internal string WithValueUnit(string readout)
            => ValueUnit is { Length: > 0 } u && readout.Length > 0 && readout != "NaN" ? $"{readout} {u}" : readout;

        private string? InferValueUnit()
        {
            if (!string.IsNullOrEmpty(CubeValueUnit)) return CubeValueUnit;

            if (!string.IsNullOrWhiteSpace(Expression))
            {
                try { return UnitOfExpression(Parser.Parse(Expression)); }
                catch (ExpressionException) { return null; }
            }

            if (IsCubeBound && CubeName is { Length: > 0 } cube)
                return WithTransform(QuantityUnit(cube), DisplayTransform);

            // A network (Touchstone) trace: an S-parameter is a ratio, so its dB is plain dB; a Z or Y matrix in dB is not
            // a unit anyone writes, and its magnitude's unit depends on the matrix — left unsaid.
            return MatrixType == MatrixType.S && Derived is DerivedParameters.None or DerivedParameters.MaxGain
                   && DisplayTransform is CubeTransform.dB20 or CubeTransform.dB10 or CubeTransform.dB
                ? "dB" : null;
        }

        /// <summary>The unit a trace expression's VALUE carries: its outermost function's, read through the functions that
        /// keep their operand's unit. Anything else — arithmetic, a comparison, an unknown function — is not known.</summary>
        private static string? UnitOfExpression(Expr e)
        {
            switch (e)
            {
                case CallExpr { Name: "dBm" }:
                    return "dBm";
                case CallExpr { Name: "dB" or "dB20", Args.Length: >= 1 } c:
                    return WithTransform(UnitOfQuantity(c.Args[0]), CubeTransform.dB20);
                case CallExpr { Name: "dB10", Args.Length: >= 1 } c:
                    return WithTransform(UnitOfQuantity(c.Args[0]), CubeTransform.dB10);
                default:
                    return UnitOfQuantity(e);
            }
        }

        /// <summary>The functions whose result is in their first argument's unit.</summary>
        private static readonly HashSet<string> UnitPreserving = new(StringComparer.Ordinal)
        {
            "fft", "waveform", "avg", "rms", "value_at", "at", "mag", "abs", "real", "conj",
            "max_over", "min_over", "mean_over", "median_over",
        };

        /// <summary>The unit of a quantity written in an expression: a result reference (<c>TR1.V</c>, <c>HB1.V("out")</c>,
        /// <c>TR1.V[:, 1]</c>) by its cube's name, read through the unit-preserving functions.</summary>
        private static string? UnitOfQuantity(Expr e)
        {
            switch (e)
            {
                case RefExpr r:
                    return QuantityUnit(r.Name);
                case IndexExpr ix:
                    return UnitOfQuantity(ix.Target);
                case CallExpr { Name: "fft_power" }:
                    return "W";
                case CallExpr c when UnitPreserving.Contains(c.Name) && c.Args.Length >= 1:
                    return UnitOfQuantity(c.Args[0]);
                // a qualified call-style reference, HB1.V("out"), names the cube
                case CallExpr c when c.Name.Contains('.'):
                    return QuantityUnit(c.Name);
                default:
                    return null;
            }
        }

        /// <summary>The unit of a result cube by its name (the part after the analysis prefix). "1" marks a ratio: an
        /// S-parameter has no unit, but its dB is plain dB.</summary>
        private static string? QuantityUnit(string cubeName)
        {
            int dot = cubeName.LastIndexOf('.');
            string q = dot >= 0 ? cubeName[(dot + 1)..] : cubeName;
            return q switch
            {
                "V" or "Vprobe" => "V",
                "I" or "INl" => "A",
                "P" or "Pt" or "Pprobe_W" or "Pprobe_fund_W" => "W",
                "S" => "1",
                _ => null,
            };
        }

        /// <summary>The unit after <paramref name="transform"/>: a dB of a voltage or current is dBV/dBA (20·log10), of a
        /// power dBW (10·log10), of a ratio dB. A magnitude, real part or the value itself keeps the unit; a phase, an
        /// imaginary part's sign aside, does not carry it, and is left unsaid.</summary>
        private static string? WithTransform(string? unit, CubeTransform transform)
        {
            if (unit is null) return null;
            return transform switch
            {
                CubeTransform.dB20 or CubeTransform.dB => unit switch { "V" => "dBV", "A" => "dBA", "1" => "dB", _ => null },
                CubeTransform.dB10 => unit switch { "W" => "dBW", "1" => "dB", _ => null },
                CubeTransform.None or CubeTransform.Mag or CubeTransform.Real or CubeTransform.Imag or CubeTransform.Conj
                    => unit == "1" ? null : unit,
                _ => null,
            };
        }
    }
}
