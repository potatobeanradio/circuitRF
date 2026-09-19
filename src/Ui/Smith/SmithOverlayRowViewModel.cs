using CircuitRF.Design.Smith;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One row of the overlays list — a piece of reference material under the work
/// (<c>brief-smith-8-overlays-markers.md</c> <c>R-smith8-2</c>, <c>R-smith8-4</c>;
/// <c>docs/design/smith-chart.md</c> §5.7).
/// </summary>
/// <remarks>
/// <b>An EDITOR over the document's own <see cref="SmithOverlayRef"/>, not a copy of it</b> —
/// <see cref="SmithGeneratorRowViewModel"/>'s shape and for its reason. Every write goes out through
/// <see cref="SmithChartViewModel.EditOverlay"/>, which is what makes it one undo entry and what
/// re-resolves the row.
///
/// <para><b>An unresolved row is marked and says why in its tooltip</b>, and the document still
/// opens with the rest of the chart drawn. That is the opposite of an S1P ELEMENT, whose missing
/// file is a refusal — an element is part of the cascade and the walk cannot be evaluated without
/// it, while an overlay is something the user is comparing AGAINST and its absence costs them the
/// comparison and nothing else.</para>
/// </remarks>
public sealed partial class SmithOverlayRowViewModel : ObservableObject
{
    private readonly SmithChartViewModel _owner;

    internal SmithOverlayRowViewModel(SmithChartViewModel owner, SmithOverlayRef overlay)
    {
        _owner  = owner;
        Overlay = overlay;
    }

    /// <summary>The document's own row. Mutated in place by <see cref="SmithChartViewModel"/>.</summary>
    internal SmithOverlayRef Overlay { get; }

    /// <summary>What the curve is called on the chart and in the trace list — the file (or cube) and
    /// the quantity, which is the pair that tells two overlays on the same part apart.</summary>
    public string Label => SmithOverlayResolver.Label(Overlay);

    /// <summary>The reference itself, shown in full so a row that does not resolve shows the path
    /// that did not.</summary>
    public string Source => Overlay.Source;

    /// <summary>Draw it. A hidden overlay is not put on the <c>Plot</c> at all, so it is also out of
    /// the trace list, the legend and the Add Marker menu.</summary>
    public bool Visible
    {
        get => Overlay.Visible;
        set => _owner.EditOverlay(this, "Show overlay", o => o.Visible = value);
    }

    /// <summary>
    /// Renormalize to the chart's Z₀ on the way in (<c>R-smith8-3</c>).
    /// </summary>
    /// <remarks>
    /// <b>On by default and it must be</b>: a 75 Ω part drawn on a 50 Ω chart without this is a
    /// curve in the wrong place that looks entirely plausible. Off is the deliberate "show me the
    /// file's own numbers".
    /// </remarks>
    public bool Renormalize
    {
        get => Overlay.Renormalize;
        set => _owner.EditOverlay(this, "Renormalize overlay", o => o.Renormalize = value);
    }

    /// <summary>
    /// Let this row take part in the chart's autoscale (<c>R-smith8-4</c>).
    /// </summary>
    /// <remarks>
    /// <b>Off by default.</b> A stability circle can be enormous — an unconditionally stable
    /// device's load circle routinely sits far outside the unit disc — and one unlucky overlay
    /// reframing the chart would squash the cascade the user is working on into a corner of it.
    /// </remarks>
    public bool IncludeInAutoscale
    {
        get => Overlay.IncludeInAutoscale;
        set => _owner.EditOverlay(this, "Autoscale to overlay", o => o.IncludeInAutoscale = value);
    }

    /// <summary>The quantity cell: <c>S11</c>, <c>S21</c>, <c>Z11</c>, a virtual Y, or a cube spec.
    /// The digits are <b>1-based port numbers</b>, never indices.</summary>
    public string QuantityEntry
    {
        get => Overlay.Quantity;
        set => _owner.EditOverlay(this, "Edit overlay quantity", o => o.Quantity = value?.Trim() ?? "");
    }

    /// <summary>The derived mode, as a <c>DerivedParameters</c> member name.
    /// <c>SourceStabilityCircle</c> and <c>LoadStabilityCircle</c> are the two this tool was asked
    /// for by name; both are computed by the existing derived path and not here.</summary>
    public string DerivedEntry
    {
        get => Overlay.Derived;
        set => _owner.EditOverlay(this, "Edit overlay quantity",
                                  o => o.Derived = string.IsNullOrWhiteSpace(value) ? "None" : value.Trim());
    }

    /// <summary>Why this row draws nothing, or null. <b>The resolver's own sentence</b>, surfaced
    /// rather than re-written, with the path it looked for in it.</summary>
    public string? Unresolved
    {
        get => _unresolved;
        internal set
        {
            if (_unresolved == value) return;
            _unresolved = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnresolved));
        }
    }
    private string? _unresolved;

    /// <summary>True when this row is marked — what the view turns red and hangs the tooltip on.</summary>
    public bool IsUnresolved => _unresolved is { Length: > 0 };

    /// <summary>Re-reads every cell from the document — a rejected edit still notifies, for
    /// <see cref="SmithGeneratorRowViewModel"/>'s reason: a control left showing text the model does
    /// not hold reads as accepted.</summary>
    public void NotifyAll()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(Renormalize));
        OnPropertyChanged(nameof(IncludeInAutoscale));
        OnPropertyChanged(nameof(QuantityEntry));
        OnPropertyChanged(nameof(DerivedEntry));
    }
}
