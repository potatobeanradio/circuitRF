using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// One entry of the Response selector (match.md §6.6). A response that cannot absorb both ends at the
/// current order is <b>shown disabled with the numeric reason in its tooltip</b>, never silently
/// missing — and the numbers come from MN-1's own refusal rather than being recomputed here.
/// </summary>
public sealed partial class MatchResponseOptionViewModel : ObservableObject
{
    internal MatchResponseOptionViewModel(ResponseShape shape, string display, string description)
    {
        Shape = shape;
        Display = display;
        Description = description;
    }

    /// <summary>The prototype family this entry selects.</summary>
    public ResponseShape Shape { get; }

    /// <summary>What the radio button reads.</summary>
    public string Display { get; }

    /// <summary>The one-line explanation shown when the entry is available.</summary>
    public string Description { get; }

    /// <summary>False when the family cannot absorb both ends at the current order.</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>True for the design's current response.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// The refusal's own message when disabled — "Bessel cannot absorb termination 2 at order 4 — its
    /// far-end Q reaches only 0.33 against the 0.64 needed" — and <see cref="Description"/> otherwise.
    /// </summary>
    [ObservableProperty] private string _tooltip = "";

    /// <summary>The refusal behind a disabled entry, or null.</summary>
    public MatchRefusal? Refusal { get; internal set; }

    /// <summary>
    /// How solid the entry looks in the drop-down list.
    /// </summary>
    /// <remarks>
    /// The Response selector became a ComboBox (owner, 2026-08-19), and a ComboBox item does not dim
    /// itself from <c>IsEnabled</c> the way a RadioButton's own content does — its item container
    /// stays fully opaque, so an infeasible family would look pickable. This is the one place that
    /// decides how "cannot be picked" looks in the list.
    /// </remarks>
    public double ListOpacity => IsEnabled ? 1.0 : 0.45;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(ListOpacity));
}
