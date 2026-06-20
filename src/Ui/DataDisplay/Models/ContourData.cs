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
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    public enum ContourFillType { None, Lines, TopoMap, HeatMap }
    public enum ContourLevelMode { Range, Count }
    public enum ContourFillKind  { TopoMap, HeatMap }

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
        public SurfaceGrid?      Grid    { get; set; }

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

        public ContourLevelMode LevelMode  { get; set; } = ContourLevelMode.Range;
        public double           LevelStart { get; set; } = -30.0;
        public double           LevelStep  { get; set; } = 0.5;
        public double           LevelStop  { get; set; } = 60.0;
        public int              LevelCount { get; set; } = 10;

        // ---- Display toggles -----------------------------------------------

        public bool ShowIsoLines { get; set; } = true;

        /// <summary>Whether to render the fill layer. Default is plane-dependent (see ContourDefaults).</summary>
        public bool ShowFill     { get; set; }

        public bool DrawLabels   { get; set; } = true;

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

        public SKColor LineColor   { get; set; } = new SKColor(255, 255, 255, 220);
        public float   StrokeWidth { get; set; } = 1.5f;

        // ---- Label styling (state now; richer render deferred) -------------
        // DEFERRED (AppSettings): defaults come from ContourDefaults; SettingsView override is a future pass.
        public SKColor LabelBackground { get; set; } = new SKColor(0, 0, 0, 140);
        public double  LabelSpacing    { get; set; } = 1.0;

        // ---- Colormap (picker + persist now; render mapping deferred) ------
        // DEFERRED: renderer keeps current blue→red palette until colormap ramps are implemented.
        public ContourColorMap ColorMap { get; set; } = ContourColorMap.Hot;

        // ---- Polyline cache ------------------------------------------------

        private IReadOnlyList<IsoPolyline>? _cachedPolylines;
        private SurfaceGrid?                _cacheGrid;
        private ContourLevelSet?            _cacheLevels;

        /// <summary>Returns cached iso-polylines, re-extracting when Grid or Levels changes.
        /// Returns null when Grid is not set or Levels is empty.</summary>
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
