using System.ComponentModel;
using Avalonia.Controls;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The profile view as a dock tool (wbond.md §10.1, WB39a/M3) — a host, not a second implementation.
///
/// <para>What a HOST owes the profile view is the grid pitch: that canvas draws its own grid from a
/// number nobody else can derive for it, and left unset the docked panel showed no grid at all (owner,
/// 2026-08-17). It is a plain CLR property on the control rather than a styled one, so it is pushed from
/// here rather than bound.</para>
/// </summary>
public partial class WBondProfileToolView : UserControl
{
    private WBondProfileTool? _bound;

    public WBondProfileToolView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
    }

    private void Rebind()
    {
        if (_bound is not null) _bound.PropertyChanged -= OnToolPropertyChanged;

        _bound = DataContext as WBondProfileTool;
        if (_bound is not null) _bound.PropertyChanged += OnToolPropertyChanged;

        PushGridPitch();
    }

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WBondProfileTool.GridPitchNm) or nameof(WBondProfileTool.Editor))
            PushGridPitch();
    }

    private void PushGridPitch() => ProfileView.GridPitchNm = _bound?.GridPitchNm ?? 0;
}
