// brief-hzn-12 — the setup panel's plane-wave note. A plane wave belongs to the 3D view, not to a setup: the view lists its waves under
// Excitations, at most one is enabled, and the enabled one drives every Driven setup of the view in place of its ports. So the panel
// edits nothing here; it says which wave drives the setup (or that its solver cannot run one) and, after a run, the monostatic
// cross-section.

using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;
using RfCore.Data;

namespace CircuitRF.Ui.Layout.Em;

public partial class EmSetupEditorViewModel
{
    /// <summary>What drives the setup when a plane wave does, in a sentence; null when its ports do.</summary>
    [ObservableProperty] private string? _excitationNote;

    /// <summary>The monostatic cross-section of the last scattering run of this setup, in this session; null before one.</summary>
    [ObservableProperty] private string? _rcsSummary;

    /// <summary>True when the setup is driven by the 3D view's enabled plane wave.</summary>
    public bool IsPlaneWaveSetup => Is3DSetup && Problem3DChoice.Value == Em3dProblemType.Driven && !IsThermalSetup && Working.PlaneWave is not null;

    private void RaisePlaneWaveVisibility()
    {
        OnPropertyChanged(nameof(IsPlaneWaveSetup));
        OnPropertyChanged(nameof(Problem3DDescription));
        OnPropertyChanged(nameof(RadiationPatternDisabledReason));
    }

    /// <summary>On a solver change: a solver with no plane-wave source runs the setup by its ports (the 3D view disables its wave in
    /// the same edit), so the panel stops showing the wave.</summary>
    private void DropPlaneWaveIfSolverCannot()
    {
        if (Working.PlaneWave is null || Em3dPlaneWaves.SolverCannot(Working.Solver3D) is null) return;
        Working.PlaneWave = null;
        SyncPlaneWaveFields();
    }

    private void SyncPlaneWaveFields()
    {
        var w = Working.PlaneWave;
        ExcitationNote = w is null ? null
            : $"Driven by the 3D view's plane wave '{w.Name ?? "plane wave"}', not by its ports. The wave is edited on its row under " +
              "Excitations in the 3D view.";
        RaisePlaneWaveVisibility();
    }

    /// <summary>The monostatic cross-section of a finished scattering run, for the panel.</summary>
    public void AdoptScattering(DataSet data)
        => RcsSummary = data.Groups.Contains(Em3dScatteringResult.Group) ? EmergeScattering.Headline(data) : null;
}
