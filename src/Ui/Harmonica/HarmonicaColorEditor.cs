// ================================================================
//  HarmonicaColorEditor.cs  —  M5 of brief-harmonicarf-h7
//
//  R-h7-15  colours live in the .charm, and the divergence from circuitRF's Layer 3 is deliberate:
//           harmonicaRF runs with no workspace open and ships standalone, so a theme NAME plus a
//           search path has nothing to resolve against. CharmAppearance stores the resolved map for
//           BOTH variants; this gives it an editor.
//  R-h7-16  a colour change must not invalidate physics. It cannot, by construction — nothing here
//           touches a ContourGrid, a HarmonicaContext or a scheduler — and the gate proves it through
//           THIS type rather than through the raw property.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// §7.9.4's colour editor, as logic. The dialog is a renderer of this.
///
/// <para><b>It writes <see cref="CharmAppearance"/> and nothing else.</b> Every mutation goes through
/// the same get/set pair the document exposes, so live preview is "assign the appearance" — which
/// re-projects <c>HarmonicaRenderTheme</c> and invalidates the canvas, and has no path to a grid, a
/// context or a scheduler to invalidate (R-h45-11, extended here to the editor path).</para>
///
/// <para><b>Only <c>Harmonica.*</c> roles cross.</b> <see cref="HarmonicaAppearanceBridge"/> owns that
/// list and this type asks it, rather than filtering on the prefix a second time.</para>
/// </summary>
public sealed class HarmonicaColorEditor
{
    private readonly Func<CharmAppearance>   _get;
    private readonly Action<CharmAppearance> _set;

    public HarmonicaColorEditor(Func<CharmAppearance> get, Action<CharmAppearance> set)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    /// <summary>Every editable role, in <see cref="ColorRole.All"/>'s own order.</summary>
    public static IReadOnlyList<string> Roles => HarmonicaAppearanceBridge.Roles;

    /// <summary>The label the editor shows for a role — the bare name after "Harmonica.".</summary>
    public static string LabelFor(string role)
        => role.StartsWith("Harmonica.", StringComparison.Ordinal) ? role["Harmonica.".Length..] : role;

    // ── reading ───────────────────────────────────────────────────────────────

    /// <summary>The RESOLVED colour for a role — the document's own value, or the built-in default
    /// when it has none. Never a sentinel: §7.9.1's "roles absent from a stored theme fall back to the
    /// built-in default" is what makes an old <c>.charm</c> open after new roles are added.</summary>
    public Rgba Resolve(string role, ColorVariant variant)
    {
        var stored = variant == ColorVariant.Dark ? _get().Dark : _get().Light;
        if (stored.TryGetValue(role, out string? raw)
            && CharmAppearance.TryDecode(raw, out byte r, out byte g, out byte b, out byte a))
            return new Rgba(r, g, b, a);

        return ColorTheme.BuiltIn.Resolve(role, variant);
    }

    /// <summary>Whether the document overrides this role in this variant.</summary>
    public bool IsOverridden(string role, ColorVariant variant)
        => Resolve(role, variant) != ColorTheme.BuiltIn.Resolve(role, variant);

    /// <summary>Whether ANYTHING has been recoloured. Drives the "Reset all" button's enablement, and
    /// is exactly the condition <c>CharmIo</c> uses to omit the block.</summary>
    public bool IsDefault => _get().IsDefault;

    // ── writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets one role in one variant. Live preview is this call — <b>no re-solve, no re-fit, no
    /// re-factorization</b>, which is R-h7-16 and holds because nothing on this path can reach a
    /// solve object at all.
    /// </summary>
    public void Set(string role, ColorVariant variant, Rgba colour)
    {
        if (!Roles.Contains(role))
            throw new ArgumentException($"'{role}' is not a Harmonica.* role.", nameof(role));

        var a = _get();
        var map = new Dictionary<string, string>(
            variant == ColorVariant.Dark ? a.Dark : a.Light, StringComparer.Ordinal)
        {
            [role] = CharmAppearance.Encode(colour.R, colour.G, colour.B, colour.A),
        };

        _set(variant == ColorVariant.Dark ? a with { Dark = map } : a with { Light = map });
    }

    /// <summary>
    /// §7.9.4's per-role revert (right-click a role → <i>Reset</i>). Reverts <b>both</b> variants:
    /// "undo one role" is the want the note names, and a role reverted in one variant only leaves the
    /// document half-recoloured in a way nothing on screen can show.
    /// </summary>
    public void Revert(string role)
    {
        var a = _get();
        var light = new Dictionary<string, string>(a.Light, StringComparer.Ordinal);
        var dark  = new Dictionary<string, string>(a.Dark,  StringComparer.Ordinal);
        light.Remove(role);
        dark.Remove(role);
        _set(a with { Light = light, Dark = dark });
    }

    /// <summary>§7.9.4's <i>Reset all colours to defaults</i>. Leaves the iso-line fade parameters and
    /// the label toggle ALONE — they are not colours, and a user who flattened the fade did not ask
    /// for it back when they asked for the palette back.</summary>
    public void ResetAllColours()
    {
        var a = _get();
        _set(a with
        {
            Light = new Dictionary<string, string>(StringComparer.Ordinal),
            Dark  = new Dictionary<string, string>(StringComparer.Ordinal),
        });
    }

    /// <summary>Resets everything the appearance block carries, colours and fade alike.</summary>
    public void ResetEverything() => _set(CharmAppearance.Default);

    // ── §7.2's fade parameters ────────────────────────────────────────────────

    /// <summary>§7.2's α_floor. <c>1</c> flattens the fade with no code change, which is the point of
    /// its being a theme value rather than a constant.</summary>
    public double IsoAlphaFloor
    {
        get => _get().IsoAlphaFloor ?? CircuitRF.Ui.Renderers.HarmonicaRenderTheme.DefaultIsoAlphaFloor;
        set => _set(_get() with { IsoAlphaFloor = Math.Clamp(value, 0.0, 1.0) });
    }

    /// <summary>§7.2's shaping exponent <c>p</c>.</summary>
    public double IsoAlphaExponent
    {
        get => _get().IsoAlphaExponent ?? CircuitRF.Ui.Renderers.HarmonicaRenderTheme.DefaultIsoAlphaExponent;
        set => _set(_get() with { IsoAlphaExponent = Math.Clamp(value, 0.05, 8.0) });
    }

    /// <summary>D11 — iso-line labels, default OFF.</summary>
    public bool ShowIsoLineLabels
    {
        get => _get().ShowIsoLineLabels ?? false;
        set => _set(_get() with { ShowIsoLineLabels = value });
    }

    // ── .ccolor interchange (§7.9.4) ──────────────────────────────────────────

    /// <summary>
    /// Exports the document's <c>Harmonica.*</c> roles as an ordinary <c>.ccolor</c>, through
    /// <see cref="ColorThemeIo"/> unchanged — so the file is readable by circuitRF's own Preferences
    /// and by another <c>.charm</c>.
    ///
    /// <para><b>Only Harmonica.* roles are written.</b> A <c>.ccolor</c> exported from harmonicaRF
    /// that also carried the schematic palette would silently overwrite a user's whole application
    /// theme when imported there — the same reasoning that keeps them out of the <c>.charm</c>.</para>
    /// </summary>
    public string ExportCcolor(string name = "harmonicaRF")
    {
        var light = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        var dark  = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        foreach (string role in Roles)
        {
            light[role] = Resolve(role, ColorVariant.Light);
            dark[role]  = Resolve(role, ColorVariant.Dark);
        }
        return ColorThemeIo.Save(new ColorTheme(name, light, dark));
    }

    /// <summary>
    /// Imports a <c>.ccolor</c> into this document. Roles it does not state keep what they had — a
    /// <c>.ccolor</c> written for the schematic carries no <c>Harmonica.*</c> role at all and must
    /// therefore change nothing rather than blanking the palette.
    /// </summary>
    /// <returns>How many roles the file actually supplied, per variant, so the caller can say so
    /// rather than leaving a no-op looking like a failure.</returns>
    public (int Light, int Dark) ImportCcolor(string json)
    {
        var theme = ColorThemeIo.Load(json);
        var (light, dark) = theme.GetRoleMaps();

        var a = _get();
        var newLight = new Dictionary<string, string>(a.Light, StringComparer.Ordinal);
        var newDark  = new Dictionary<string, string>(a.Dark,  StringComparer.Ordinal);

        int nL = 0, nD = 0;
        foreach (string role in Roles)
        {
            if (light.TryGetValue(role, out var cl))
            { newLight[role] = CharmAppearance.Encode(cl.R, cl.G, cl.B, cl.A); nL++; }
            if (dark.TryGetValue(role, out var cd))
            { newDark[role] = CharmAppearance.Encode(cd.R, cd.G, cd.B, cd.A); nD++; }
        }

        _set(a with { Light = newLight, Dark = newDark });
        return (nL, nD);
    }
}
