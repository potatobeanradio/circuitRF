using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// brief NZ-2: the Freq picker of an expression trace whose result keeps a <c>freq</c> axis beside another — a noise, gain
/// or stability circle (<c>ga_circle(SP1.S, 12)</c>, <c>[freq, point]</c>), or a quantity pinned to one frequency
/// (<c>SP1.Sopt</c> as a marker). It is the contour trace's frequency picker for an expression: the trace is drawn at the
/// chosen frequency, which the .cdd keeps.
/// </summary>
public partial class TraceRowViewModel
{
    /// <summary>Shown when the last resolve pinned a frequency axis with more than one point.</summary>
    public bool ShowExpressionFreqPicker => !IsContourTrace && _trace.ExpressionFreqPin is { Freq.Length: > 1 };

    /// <summary>The pinned axis' frequencies, in GHz as the contour picker shows them. One list per axis: a ComboBox
    /// handed a new list on every refresh drops its selection (src/Ui/CLAUDE.md).</summary>
    public IReadOnlyList<string> ExpressionFrequencies
    {
        get
        {
            var axis = _trace.ExpressionFreqPin?.Freq;
            if (!ReferenceEquals(axis, _expressionFreqAxis))
            {
                _expressionFreqAxis = axis;
                _expressionFrequencies = axis is null ? [] : [.. axis.Values.Select(f => $"{f / 1e9:G4} GHz")];
            }
            return _expressionFrequencies;
        }
    }
    private RfCore.Data.Axis? _expressionFreqAxis;
    private IReadOnlyList<string> _expressionFrequencies = [];

    /// <summary>The frequency drawn: a change re-resolves the trace there.</summary>
    public int ExpressionFreqIndex
    {
        get => _trace.ExpressionFreqPin?.Index ?? 0;
        set
        {
            if (value < 0 || _trace.ExpressionFreqPin is not { } p || value == p.Index) return;
            _trace.ExpressionFreqIndex = value;
            _parent.RebuildAndNotify();
        }
    }

    /// <summary>
    /// The trace is a typed expression — <c>ns_circle(S, NFmin + 0.25)</c>, <c>V[:, 1] - V[:, 2]</c> — rather than one
    /// quantity the group and item pickers can name. Those pickers then show nothing selected, with
    /// <see cref="PickerPlaceholder"/>, instead of the first item as if the trace were it. The rule is
    /// <c>TraceResolve</c>'s own: an expression with no single-cube identity — and one that resolves, since an invalid
    /// edit leaves the pickers as they were (ViSymmetricCardTests.SpecEdit_Invalid_BestEffort).
    /// </summary>
    public bool IsTypedExpression => _trace.Expression is not null && _trace.ExpressionError is null
                                     && !(_trace.CubeName is not null && _trace.Slice is not null);

    /// <summary>What the group picker says with nothing selected: "Expression" on a typed expression, else the
    /// no-source prompt it always had.</summary>
    public string GroupPlaceholder => IsTypedExpression ? "Expression" : "(load a file…)";

    /// <summary>What the item picker says with nothing selected: nothing — the group picker beside it already says
    /// "Expression", and the word twice reads as two things.</summary>
    public string? PickerPlaceholder => null;

    /// <summary>A commit that made the trace a typed expression: the pickers drop their selection, which no longer
    /// names it, without re-aiming the trace.</summary>
    private void ClearPickersForExpression()
    {
        bool saved = _suppressDataCallback;
        _suppressDataCallback = true;
        try
        {
            SelectedGroup = null;
            FilterSignalsToGroup(null);
            SelectedSignal = null;
        }
        finally { _suppressDataCallback = saved; }
        RefreshExpressionFreq();
    }

    private void RefreshExpressionFreq()
    {
        OnPropertyChanged(nameof(IsTypedExpression));
        OnPropertyChanged(nameof(GroupPlaceholder));
        OnPropertyChanged(nameof(PickerPlaceholder));
        OnPropertyChanged(nameof(ShowMatrixTypeCombo));
        OnPropertyChanged(nameof(ShowExpressionFreqPicker));
        OnPropertyChanged(nameof(ExpressionFrequencies));
        OnPropertyChanged(nameof(ExpressionFreqIndex));
    }
}
