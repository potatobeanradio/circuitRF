// One row of the board panel's layer list (brief-railrf-20-layer-visibility.md R-rail20-1a).

using CircuitRF.Design.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One drawing layer, as the board panel's layer list shows it — a swatch, a name and a box.
/// </summary>
/// <remarks>
/// <b>It writes through the view model, not through the technology.</b> The tick is a question about
/// this WINDOW's picture and it never reaches the <c>.ctech</c>: <see cref="RailRfViewModel"/> owns
/// the hidden set, this row asks it to change, and the row is then told what the answer was. So a
/// row cannot come to disagree with the drawing, and there is nothing here to write back.
///
/// <para><b>A layer the TECHNOLOGY does not draw can still be ticked on here</b> (2026-09-23). It was
/// a disabled row until then, because the window's set was unioned with the technology's — which
/// left editing the <c>.ctech</c> as the only way to see such a layer, the route this list exists to
/// retire. The window now overrides the technology in both directions; the tooltip still says what
/// the <c>.ctech</c> itself states.</para>
/// </remarks>
public sealed partial class RailLayerRowViewModel : ObservableObject
{
    private readonly Action<LayerKey, bool> _write;
    private bool _isRefreshing;

    public RailLayerRowViewModel(LayerDef layer, bool visible, bool hiddenByTechnology,
                                 Action<LayerKey, bool> write)
    {
        Key                = layer.Key;
        Name               = layer.Name is { Length: > 0 } n ? n : $"L{layer.Key.Layer}/{layer.Key.Datatype}";
        SwatchColor        = new Avalonia.Media.Color(255, layer.Color.R, layer.Color.G, layer.Color.B);
        HiddenByTechnology = hiddenByTechnology;
        _write             = write;
        _visible           = visible;
    }

    /// <summary>The drawing layer this row is about.</summary>
    public LayerKey Key { get; }

    /// <summary>The technology's own name for it, or the fallback palette's spelling.</summary>
    public string Name { get; }

    /// <summary>The layer's colour, opaque — the swatch is an identifier, not a preview of the fill,
    /// so it is drawn at full alpha rather than at the layer's own fill opacity.</summary>
    public Avalonia.Media.Color SwatchColor { get; }

    /// <summary>True where the <c>.ctech</c>'s own <c>Vis</c> box is off for this layer — what
    /// "Follow the technology" returns the box to, and what the tooltip reports.</summary>
    public bool HiddenByTechnology { get; }

    /// <summary>What the row says about this layer, and what its tooltip is.</summary>
    public string Tip =>
        $"{Name} — show or hide it on this board. This window only; nothing is written to the .ctech"
        + (HiddenByTechnology ? ", which hides this layer." : ", which shows this layer.");

    /// <summary>Whether the board is drawing it.</summary>
    [ObservableProperty]
    private bool _visible;

    partial void OnVisibleChanged(bool value)
    {
        if (_isRefreshing) return;
        _write(Key, value);
    }

    /// <summary>Sets the box without writing back — what "Follow the technology" and an adopted
    /// technology call.</summary>
    internal void Refresh(bool visible)
    {
        _isRefreshing = true;
        Visible = visible;
        _isRefreshing = false;
    }
}
