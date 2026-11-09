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

    /// <summary>Adds a single point at the first segment's start, as the EM setup's plan does.</summary>
    [RelayCommand]
    private void AddSegment()
    {
        var first = Segments[0].Build();
        AddSegmentInternal(new FrequencySpecViewModel(_model,
            new FrequencySpec(first.StartExpr, first.StartExpr, 1, SweepKind.Linear, first.StartUnit, first.StartUnit)));
    }

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

    /// <summary>The Noise check box: <c>Noise=yes</c>, the small-signal noise cubes beside S.</summary>
    [ObservableProperty] private bool _noise;

    /// <summary><c>Noise=</c> as the box has it: the loaded analysis's own spelling while the box still says what it
    /// said, else <c>yes</c> or <c>no</c>.</summary>
    public string NoiseExpr => Source is { } s && s.Noise == Noise ? s.NoiseExpr : Noise ? "yes" : Analysis.NoiseDefault;

    /// <summary>The per-part box under it: <c>NoiseContrib=yes</c>, each part's share of <c>Cs</c> as <c>NCs</c>. Only
    /// meaningful with <see cref="Noise"/> on, which is when the box is enabled.</summary>
    [ObservableProperty] private bool _noiseContrib;

    /// <summary><c>NoiseContrib=</c> as the box has it, keeping the loaded spelling while the box is unchanged.</summary>
    public string NoiseContribExpr => Source is { } s && s.NoiseContrib == NoiseContrib
        ? s.NoiseContribExpr : NoiseContrib ? "yes" : Analysis.NoiseContribDefault;

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    /// <summary>The analysis this body was loaded from, or null for a new one — what an edit carries its other keys from.</summary>
    public SParameterAnalysis? Source { get; private set; }

    public static SpBodyViewModel FromAnalysis(SParameterAnalysis sp, SchematicEditModel model)
    {
        var vm = new SpBodyViewModel(model) { Source = sp, Noise = sp.Noise, NoiseContrib = sp.NoiseContrib };
        vm.Segments.Clear();
        foreach (var sweep in sp.Sweeps)
            vm.AddSegmentInternal(new FrequencySpecViewModel(model, sweep));
        return vm;
    }
}
