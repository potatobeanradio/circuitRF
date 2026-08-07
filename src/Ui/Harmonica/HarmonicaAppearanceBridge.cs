using System;
using System.Collections.Generic;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R-h45-12 — the ONE place a <c>.charm</c>'s appearance block meets circuitRF's role vocabulary.
///
/// <para><c>src/Harmonica</c> stores the appearance as plain <c>role-name → "r,g,b,a"</c> data because
/// it is on the framework-free side of the wall and cannot see <see cref="ColorRole"/>. This bridge
/// is the other half: it knows which roles are <c>Harmonica.*</c>, and it converts in both
/// directions. Nothing else in the codebase should parse or emit that encoding.</para>
///
/// <para><b>Only Harmonica.* roles cross.</b> A <c>.charm</c> carries harmonicaRF's own appearance,
/// not the schematic's or the layout's — writing those in would make the file quietly override a
/// user's whole application theme on open, which is not what "self-describing" means here.</para>
///
/// <para><b>Absent means default, and that is enforced by CONSTRUCTION.</b>
/// <see cref="ToColorTheme"/> starts from <see cref="ColorTheme.BuiltIn"/>'s own maps and overlays
/// only what the file actually stated, so a role omitted from a stored map resolves to its built-in
/// default with no special-casing anywhere downstream (Tier 3's second clause).</para>
/// </summary>
public static class HarmonicaAppearanceBridge
{
    /// <summary>Every role this bridge is allowed to carry, in <see cref="ColorRole.All"/>'s order.</summary>
    public static readonly IReadOnlyList<string> Roles = BuildRoleList();

    private static IReadOnlyList<string> BuildRoleList()
    {
        var list = new List<string>();
        foreach (string role in ColorRole.All)
            if (role.StartsWith("Harmonica.", StringComparison.Ordinal))
                list.Add(role);
        return list;
    }

    // ── ColorTheme → .charm ───────────────────────────────────────────────────

    /// <summary>
    /// Captures the resolved <c>Harmonica.*</c> map for BOTH variants out of <paramref name="theme"/>,
    /// together with the iso-line fade parameters and the label toggle.
    ///
    /// <para><b>Resolved, not "as overridden".</b> Every role is written, even one the user never
    /// touched — that is what §7.9.4's "embeds the resolved role map for both variants" asks for, and
    /// it is what makes a <c>.charm</c> open the same on a machine whose built-in defaults have since
    /// moved. Use <paramref name="onlyIfCustomised"/> to skip writing when the theme is untouched.</para>
    /// </summary>
    public static CharmAppearance ToAppearance(
        ColorTheme theme,
        double? isoAlphaFloor = null,
        double? isoAlphaExponent = null,
        bool? showIsoLineLabels = null,
        bool onlyIfCustomised = false)
    {
        if (onlyIfCustomised && !DiffersFromBuiltIn(theme)
            && isoAlphaFloor is null && isoAlphaExponent is null && showIsoLineLabels is null)
            return CharmAppearance.Default;

        return new CharmAppearance
        {
            Light = Capture(theme, ColorVariant.Light),
            Dark  = Capture(theme, ColorVariant.Dark),
            IsoAlphaFloor     = isoAlphaFloor,
            IsoAlphaExponent  = isoAlphaExponent,
            ShowIsoLineLabels = showIsoLineLabels,
        };
    }

    private static Dictionary<string, string> Capture(ColorTheme theme, ColorVariant variant)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string role in Roles)
        {
            var c = theme.Resolve(role, variant);
            map[role] = CharmAppearance.Encode(c.R, c.G, c.B, c.A);
        }
        return map;
    }

    /// <summary>True when any <c>Harmonica.*</c> role in <paramref name="theme"/> differs from the
    /// built-in default in either variant.</summary>
    public static bool DiffersFromBuiltIn(ColorTheme theme)
    {
        foreach (string role in Roles)
            foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
                if (theme.Resolve(role, variant) != ColorTheme.BuiltIn.Resolve(role, variant))
                    return true;
        return false;
    }

    // ── .charm → ColorTheme ───────────────────────────────────────────────────

    /// <summary>
    /// Overlays a stored appearance onto <paramref name="baseTheme"/> (default:
    /// <see cref="ColorTheme.BuiltIn"/>), returning a new theme. Non-<c>Harmonica.*</c> roles are
    /// carried through untouched, so loading a <c>.charm</c> never disturbs the schematic or layout
    /// palette. An unparseable colour is SKIPPED — that role keeps its default rather than becoming
    /// black, and the caller is told via <paramref name="rejected"/>.
    /// </summary>
    public static ColorTheme ToColorTheme(
        CharmAppearance appearance, out IReadOnlyList<string> rejected, ColorTheme? baseTheme = null)
    {
        var basis = baseTheme ?? ColorTheme.BuiltIn;
        var (baseLight, baseDark) = basis.GetRoleMaps();

        var bad = new List<string>();
        var light = Overlay(baseLight, appearance.Light, basis, ColorVariant.Light, bad);
        var dark  = Overlay(baseDark,  appearance.Dark,  basis, ColorVariant.Dark,  bad);
        rejected = bad;

        return new ColorTheme(basis.Name, light, dark);
    }

    private static Dictionary<string, Rgba> Overlay(
        IReadOnlyDictionary<string, Rgba> baseMap,
        IReadOnlyDictionary<string, string> stored,
        ColorTheme basis, ColorVariant variant,
        List<string> rejected)
    {
        // Start from the basis's own map so every non-Harmonica role, and every Harmonica role the
        // file did not state, keeps exactly what it had.
        var result = new Dictionary<string, Rgba>(baseMap, StringComparer.Ordinal);

        foreach (string role in Roles)
        {
            if (!stored.TryGetValue(role, out string? raw)) continue;      // absent → default
            if (!CharmAppearance.TryDecode(raw, out byte r, out byte g, out byte b, out byte a))
            {
                rejected.Add($"{role} ({variant}): '{raw}'");
                continue;                                                   // malformed → default
            }
            result[role] = new Rgba(r, g, b, a);
        }

        // A role the basis itself does not define (Harmonica.* always does, but a caller may pass a
        // partial basis) must still resolve — fall back through the basis's own Resolve.
        foreach (string role in Roles)
            if (!result.ContainsKey(role))
                result[role] = basis.Resolve(role, variant);

        return result;
    }

    // ── the whole projection, in one call ─────────────────────────────────────

    /// <summary>
    /// The convenience the panels actually use: a stored appearance straight to the Layer-2 tokens
    /// for one variant, with the fade parameters threaded through.
    /// </summary>
    public static CircuitRF.Ui.Renderers.HarmonicaRenderTheme ToRenderTheme(
        CharmAppearance appearance, ColorVariant variant, ColorTheme? baseTheme = null)
    {
        var theme = ToColorTheme(appearance, out _, baseTheme);
        return CircuitRF.Ui.Renderers.HarmonicaRenderTheme.FromTheme(
            theme, variant, appearance.IsoAlphaFloor, appearance.IsoAlphaExponent);
    }
}
