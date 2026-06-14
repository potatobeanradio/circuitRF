// ================================================================
//  AppSettings.cs  —  User preferences (in-memory defaults for circuitRF)
//
//  Ported from splotRF/src/Models/AppSettings.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.  Disk persistence deferred to 7.x: the
//  Load() method returns in-memory defaults rather than reading
//  splotRF's config path, and Save() is a no-op until wired.
//
//  TODO 7.x: wire AppSettings persistence to circuitRF's app-data dir.
// ================================================================

namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>Which color theme to use when exporting or copying plots.</summary>
    public enum ExportThemeMode
    {
        UseSystemTheme,   // Follow the OS light/dark appearance
        ForceLightTheme,  // Always export with the light theme
        ForceDarkTheme    // Always export with the dark theme
    }

    /// <summary>
    /// Plain-object settings model.  All properties have sensible defaults.
    /// Disk persistence is deferred to 7.x.
    /// </summary>
    public class AppSettings
    {
        // ── Export & Copy ────────────────────────────────────────────────────

        /// <summary>Color theme override for exported / copied plots.</summary>
        public ExportThemeMode ExportTheme { get; set; } = ExportThemeMode.UseSystemTheme;

        /// <summary>When true, plots are exported / copied with a transparent background.</summary>
        public bool ExportTransparentBackground { get; set; } = true;

        // ── Display ──────────────────────────────────────────────────────────

        /// <summary>When true, marker info boxes are drawn without a filled background.</summary>
        public bool MarkerBoxTransparentBackground { get; set; } = true;

        /// <summary>
        /// When true, file-name prefix is always shown in marker info boxes and
        /// axis labels, regardless of how many SNPs are loaded.
        /// </summary>
        public bool AlwaysDisplayDataSourcePrefix { get; set; } = false;

        // ── New Marker Defaults ──────────────────────────────────────────────

        /// <summary>Number of significant / decimal digits for newly created markers.</summary>
        public int MarkerMaxFractionDigits { get; set; } = 4;

        /// <summary>Number format for newly created markers (Auto / Fixed / Scientific).</summary>
        public PrecisionFormat MarkerPrecisionFormat { get; set; } = PrecisionFormat.G;

        // ── Rect Plot ────────────────────────────────────────────────────────

        /// <summary>Golden aspect ratio (width / height) φ ≈ 1.618.</summary>
        public static double GoldenAspectRatio = 1.618;

        /// <summary>
        /// Aspect ratio (width / height) applied during Shift+drag resize of Rect plots.
        /// Default is the golden ratio φ ≈ 1.618.
        /// </summary>
        public double RectAspectRatio { get; set; } = GoldenAspectRatio;

        // ── Persistence ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns in-memory defaults.
        /// TODO 7.x: wire AppSettings persistence to circuitRF's app-data dir.
        /// </summary>
        public static AppSettings Load() => new AppSettings();

        /// <summary>
        /// No-op until 7.x wires persistence.
        /// TODO 7.x: wire AppSettings persistence to circuitRF's app-data dir.
        /// </summary>
        public void Save() { }
    }
}
