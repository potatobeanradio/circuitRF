// ================================================================
//  ContourData.cs  —  Per-trace model for loadpull contour rendering
//
//  Carries the SurfaceGrid, level set, fill type, and cached
//  iso-polylines for one contour trace.  The Grid/Scatter/Levels
//  fields are set by the VM (TraceRowViewModel.RebuildContour) from
//  a LoadpullSurface instance; the authoring params below drive that
//  rebuild and are persisted in .cdd via ContourTraceConfig.
//
//  FillType is now a computed getter from ShowFill + SelectedFillKind
//  so the renderer contract is unchanged while the card can toggle
//  ShowFill independently of the selected fill kind.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    public enum ContourFillType      { None, Lines, TopoMap, HeatMap }
    public enum ContourLevelMode     { Range, Count }
    public enum ContourFillKind      { TopoMap, HeatMap }
    public enum ContourFillSelection { None, Topography, Heatmap }

    /// <summary>Matplotlib-style colormap names. Render mapping is deferred — the renderer keeps
    /// the current blue→red palette until a later pass maps these names to ramps.</summary>
    // DEFERRED: renderer ignores ColorMap until colormap ramp mapping is implemented.
    public enum ContourColorMap
    { Gray, Bone, Pink, Spring, Summer, Autumn, Winter, Cool, Wistia, Hot, Afmhot, GistHeat, Copper }

    public sealed class ContourData
    {
        // ---- Data inputs (set externally by LoadpullSurface wiring) --------

        /// <summary>Resampled metric surface (from <c>LoadpullSurface.Resample</c>).
        /// Setting a new reference invalidates the polyline cache.</summary>
        public SurfaceGrid?      Grid     { get; set; }

        /// <summary>Disk-covering fill grid (Gamma plane only): resampled over [-1,1]×[-1,1] at
        /// higher resolution so the TopoMap fill reaches the Smith circular-clip edge.
        /// Null on Rect plots (fill uses <see cref="Grid"/>).</summary>
        public SurfaceGrid?      FillGrid { get; set; }

        /// <summary>Scatter points (from <c>LoadpullSurface.Reduce</c>). Used only for HeatMap.</summary>
        public ScatterReduction? Scatter { get; set; }

        // ---- Level set (set externally from ContourExtractor) --------------

        public ContourLevelSet Levels { get; set; } = new ContourLevelSet(Array.Empty<double>());

        // ---- Authoring params (persisted in .cdd; VM mirrors these) --------

        public string         MetricName            { get; set; } = "Pout";
        public ConstraintKind ContourConstraintKind { get; set; } = ConstraintKind.Compression;
        public string         ConstraintMetricName  { get; set; } = "";
        public double         ConstraintValue       { get; set; } = 3.0;
        public int            FreqIndex             { get; set; } = 0;

        // ---- Level-set authoring -------------------------------------------

        public ContourLevelMode LevelMode  { get; set; } = ContourLevelMode.Count;
        public double           LevelStart { get; set; } = -30.0;
        public double           LevelStep  { get; set; } = 0.5;
        public double           LevelStop  { get; set; } = 60.0;
        public int              LevelCount { get; set; } = 10;

        // ---- Display toggles -----------------------------------------------

        public bool ShowIsoLines { get; set; } = true;

        /// <summary>Whether to render the fill layer. Default is plane-dependent (see ContourDefaults).</summary>
        public bool ShowFill     { get; set; }

        public bool DrawLabels   { get; set; }

        // ---- Fill kind + derived FillType ----------------------------------

        /// <summary>The fill style when ShowFill is true. Remembered across ShowFill toggles.</summary>
        public ContourFillKind SelectedFillKind { get; set; } = ContourFillKind.TopoMap;

        /// <summary>Fill type as read by the renderer — derived from ShowFill and SelectedFillKind.
        /// None when ShowFill is false; TopoMap or HeatMap otherwise.</summary>
        public ContourFillType FillType => ShowFill
            ? (SelectedFillKind == ContourFillKind.TopoMap
                ? ContourFillType.TopoMap
                : ContourFillType.HeatMap)
            : ContourFillType.None;

        // ---- Style ---------------------------------------------------------

        public SKColor LineColor         { get; set; } = new SKColor(255, 255, 255, 220);
        public float   StrokeWidth       { get; set; } = 1.5f;
        public bool    LineColorOverridden { get; set; }

        // ---- Label styling (state now; richer render deferred) -------------
        // DEFERRED (AppSettings): defaults come from ContourDefaults; SettingsView override is a future pass.
        public SKColor LabelBackground { get; set; } = SKColors.White;
        public double  LabelSpacing    { get; set; } = 30.0;

        // ---- Colormap -------------------------------------------------------
        public ContourColorMap ColorMap { get; set; } = ContourColorMap.Bone;

        // ---- Overlay display toggles -------------------------------------
        public bool    DisplayMxp        { get; set; } = true;
        public bool    DisplayMxe        { get; set; } = true;
        public bool    DisplayGridPoints { get; set; }
        public SKColor GridPointColor    { get; set; } = SKColors.Black;
        public SKColor LabelForeground   { get; set; } = SKColors.Black;

        // ---- Size / font params (§5) ------------------------------------
        public double GridPointSize { get; set; } = 3.0;
        public double LevelFontSize { get; set; } = 9.0;

        // ---- Fade-line-opacity toggle (§7) ------------------------------
        public bool FadeLineOpacity { get; set; }

        // ---- Interp engine params -----------------------------------------
        public RbfKernel InterpKernel { get; set; } = RbfKernel.Multiquadric;
        public double    Smoothing    { get; set; } = 1e-3;
        public double?   Epsilon      { get; set; } = null;

        // ---- Cached optima coords (set by VM in RebuildContour; renderer reads) ----

        public Complex? MxpCoord { get; set; }
        public Complex? MxeCoord { get; set; }

        // ---- Polyline cache ------------------------------------------------

        private IReadOnlyList<IsoPolyline>? _cachedPolylines;
        private SurfaceGrid?                _cacheGrid;
        private ContourLevelSet?            _cacheLevels;

        // ---- Title string ---------------------------------------------------

        /// <summary>Builds a human-readable title for this contour trace (used by Plot.Title default).</summary>
        public string TitleString()
        {
            string displayName = MetricDisplayName(MetricName);
            string unit        = MetricUnit(MetricName);

            if (ContourConstraintKind == ConstraintKind.Compression)
            {
                string c = FormatCompression(ConstraintValue);
                return $"P-{c}dB {displayName} ({unit})";
            }
            else
            {
                // §6 defense: if constraint aliases to the same concept as the plotted metric,
                // the title would read "X at Constant X" — fall back to a non-degenerate form.
                if (MetricDisplayName(ConstraintMetricName) == displayName)
                    return $"P-3dB {displayName} ({unit})";

                string otherDisplay = MetricDisplayName(ConstraintMetricName);
                string otherUnit    = MetricUnit(ConstraintMetricName);
                string val          = FormatCompression(ConstraintValue);
                return $"{displayName} ({unit}) at Constant {otherDisplay}={val} {otherUnit}";
            }
        }

        private static string MetricDisplayName(string metric) => metric switch
        {
            "Gain" or "Gt" or "Gp"       => "Gain",
            "DE" or "PAE" or "Efficiency" => "Efficiency",
            _                             => metric,
        };

        private static string MetricUnit(string metric) => metric switch
        {
            "Pout"                        => "dBm",
            "Gain" or "Gt" or "Gp"       => "dB",
            "DE" or "PAE" or "Efficiency" => "%",
            "AMPM"                        => "deg",
            _                             => "",
        };

        private static string FormatCompression(double value)
        {
            string s = value.ToString("G6");
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return s;
        }

        /// <summary>
        /// Deep-copy of all authoring and style fields. Computed/cached state
        /// (Grid, Scatter, Levels, MxpCoord, MxeCoord, polyline cache) is left
        /// null so the pasted trace re-fits on first draw.
        /// </summary>
        public ContourData Clone() => new ContourData
        {
            // Grid, FillGrid, Scatter, Levels, MxpCoord, MxeCoord left null — re-built on first draw.
            MetricName            = MetricName,
            ContourConstraintKind = ContourConstraintKind,
            ConstraintMetricName  = ConstraintMetricName,
            ConstraintValue       = ConstraintValue,
            FreqIndex             = FreqIndex,
            LevelMode             = LevelMode,
            LevelStart            = LevelStart,
            LevelStep             = LevelStep,
            LevelStop             = LevelStop,
            LevelCount            = LevelCount,
            ShowIsoLines          = ShowIsoLines,
            ShowFill              = ShowFill,
            DrawLabels            = DrawLabels,
            SelectedFillKind      = SelectedFillKind,
            LineColor             = LineColor,
            StrokeWidth           = StrokeWidth,
            LineColorOverridden   = LineColorOverridden,
            LabelBackground       = LabelBackground,
            LabelSpacing          = LabelSpacing,
            ColorMap              = ColorMap,
            DisplayMxp            = DisplayMxp,
            DisplayMxe            = DisplayMxe,
            DisplayGridPoints     = DisplayGridPoints,
            GridPointColor        = GridPointColor,
            LabelForeground       = LabelForeground,
            GridPointSize         = GridPointSize,
            LevelFontSize         = LevelFontSize,
            FadeLineOpacity       = FadeLineOpacity,
            InterpKernel          = InterpKernel,
            Smoothing             = Smoothing,
            Epsilon               = Epsilon,
        };

        public IReadOnlyList<IsoPolyline>? GetPolylines()
        {
            var grid = Grid;
            if (grid == null || Levels.Levels.Length == 0) return null;

            if (!ReferenceEquals(grid, _cacheGrid) || !ReferenceEquals(Levels, _cacheLevels))
            {
                _cachedPolylines = ContourExtractor.Extract(grid, Levels);
                _cacheGrid       = grid;
                _cacheLevels     = Levels;
            }
            return _cachedPolylines;
        }
    }

    /// <summary>
    /// Creation-time defaults for contour trace level sets and display settings,
    /// centralised here so a future AppSettings pass can override them in one place.
    /// </summary>
    // DEFERRED (AppSettings): these values will be user-overridable from SettingsView in a future pass.
    public static class ContourDefaults
    {
        /// <summary>Returns the recommended (start, step, stop) level-set for a known metric name.
        /// Returns a generic 0:1:10 range for unrecognised metrics.</summary>
        public static (double Start, double Step, double Stop) LevelRange(string metric) => metric switch
        {
            "Pout"  => (-30.0, 0.5,  60.0),
            "DE"    => (  0.0, 5.0, 100.0),
            "PAE"   => (  0.0, 5.0, 100.0),
            "Gain"  => (-10.0, 0.5,  50.0),
            "Gt"    => (-10.0, 0.5,  50.0),
            "Gp"    => (-10.0, 0.5,  50.0),
            "AMPM"  => (-200.0, 5.0, 200.0),
            _       => (  0.0, 1.0,  10.0),
        };

        /// <summary>ShowFill is OFF by default on Smith/Polar (Γ-plane), ON for Rect (Z-plane).</summary>
        public static bool ShowFillDefault(SurfacePlane plane) => plane == SurfacePlane.Z;
    }
}
