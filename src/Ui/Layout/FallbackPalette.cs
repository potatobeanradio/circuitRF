using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Generates a deterministic <see cref="LayerDef"/> for any <see cref="LayerKey"/> that isn't
/// defined by a resolved <see cref="Technology"/> — or for every layer when there is no technology
/// at all (docs/design/layout-view.md §2.4, "generated fallback palette"). Framework-free, no state.
///
/// Two distinct callers rely on this: no technology at all (every layer comes from the palette),
/// and a resolved technology that simply doesn't define a given layer (common after a GDSII import
/// in L4) — the palette fills just that gap.
///
/// <b>Determinism is the requirement</b> — the same key must produce the same color on every
/// machine and every session, or two people comparing screenshots will disagree about which layer
/// is which. The hash below is a fixed FNV-1a variant, deliberately NOT .NET's
/// <see cref="HashCode.Combine{T1, T2}"/>, which is randomized per process and would violate this.
/// </summary>
public static class FallbackPalette
{
    private const double Saturation = 0.55;
    private const double Value      = 0.85;

    public static LayerDef For(LayerKey key) => new()
    {
        Key         = key,
        Name        = $"L{key.Layer}/{key.Datatype}",
        Color       = ColorFor(key),
        FillOpacity = 0.35,
        ZOrder      = key.Layer * 1000 + key.Datatype,
        Visible     = true,
        Selectable  = true,
    };

    private static Rgba ColorFor(LayerKey key)
    {
        double hue = StableHash(key.Layer, key.Datatype) % 360;
        return HsvToRgb(hue, Saturation, Value);
    }

    // FNV-1a — fixed, non-randomized, identical across machines and .NET versions.
    // Do NOT replace with HashCode.Combine: its seed is randomized per process.
    private static uint StableHash(int layer, int datatype)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)layer) * 16777619u;
            h = (h ^ (uint)datatype) * 16777619u;
            return h;
        }
    }

    private static Rgba HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;

        (double r1, double g1, double b1) = h switch
        {
            < 60  => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _     => (c, 0.0, x),
        };

        var r = (byte)Math.Round((r1 + m) * 255);
        var g = (byte)Math.Round((g1 + m) * 255);
        var b = (byte)Math.Round((b1 + m) * 255);
        return new Rgba(r, g, b);
    }
}
