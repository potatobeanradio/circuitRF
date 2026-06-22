// ================================================================
//  Marker.cs  —  Frequency marker value object
//
//  Ported from splotRF/src/Models/Marker.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.  ComplexStringHelper.Format (splotRF
//  ViewModel type not ported) is inlined as FormatRI().
// ================================================================

using System;
using System.Numerics;
using RfCore;

namespace CircuitRF.Ui.DataDisplay
{
    // ============================================================
    //  MarkerStyle
    // ============================================================

    public enum MarkerStyle { Small, Medium, Large, XLarge }

    public enum MarkerKind { Polyline, Spectrum, StabilityCircle, Table, Contour }

    public static class MarkerStyleExtensions
    {
        public static string Description(this MarkerStyle s) => s switch
        {
            MarkerStyle.Small  => "Small",
            MarkerStyle.Medium => "Medium",
            MarkerStyle.Large  => "Large",
            MarkerStyle.XLarge => "X-Large",
            _                  => s.ToString()
        };

        public static string ShortDescription(this MarkerStyle s) => s switch
        {
            MarkerStyle.Small  => "S",
            MarkerStyle.Medium => "M",
            MarkerStyle.Large  => "L",
            MarkerStyle.XLarge => "XL",
            _                  => s.ToString()
        };

        /// <summary>Multiplier applied to the base font-size in the renderer.</summary>
        public static double FontScale(this MarkerStyle s) => s switch
        {
            MarkerStyle.Small  => 0.5,
            MarkerStyle.Medium => 1.0,
            MarkerStyle.Large  => 1.5,
            MarkerStyle.XLarge => 2.0,
            _                  => 1.0
        };
    }

    // ============================================================
    //  Marker
    // ============================================================

    public class Marker
    {
        // ---- Identity ---------------------------------------------------

        public Guid Id { get; set; } = Guid.NewGuid();

        // ---- Core state -------------------------------------------------

        private string _name = "m1";
        public string Name
        {
            get => _name;
            set => _name = string.IsNullOrEmpty(value) ? $"m{Index}" : value;
        }

        public int            Index                  { get; set; }
        public double         Freq                   { get; set; }
        public FreqUnit       FreqUnits              { get; set; } = FreqUnit.GHz;
        public MatrixFormat   MatrixFormat           { get; set; } = MatrixFormat.MA;
        public MatrixFormat   MatrixFormatImpedance  { get; set; } = MatrixFormat.RI;
        public bool           UseNormalizedImpedance { get; set; } = true;
        public bool           IsMulti                { get; set; }
        public bool           IsDelta                { get; set; }
        public MarkerStyle    Style                  { get; set; } = MarkerStyle.Medium;
        public int            MaximumFractionDigits  { get; set; } = 4;
        public PrecisionFormat FormatString          { get; set; } = PrecisionFormat.G;
        public MarkerKind     MarkerKind             { get; set; } = MarkerKind.Polyline;
        public bool           ShowInfoBox            { get; set; } = true;
        public bool           ContourSnapped         { get; set; }
        public bool           VswrEnabled            { get; set; }
        public double         VswrValue              { get; set; } = 2.0;

        /// <summary>
        /// Info-box position in screen pixels relative to the PlotContainerView top-left.
        /// NaN means "not placed yet".
        /// </summary>
        public Avalonia.Point InfoBoxPos { get; set; } = new(double.NaN, double.NaN);

        /// <summary>
        /// For stability-circle markers: the snapped perimeter point
        /// in world coordinates.
        /// </summary>
        public System.Numerics.Vector2 PositionStatic { get; set; }

        // ---- Display strings (no Trace access needed) -------------------

        public string MarkerString => Name;

        public string FreqString =>
            $"freq={(Freq * FreqUnits.Scale()).ToString($"G{MaximumFractionDigits}")} {FreqUnits.Description()}";

        // ---- Constructors -----------------------------------------------

        /// <summary>
        /// Creates a marker pinned to <paramref name="freq"/> on the given trace.
        /// The trace is used only for initialising MatrixFormat — it is not stored.
        /// </summary>
        public Marker(
            Trace   trace,
            double  freq,
            bool    isMulti,
            bool    isDelta,
            int     index,
            FreqUnit freqUnit = FreqUnit.GHz)
        {
            Index     = index;
            Name      = $"m{index}";
            Freq      = freq;
            IsMulti   = isMulti;
            IsDelta   = isDelta;
            FreqUnits = freqUnit;
            MatrixFormat = trace.IsStabilityCircle ? MatrixFormat.MA
                : trace.YAxis == DependentVarFormat.Db ? MatrixFormat.DB
                : MatrixFormat.MA;
        }

        /// <summary>Copy constructor.</summary>
        public Marker(Marker src)
        {
            Name                   = src.Name;
            Index                  = src.Index;
            Freq                   = src.Freq;
            FreqUnits              = src.FreqUnits;
            MatrixFormat           = src.MatrixFormat;
            MatrixFormatImpedance  = src.MatrixFormatImpedance;
            IsMulti                = src.IsMulti;
            IsDelta                = src.IsDelta;
            InfoBoxPos             = src.InfoBoxPos;
            PositionStatic         = src.PositionStatic;
            UseNormalizedImpedance = src.UseNormalizedImpedance;
            FormatString           = src.FormatString;
            Style                  = src.Style;
            MaximumFractionDigits  = src.MaximumFractionDigits;
            MarkerKind             = src.MarkerKind;
            ShowInfoBox            = src.ShowInfoBox;
            ContourSnapped         = src.ContourSnapped;
            VswrEnabled            = src.VswrEnabled;
            VswrValue              = src.VswrValue;
        }

        // ---- Interaction ------------------------------------------------

        /// <summary>Moves the info-box by a pixel delta in container coordinates.</summary>
        public void TranslateInfoBox(double dx, double dy) =>
            InfoBoxPos = new Avalonia.Point(InfoBoxPos.X + dx, InfoBoxPos.Y + dy);

        // ---- Format helper (pure, no Trace access) ----------------------

        public string FormatComplex(Complex c)
        {
            string fmt = $"{FormatString.ToString()}{MaximumFractionDigits}";
            return MatrixFormat switch
            {
                MatrixFormat.MA =>
                    $"{c.Magnitude.ToString(fmt)}∠{(c.Phase * 180 / Math.PI).ToString(fmt)}°",
                MatrixFormat.DB =>
                    $"{(20 * Math.Log10(c.Magnitude + 1e-300)).ToString(fmt)} dB ∠{(c.Phase * 180 / Math.PI).ToString(fmt)}°",
                _ => FormatRI(c, fmt)
            };
        }

        // Inlined from splotRF.ViewModels.ComplexStringHelper.Format
        private static string FormatRI(Complex c, string fmt)
        {
            string sign = c.Imaginary >= 0 ? "+" : "";
            return $"{c.Real.ToString(fmt)}{sign}{c.Imaginary.ToString(fmt)}j";
        }

        // ---- Equality ---------------------------------------------------

        public override bool Equals(object? obj) => obj is Marker m && m.Id == Id;
        public override int  GetHashCode()        => Id.GetHashCode();
    }
}
