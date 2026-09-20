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
/// <para><b>A layer the TECHNOLOGY does not draw is shown and disabled</b>, never silently ticked
/// off. The window's set is unioned with the technology's (R-rail20-1c), so a tick on such a row
/// would be a control that can be pressed and does nothing — the line this window's own panel
/// buttons already draw. The tooltip says which file is hiding it.</para>
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

    /// <summary>True where the <c>.ctech</c> itself is not drawing this layer. The box is then
    /// unticked and disabled — see the type's own remarks.</summary>
    public bool HiddenByTechnology { get; }

    /// <summary>Whether the box may be pressed at all.</summary>
    public bool CanToggle => !HiddenByTechnology;

    /// <summary>What the row says about this layer, and what its tooltip is.</summary>
    public string Tip => HiddenByTechnology
        ? $"{Name} — the technology is not drawing this layer, so this window cannot show it. "
        + "Its Vis box is in the .ctech."
        : $"{Name} — show or hide it on this board. This window only; nothing is written to the .ctech.";

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
