using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>One checkable line of the solutions filter — an order, or a response family.</summary>
public sealed partial class MatchSolutionFilterToggle : ObservableObject
{
    private readonly Action _changed;

    internal MatchSolutionFilterToggle(string label, int order, ResponseShape? shape, Action changed)
    {
        Label = label;
        Order = order;
        Shape = shape;
        _changed = changed;
    }

    /// <summary>What the line reads.</summary>
    public string Label { get; }

    /// <summary>The order this line stands for, or 0 when it stands for a response family.</summary>
    public int Order { get; }

    /// <summary>The response family this line stands for, or null when it stands for an order.</summary>
    public ResponseShape? Shape { get; }

    /// <summary>Whether solutions of this order / family are listed.</summary>
    [ObservableProperty] private bool _isOn = true;

    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// What the Solutions panel's filter button holds: which orders and which response families are
/// listed, and whether Q-adjusted and negative-element solutions are among them.
/// </summary>
/// <remarks>
/// <b>This is why the specification pane lost its Order, Filter Response and Options cards</b>
/// (owner, 2026-08-28). Those three were INPUTS to a search that produced one order's solutions in
/// one family; the search now runs the whole cross-product, so the same four knobs are the wrong
/// shape as inputs and the right shape as a filter over the answer. The user picks a network by
/// looking at networks, rather than by guessing at a setting and reading what it produced.
///
/// <para><b>It filters, it does not search.</b> Every toggle here is display state — turning
/// "Q-adjusted" off hides rows that are already in hand, and turning it back on shows them again
/// with no work done. That is what makes the filter instant where the searches behind it are not,
/// and it is why none of this lives in <see cref="MatchDesign"/>: a filter setting must not make a
/// saved design different from the one that was saved.</para>
///
/// <para><b>The defaults are Q-adjusted ON and negative components OFF</b> (owner, same round). They
/// are not symmetrical because the two are not: a Q-adjusted solution is an ordinary network with one
/// more element at the analysis end, while a negative element is something the user has to get rid of
/// before the design is buildable. The first belongs in the list by default; the second is a
/// deliberate ask.</para>
/// </remarks>
public sealed partial class MatchSolutionFilterViewModel : ObservableObject
{
    private bool _quiet;

    internal MatchSolutionFilterViewModel(IEnumerable<MatchResponseOptionViewModel> responses)
    {
        // The family names are the CARDS' own, from one place — a filter line that read differently
        // from the cards it hides would be a filter nobody could use.
        foreach (var option in responses)
            Responses.Add(new MatchSolutionFilterToggle(
                MatchSolutionRowViewModel.FamilyName(option.Shape), 0, option.Shape, Raise));
    }

    /// <summary>One line per order the termination pair permits.</summary>
    public ObservableCollection<MatchSolutionFilterToggle> Orders { get; } = [];

    /// <summary>One line per response family.</summary>
    public ObservableCollection<MatchSolutionFilterToggle> Responses { get; } = [];

    /// <summary>Whether §4.6's Q-adjusted solutions are listed. On by default.</summary>
    [ObservableProperty] private bool _showQAdjusted = true;

    /// <summary>Whether solutions carrying a non-positive element are listed. Off by default.</summary>
    [ObservableProperty] private bool _showNegativeComponents;

    /// <summary>Raised whenever any of the above moves. The Designer re-filters; it does not re-search.</summary>
    public event EventHandler? Changed;

    partial void OnShowQAdjustedChanged(bool value) => Raise();
    partial void OnShowNegativeComponentsChanged(bool value) => Raise();

    /// <summary>
    /// Re-declares which orders the filter offers, preserving the state of the ones that survive.
    /// </summary>
    /// <remarks>
    /// <c>MatchOrders.ValidOrders</c> narrows to two or three entries the moment both terminations
    /// carry a reactance, so this list genuinely changes under the user. A rebuild that reset every
    /// line to ON would silently undo a filter they had set; a rebuild that dropped a line the user
    /// had turned OFF and later brought it back OFF would hide solutions they never asked to hide.
    /// So a surviving order keeps its state and a NEW one arrives on, which is the reading of "these
    /// orders are now possible" that shows the user the most.
    /// </remarks>
    internal void SetOrders(IReadOnlyList<int> orders)
    {
        if (Orders.Select(o => o.Order).SequenceEqual(orders)) return;

        var was = Orders.ToDictionary(o => o.Order, o => o.IsOn);
        _quiet = true;
        try
        {
            Orders.Clear();
            foreach (int order in orders)
            {
                var toggle = new MatchSolutionFilterToggle(
                    $"Order {order.ToString(CultureInfo.InvariantCulture)}", order, null, Raise);
                if (was.TryGetValue(order, out bool on)) toggle.IsOn = on;
                Orders.Add(toggle);
            }
        }
        finally { _quiet = false; }

        Raise();
    }

    /// <summary>Whether one row survives the filter as it stands.</summary>
    public bool Accepts(MatchSolutionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Solution.QAdjust > 0 && !ShowQAdjusted) return false;
        if (row.HasNegativeComponents && !ShowNegativeComponents) return false;

        // An order the pair no longer permits has no line to consult. Such a row can only be one the
        // design is already on — it is shown, because a panel that hides the applied solution is
        // answering "which one am I looking at?" with nothing.
        var order = Orders.FirstOrDefault(o => o.Order == row.Order);
        if (order is not null && !order.IsOn) return false;

        var response = Responses.FirstOrDefault(r => r.Shape == row.Response);
        return response is null || response.IsOn;
    }

    /// <summary>What the filter button's tooltip says it is doing.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(4);

            var offOrders = Orders.Where(o => !o.IsOn).Select(o => o.Order).ToList();
            if (offOrders.Count > 0)
                parts.Add("hiding order " + string.Join(", ", offOrders));

            var offShapes = Responses.Where(r => !r.IsOn).Select(r => r.Label).ToList();
            if (offShapes.Count > 0)
                parts.Add("hiding " + string.Join(", ", offShapes));

            if (!ShowQAdjusted) parts.Add("hiding Q-adjusted");
            if (!ShowNegativeComponents) parts.Add("hiding negative components");

            return parts.Count == 0
                ? "Filter — every solution found is listed."
                : "Filter — " + string.Join("; ", parts) + ".";
        }
    }

    /// <summary>True when anything at all is being hidden.</summary>
    /// <remarks>
    /// <b>Nothing in the window renders this any more</b> (owner, 2026-08-28: the small
    /// warning-coloured dot beside the filter button was distracting, and it went). It was lit on
    /// almost every design, because the DEFAULT filter hides negative-component solutions — a mark
    /// that is always on marks nothing, and a warning colour for a normal state is worse than no
    /// mark at all.
    ///
    /// <para>Kept, because it is the one place the question "is this list the whole answer?" is
    /// decided, and <see cref="Summary"/> — the button's tooltip, which says in words what the dot
    /// said in colour — is its long form. A caller that needs the yes/no should ask here rather than
    /// re-derive it from four pieces of state.</para>
    /// </remarks>
    public bool IsNarrowed =>
        !ShowQAdjusted || !ShowNegativeComponents
        || Orders.Any(o => !o.IsOn) || Responses.Any(r => !r.IsOn);

    private void Raise()
    {
        if (_quiet) return;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsNarrowed));
        Changed?.Invoke(this, EventArgs.Empty);
    }

}
