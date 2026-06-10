using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Body VM for S-Parameter analysis (analysis-authoring.md §4.2 L2).
/// Owns an ordered collection of FrequencySpecViewModels (sweep segments).
/// Always contains at least one segment; RemoveSegment is disabled when Count == 1.
/// BuildSweeps() maps the segments to FrequencySpec objects for SParameterAnalysis.
/// </summary>
public sealed partial class SpBodyViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;

    /// <summary>Ordered list of sweep segments. Always ≥ 1 entry.</summary>
    public ObservableCollection<FrequencySpecViewModel> Segments { get; } = [];

    public SpBodyViewModel(SchematicEditModel model)
    {
        _model = model;
        Segments.CollectionChanged += OnSegmentsChanged;
        AddSegmentInternal(new FrequencySpecViewModel(model));
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshCanRemove();
        RemoveSegmentCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCanRemove()
    {
        bool canRemove = Segments.Count > 1;
        foreach (var s in Segments)
            s.CanRemoveSelf = canRemove;
    }

    // ── Add / Remove commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void AddSegment() => AddSegmentInternal(new FrequencySpecViewModel(_model));

    [RelayCommand(CanExecute = nameof(CanRemoveSegment))]
    private void RemoveSegment(FrequencySpecViewModel? seg)
    {
        if (seg is null || Segments.Count <= 1) return;
        Segments.Remove(seg);
    }

    private bool CanRemoveSegment(FrequencySpecViewModel? seg)
        => seg is not null && Segments.Count > 1;

    private void AddSegmentInternal(FrequencySpecViewModel seg)
    {
        seg.SetRemoveCallback(s =>
        {
            if (Segments.Count > 1)
                Segments.Remove(s);
        });
        Segments.Add(seg);
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<FrequencySpec> BuildSweeps()
        => Segments.Select(s => s.Build()).ToList();

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    public static SpBodyViewModel FromAnalysis(SParameterAnalysis sp, SchematicEditModel model)
    {
        var vm = new SpBodyViewModel(model);
        vm.Segments.Clear();
        foreach (var sweep in sp.Sweeps)
            vm.AddSegmentInternal(new FrequencySpecViewModel(model, sweep));
        return vm;
    }
}
