using System;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// R-h45-2 — the Layer-2 token bundle the harmonicaRF renderers draw with, projected from a
/// <see cref="ColorTheme"/>'s <c>Harmonica.*</c> roles. <b>No hardcoded static is the source of
/// truth</b> — <see cref="Light"/> and <see cref="Dark"/> below are themselves derived from
/// <see cref="ColorTheme.BuiltIn"/>, exactly as <see cref="SchematicRenderTheme"/> does, so there is
/// one place a default colour is written down and it is the role table.
///
/// <para><b>Why this exists at all rather than "just read the roles".</b> §7.9.1's load-bearing
/// sentence is "built on the existing theming system, not beside it" — and the way the existing
/// system stays honest is that a RENDERER never touches <see cref="ColorTheme"/>. It consumes a token
/// struct. Retrofitting a theme onto renderers that reach for colours directly is the mistake
/// <c>color-themes.md</c> was written to prevent, which is why this lands in the same phase as the
/// panels rather than after them.</para>
///
/// <para><b>The two numbers that are not colours.</b> §7.9.4 puts the iso-line fade parameters
/// (<see cref="IsoAlphaFloor"/> and the shaping exponent <see cref="IsoAlphaExponent"/>) "with the
/// theme rather than as constants, so a user who dislikes the fade can flatten it (α_floor = 1)
/// without a code change". They cannot live in <see cref="ColorTheme"/>, which is a role→RGBA map by
/// construction, so they live here and persist in the <c>.charm</c> beside the role maps.</para>
///
/// <para><b>R-h45-11 — a colour change must not invalidate physics.</b> Re-projecting this struct and
/// invalidating the canvas is the WHOLE cost of a colour change: no re-solve, and specifically no
/// contour-cache or RBF-factorization invalidation. That holds by construction here — this type reads
/// a <see cref="ColorTheme"/> and produces <see cref="SKColor"/>s, and has no reference to a grid, a
/// context or a scheduler to invalidate even if someone wanted it to.</para>
/// </summary>
public sealed class HarmonicaRenderTheme
{
    // ── Themed roles (§7.9.2 / §7.9.3) ────────────────────────────────────────

    public SKColor Background       { get; init; }
    public SKColor AxisLine         { get; init; }
    public SKColor AxisText         { get; init; }
    /// <summary>ALL text in the §7.5 settings/readout strip.</summary>
    public SKColor ReadoutText      { get; init; }
    public SKColor GridLine         { get; init; }
    /// <summary>The constant-R / constant-X arcs.</summary>
    public SKColor SmithGrid        { get; init; }
    /// <summary>The FULL-opacity iso-line colour. §7.2's ranked alpha ramp is applied on top of it at
    /// draw time — one flat alpha per polyline, never baked into the role.</summary>
    public SKColor Isoline          { get; init; }
    public SKColor IsolineLabel     { get; init; }
    public SKColor GainTrace        { get; init; }
    public SKColor DcivFamily       { get; init; }
    /// <summary><b>Reserved red.</b></summary>
    public SKColor Loadline         { get; init; }
    /// <summary><b>Reserved red.</b></summary>
    public SKColor EfficiencyTrace  { get; init; }
    public SKColor GridPoint        { get; init; }
    /// <summary>A thrown-out Γ point — drawn HOLLOW (§6.3).</summary>
    public SKColor GridPointDropped { get; init; }
    public SKColor OperatingCursor  { get; init; }
    public SKColor ReachableRegion  { get; init; }
    public SKColor EditChrome       { get; init; }
    /// <summary>R-h9a-7: the message-strip text colour. Created here for brief 1C to consume; this
    /// brief only projects the role, nothing draws with it yet.</summary>
    public SKColor Messages         { get; init; }
    /// <summary>R-h9a-7: the progress-bar fill colour — same consumption note as
    /// <see cref="Messages"/> above.</summary>
    public SKColor ProgressBar      { get; init; }

    /// <summary>The five-colour harmonic-identity cycle (§4.2), in band order. Band <c>n</c> uses
    /// <see cref="MarkerBand"/>, which wraps every five bands.</summary>
    public SKColor[] MarkerBands { get; init; } = new SKColor[5];

    /// <summary>The marker colour for harmonic band <paramref name="band"/> (1 = f₀), cycling every
    /// five bands so 6f₀ repeats f₀'s colour — §4.2's own rule, in one place.</summary>
    public SKColor MarkerBand(int band) => MarkerBands[((band - 1) % 5 + 5) % 5];

    // ── Iso-line fade parameters (§7.2 / §7.9.4 — theme values, not constants) ─

    /// <summary>α of the LOWEST level. 1.0 flattens the fade to nothing.</summary>
    public double IsoAlphaFloor { get; init; } = DefaultIsoAlphaFloor;

    /// <summary>The shaping exponent <c>p</c>. &gt; 1 pushes the fade toward the top levels.</summary>
    public double IsoAlphaExponent { get; init; } = DefaultIsoAlphaExponent;

    /// <summary>
    /// R-h9a-11 — tuned from (0.25, 1.5). Read
    /// <see cref="CircuitRF.Ui.Harmonica.IsoLineAlphaRamp.AlphaByte"/>'s actual shape
    /// first, at the default D8 level count (10) both variants have shared since M4: with the OLD
    /// numbers the ramp reached 0.25 / 0.28 / 0.33 / 0.39 / 0.47 / 0.56 / 0.66 / 0.76 / 0.88 / 1.00 —
    /// the bottom level was already a full quarter-opaque and the MIDDLE of the stack (ranks 4–5) sat
    /// at 0.47–0.56, roughly half the top contour's weight. That crowds "the top contour is the
    /// answer" (this file's own §7.2 note, above) — half the level set reads as nearly as important as
    /// the other half, which is the opposite of what a ranked fade is for.
    ///
    /// <para>(0.15, 2.0) reaches 0.15 / 0.16 / 0.19 / 0.24 / 0.32 / 0.41 / 0.53 / 0.66 / 0.82 / 1.00 —
    /// the bottom QUARTER of ranks now stays under 0.25 (background context, not a competing answer)
    /// and only the top quarter clears 0.65. The floor drop and the exponent rise are the SAME lever
    /// pulled from both ends: a lower floor spends less opacity on rank 0 to begin with, and a steeper
    /// exponent (t^p for p &gt; 1 shrinks every t &lt; 1 further as p grows) keeps every non-top rank
    /// nearer that floor for longer before the curve climbs toward 1.0. Neither knob is removed —
    /// both persist as nullable overrides in <c>CharmAppearance</c>, exactly as before; this only
    /// moves the DEFAULT a user who has never touched the colour editor sees.</para>
    /// </summary>
    /// <summary>R8A §1 — lowered from 0.15/2.0 to 0.01/3.00, the owner's own default, pushing the
    /// fade harder still (see this doc comment's own numbers above for the (0.15, 2.0) reasoning,
    /// which the new numbers extend rather than replace). Both are the single source
    /// (<see cref="HarmonicaColorEditor"/>'s two properties fall back to them, <see cref="FromTheme"/>
    /// clamps against them, and a <c>.charm</c>'s own null means "take the built-in") — a document
    /// that never touched the sliders picks these up on load; one that did keeps its own.</summary>
    public const double DefaultIsoAlphaFloor    = 0.01;
    public const double DefaultIsoAlphaExponent = 3.00;

    // ── Projection factory (L2) ───────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="HarmonicaRenderTheme"/> by projecting a <see cref="ColorTheme"/>'s
    /// <c>Harmonica.*</c> roles into <see cref="SKColor"/>. Mirrors
    /// <see cref="SchematicRenderTheme.FromTheme"/> exactly, including its missing-role behaviour: a
    /// role absent from <paramref name="theme"/> falls back to <see cref="ColorTheme.BuiltIn"/>
    /// inside <see cref="ColorTheme.Resolve"/>, so an old <c>.charm</c> still opens after new roles
    /// are added (§7.9.1).
    /// </summary>
    public static HarmonicaRenderTheme FromTheme(
        ColorTheme theme, ColorVariant variant,
        double? isoAlphaFloor = null, double? isoAlphaExponent = null)
    {
        SKColor SK(string role)
        {
            var c = theme.Resolve(role, variant);
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new HarmonicaRenderTheme
        {
            Background       = SK(ColorRole.HarmonicaBackground),
            AxisLine         = SK(ColorRole.HarmonicaAxisLine),
            AxisText         = SK(ColorRole.HarmonicaAxisText),
            ReadoutText      = SK(ColorRole.HarmonicaReadoutText),
            GridLine         = SK(ColorRole.HarmonicaGridLine),
            SmithGrid        = SK(ColorRole.HarmonicaSmithGrid),
            Isoline          = SK(ColorRole.HarmonicaIsoline),
            IsolineLabel     = SK(ColorRole.HarmonicaIsolineLabel),
            GainTrace        = SK(ColorRole.HarmonicaGainTrace),
            DcivFamily       = SK(ColorRole.HarmonicaDcivFamily),
            Loadline         = SK(ColorRole.HarmonicaLoadline),
            EfficiencyTrace  = SK(ColorRole.HarmonicaEfficiencyTrace),
            GridPoint        = SK(ColorRole.HarmonicaGridPoint),
            GridPointDropped = SK(ColorRole.HarmonicaGridPointDropped),
            OperatingCursor  = SK(ColorRole.HarmonicaOperatingCursor),
            ReachableRegion  = SK(ColorRole.HarmonicaReachableRegion),
            EditChrome       = SK(ColorRole.HarmonicaEditChrome),
            Messages         = SK(ColorRole.HarmonicaMessages),
            ProgressBar      = SK(ColorRole.HarmonicaProgressBar),
            MarkerBands =
            [
                SK(ColorRole.HarmonicaMarkerBand1), SK(ColorRole.HarmonicaMarkerBand2),
                SK(ColorRole.HarmonicaMarkerBand3), SK(ColorRole.HarmonicaMarkerBand4),
                SK(ColorRole.HarmonicaMarkerBand5),
            ],
            IsoAlphaFloor    = Clamp01(isoAlphaFloor    ?? DefaultIsoAlphaFloor),
            IsoAlphaExponent = Math.Max(1e-6, isoAlphaExponent ?? DefaultIsoAlphaExponent),
        };
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // ── the bridge to the existing plot renderers ────────────────────────────

    /// <summary>
    /// Projects these tokens into the <see cref="DataDisplay.RenderTheme"/> the EXISTING
    /// <c>PlotRenderer</c> / <c>AxesRenderer</c> / <c>ContourRenderer</c> stack consumes.
    ///
    /// <para>This is what §7.9.1's "built on the existing theming system, not beside it" buys, taken
    /// one step further than the note spells out: harmonicaRF does not merely share the ROLE
    /// vocabulary, it shares the RENDERERS. §2's table is explicit that the Smith/Rect plot, its axes
    /// and ticks, the iso-line drawing, the grid points and the optima markers all already exist and
    /// must not be reimplemented — so the panels build ordinary <c>Plot</c> objects and hand them to
    /// <c>PlotRenderer.Draw</c> with THIS theme in place of the Data Display's own. The result is
    /// visually distinct (§7.9's whole point) without a second renderer to keep in step.</para>
    ///
    /// <para><b>The mapping is deliberate, not mechanical.</b> <c>GridColor</c> takes
    /// <see cref="SmithGrid"/> because on a Smith panel the "grid" IS the constant-R/X arcs;
    /// <c>MinorGridColor</c> takes <see cref="GridLine"/>, the deliberately-low-contrast one. Both
    /// tick and border take <see cref="AxisLine"/> and text takes <see cref="AxisText"/>, which the
    /// role table happens to give the same value — they are separate roles so a user can part
    /// them.</para>
    /// </summary>
    public DataDisplay.RenderTheme ToPlotTheme(bool darkMode) => new(
        GridColor       : SmithGrid,
        MinorGridColor  : GridLine,
        TickColor       : AxisLine,
        TextColor       : AxisText,
        BackgroundColor : Background,
        BorderColor     : AxisLine,
        DarkMode        : darkMode);

    // ── Convenience statics (derived from BuiltIn — one source of truth) ──────

    public static readonly HarmonicaRenderTheme Light = FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
    public static readonly HarmonicaRenderTheme Dark  = FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
}
