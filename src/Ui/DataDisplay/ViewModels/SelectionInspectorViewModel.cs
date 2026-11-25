// ================================================================
//  SelectionInspectorViewModel.cs  —  the Properties panel for several items
//
//  With two or more things selected on a display (Select All, a marquee,
//  Ctrl-clicks) the panel shows what they have in common and sets it on
//  all of them at once: Lock Position on plots, controls and text, the
//  card on plots, controls and marker boxes, and — when only controls are
//  selected — their size, label and accent. A value they disagree on
//  shows as mixed. Each change is one undo step however many kinds of
//  item it reaches.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed partial class SelectionInspectorViewModel : ObservableObject
{
    private readonly DataDisplayViewModel _display;

    internal SelectionInspectorViewModel(DataDisplayViewModel display)
    {
        _display = display;
        display.SelectionChanged += Refresh;
        display.ControlChanged += _ => Refresh();
        display.UndoRedo.StateChanged += (_, _) => Refresh();
    }

    // ---- what is selected -----------------------------------------------------------------------------

    private List<PlotContainerViewModel> Plots => _display.Plots.Where(p => p.IsSelected).ToList();
    private List<ControlContainerViewModel> Controls => _display.Controls.Where(c => c.IsSelected).ToList();
    private List<MarkerInfoBoxViewModel> MarkerBoxes => _display.MarkerInfoBoxes.Where(b => b.IsSelected).ToList();
    private int LegendBoxCount => _display.LegendBoxes.Count(b => b.IsSelected);

    /// <summary>The selected annotations, by index: all of them after Select All, else the one selected.</summary>
    private List<int> Annotations
        => _display.AllAnnotationsSelected ? [.. Enumerable.Range(0, _display.Annotations.Count)]
         : _display.SelectedAnnotationIndex >= 0 && _display.SelectedAnnotationIndex < _display.Annotations.Count
             ? [_display.SelectedAnnotationIndex] : [];

    private int Count => Plots.Count + Controls.Count + MarkerBoxes.Count + LegendBoxCount + Annotations.Count;

    /// <summary>Shown while two or more things are selected; one is edited by its own inspector.</summary>
    public bool IsActive => Count >= 2;

    /// <summary>"5 selected: 2 plots, 3 controls".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(int n, string one, string many) { if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}"); }
            Add(Plots.Count, "plot", "plots");
            Add(Controls.Count, "control", "controls");
            Add(MarkerBoxes.Count, "marker box", "marker boxes");
            Add(LegendBoxCount, "legend", "legends");
            Add(Annotations.Count, "drawing", "drawings");
            return $"{Count} selected: {string.Join(", ", parts)}";
        }
    }

    internal void Refresh()
    {
        foreach (var name in AllProperties) OnPropertyChanged(name);
    }

    private static readonly string[] AllProperties =
    [
        nameof(IsActive), nameof(Summary),
        nameof(HasLock), nameof(Locked), nameof(HasCard), nameof(Card),
        nameof(OnlyControls), nameof(IsSmall), nameof(IsMedium), nameof(IsLarge), nameof(IsMixedSize),
        nameof(HasShowLabel), nameof(ShowLabel), nameof(HasAccent), nameof(AccentLabel), nameof(AccentSwatch),
        nameof(IsMixedAccent), nameof(IsDefaultAccent), nameof(AllButtons),
    ];

    /// <summary>All true → true, all false → false, else mixed.</summary>
    private static bool? Common(IEnumerable<bool> values)
    {
        bool any = false, all = true, seen = false;
        foreach (var v in values) { seen = true; any |= v; all &= v; }
        return !seen ? false : all ? true : any ? null : false;
    }

    // ---- Lock Position: plots, controls and text ------------------------------------------------------
    // Marker and legend boxes go with their plots, so they neither have it nor hide it.

    /// <summary>Every selected plot, control and drawing can be locked — a line, box or oval cannot.</summary>
    public bool HasLock
        => IsActive && Plots.Count + Controls.Count + Annotations.Count > 0
           && Annotations.All(i => _display.Annotations[i] is CanvasText);

    public bool? Locked
    {
        get => HasLock
            ? Common(Plots.Select(p => p.PlotVM.Plot.PositionLocked)
                     .Concat(Controls.Select(c => c.Model.Locked))
                     .Concat(Annotations.Select(i => ((CanvasText)_display.Annotations[i]).Locked)))
            : false;
        set
        {
            if (!HasLock) return;
            bool on = value ?? true;
            Apply(plot => plot.PositionLocked = on, null, c => c.Locked = on,
                  a => a is CanvasText t ? t with { Locked = on } : a, on ? "lock" : "unlock");
        }
    }

    // ---- Card: plots, controls and marker boxes -------------------------------------------------------
    // A panel is its card, and a legend's card is its plot's, so neither has a say.

    private List<ControlContainerViewModel> CardControls => Controls.Where(c => c.Model is not PanelControl).ToList();

    public bool HasCard
        => IsActive && Annotations.Count == 0 && Plots.Count + CardControls.Count + MarkerBoxes.Count > 0;

    public bool? Card
    {
        get => HasCard
            ? Common(Plots.Select(p => p.PlotVM.Plot.Card)
                     .Concat(CardControls.Select(c => c.Model.ShowCard))
                     .Concat(MarkerBoxes.Select(b => b.Marker.InfoBoxCard ?? true)))
            : false;
        set
        {
            if (!HasCard) return;
            bool on = value ?? true;
            var boxes = MarkerBoxes;
            Apply(plot => plot.Card = on, boxes.Select(b => (b.Container, (Action)(() => b.Marker.InfoBoxCard = on))),
                  c => { if (c is not PanelControl) c.ShowCard = on; }, null, "card");
        }
    }

    // ---- controls only ------------------------------------------------------------------------------

    /// <summary>Nothing but controls is selected: their own rows show.</summary>
    public bool OnlyControls
        => IsActive && Controls.Count == Count;

    private bool AllSize(ControlSize s) => OnlyControls && Controls.All(c => c.Model.Size == s);
    public bool IsSmall  => AllSize(ControlSize.Small);
    public bool IsMedium => AllSize(ControlSize.Medium);
    public bool IsLarge  => AllSize(ControlSize.Large);
    public bool IsMixedSize => OnlyControls && Controls.Select(c => c.Model.Size).Distinct().Count() > 1;

    [RelayCommand] private void SetSmall()  => SetSize(ControlSize.Small);
    [RelayCommand] private void SetMedium() => SetSize(ControlSize.Medium);
    [RelayCommand] private void SetLarge()  => SetSize(ControlSize.Large);

    private void SetSize(ControlSize size)
    {
        if (OnlyControls) Apply(null, null, c => c.ApplySize(size), null, "size");
    }

    /// <summary>Sliders, knobs and buttons have a label to hide.</summary>
    public bool HasShowLabel => OnlyControls && Controls.All(c => c.Model is SteppedControl or ButtonControl);

    public bool? ShowLabel
    {
        get => HasShowLabel
            ? Common(Controls.Select(c => c.Model is SteppedControl s ? s.ShowLabel : ((ButtonControl)c.Model).ShowLabel))
            : false;
        set
        {
            if (!HasShowLabel) return;
            bool on = value ?? true;
            Apply(null, null, c =>
            {
                if (c is SteppedControl s) s.ShowLabel = on;
                else if (c is ButtonControl b)
                {
                    b.ShowLabel = on;
                    if (b.Size != ControlSize.Custom) b.ApplySize(b.Size);   // the preset that fits with or without it
                }
            }, null, "label shown");
        }
    }

    /// <summary>Every control but a panel has an accent.</summary>
    public bool HasAccent => OnlyControls && Controls.All(c => c.Model is not PanelControl);

    public bool AllButtons => HasAccent && Controls.All(c => c.Model is ButtonControl);

    public string AccentLabel => AllButtons ? "Colour" : "Accent";

    private IEnumerable<SkiaSharp.SKColor> Accents => Controls.Select(c => ControlRenderer.AccentOf(c.Model));

    public bool IsMixedAccent => HasAccent && Accents.Distinct().Count() > 1;

    /// <summary>The colour they share; transparent while they differ.</summary>
    public IBrush AccentSwatch
    {
        get
        {
            if (!HasAccent || IsMixedAccent) return Brushes.Transparent;
            var c = Accents.First();
            return new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        }
    }

    public bool IsDefaultAccent => HasAccent && Controls.All(c => c.Model.Accent is null);

    /// <summary>The colour the picker opens on: the shared one, else the first control's.</summary>
    internal Color PickerStart
    {
        get
        {
            var c = HasAccent ? Accents.First() : ControlRenderer.DefaultAccent;
            return Color.FromRgb(c.Red, c.Green, c.Blue);
        }
    }

    /// <summary>Sets every control's accent, <c>#rrggbb</c>, or back to its default with null.</summary>
    internal void SetAccent(Color? color)
    {
        if (!HasAccent) return;
        string? hex = color is { } c ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : null;
        Apply(null, null, m => m.Accent = hex, null, "accent");
    }

    public IReadOnlyList<ColorPreset> ColorPresets { get; } =
        [.. ButtonControl.Presets.Select(h => new ColorPreset(h, new SolidColorBrush(Color.Parse(h))))];

    [RelayCommand]
    private void PickPreset(ColorPreset? preset)
    {
        if (preset is not null) SetAccent(Color.Parse(preset.Hex));
    }

    // ---- one undo step ---------------------------------------------------------------------------------

    /// <summary>
    /// Applies an edit to every selected plot, control and drawing it concerns, as ONE undo step. A plot is edited
    /// through its saved form, so the step holds what the display's own plot history would: before and after.
    /// </summary>
    private void Apply(Action<Plot>? plotEdit,
                       IEnumerable<(PlotContainerViewModel Plot, Action Edit)>? plotPartEdits,
                       Action<DisplayControl>? controlEdit,
                       Func<CanvasAnnotation, CanvasAnnotation>? annotationEdit,
                       string what)
    {
        _display.CheckpointEdits();   // whatever was edited before is its own step
        var commands = new List<IUndoableCommand>();

        // Plots: their own edit and any made to something they hold (a marker's box).
        var plotSet = new HashSet<PlotContainerViewModel>(plotEdit is not null ? Plots : []);
        var parts = plotPartEdits?.ToList() ?? [];
        foreach (var (p, _) in parts) plotSet.Add(p);
        if (plotSet.Count > 0)
        {
            var before = plotSet.ToDictionary(p => p, _display.SavedFormOf);
            if (plotEdit is not null) foreach (var p in Plots) plotEdit(p.PlotVM.Plot);
            foreach (var (_, edit) in parts) edit();
            var after = plotSet.ToDictionary(p => p, _display.SavedFormOf);
            var changed = plotSet.Where(p => before[p] != after[p]).ToList();
            foreach (var p in changed) _display.RestorePlot(p, after[p]);   // the canvas, boxes and inspector follow
            if (changed.Count > 0)
                commands.Add(new PlotEditCommand(_display, changed.ToDictionary(p => p, p => before[p]),
                                                 changed.ToDictionary(p => p, p => after[p]), "selection." + what));
        }

        if (controlEdit is not null)
            foreach (var c in Controls)
            {
                var before = c.Model.Clone();
                controlEdit(c.Model);
                var after = c.Model.Clone();
                if (System.Text.Json.JsonSerializer.Serialize(before, DataDisplayJson.Options)
                    == System.Text.Json.JsonSerializer.Serialize(after, DataDisplayJson.Options)) continue;
                commands.Add(new EditControlCommand(c, before, after));
                c.Changed();
            }

        if (annotationEdit is not null)
            foreach (int i in Annotations)
            {
                var was = _display.Annotations[i];
                var now = annotationEdit(was);
                if (now == was) continue;
                commands.Add(new EditAnnotationCommand(_display, i, was, now));
                _display.ReplaceAnnotation(i, now);
            }

        _display.RebaseEditHistory();
        if (commands.Count > 0)
        {
            Gesture.Note("selection." + what, $"{commands.Count}");
            _display.UndoRedo.Push(commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
        }
        Refresh();
    }
}
