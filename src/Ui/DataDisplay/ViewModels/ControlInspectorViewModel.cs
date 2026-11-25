// ================================================================
//  ControlInspectorViewModel.cs  —  the Properties panel for one control
//  (brief-ddl-5 R-ddl5-1 … R-ddl5-3, R-ddl5-7, R-ddl5-9)
//
//  Edits the one selected control: its name and label, the slider's
//  domain (a range of its own, or one axis of a loaded source picked
//  through source → quantity → axis pickers), its orientation, size,
//  what it shows, its accent and Lock Position. Every committed change is
//  one ControlContainerViewModel.Edit, so it is one undo step and the
//  links that follow the control move with it. The inspector re-reads the
//  model whenever the control changes, which is what undo and redo need.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>A loaded source in the Bound pickers; <see cref="Entry"/> is null for a source that is no longer
/// loaded, which is listed so the picker still says what the slider is bound to.</summary>
public sealed record ControlSourceItem(DataSourceEntryViewModel? Entry, string Path, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A quantity or an axis in the Bound pickers. <see cref="Missing"/> marks the stored one when the source no
/// longer has it.</summary>
public sealed record ControlPickItem(string Value, string Text, bool Missing = false)
{
    public override string ToString() => Text;
}

public sealed partial class ControlInspectorViewModel : ObservableObject
{
    private readonly DataDisplayViewModel _display;
    private ControlContainerViewModel? _target;
    private DataSourceLibraryViewModel? _library;
    // Set while the pickers' lists and selections are being rebuilt: a ComboBox pushes null back through its
    // SelectedItem binding when its items are replaced, and that is not the user choosing anything.
    private bool _syncing;

    internal ControlInspectorViewModel(DataDisplayViewModel display)
    {
        _display = display;
        display.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataDisplayViewModel.SelectedControl)) Retarget();
            else if (e.PropertyName == nameof(DataDisplayViewModel.Library)) WatchLibrary();
        };
        display.ControlChanged += c => { if (ReferenceEquals(c, _target)) Refresh(); };
        WatchLibrary();
        Retarget();
    }

    // ---- target --------------------------------------------------------------------------------------

    internal ControlContainerViewModel? Target => _target;
    private DisplayControl? Model => _target?.Model;
    private SteppedControl? Slider => _target?.Model as SteppedControl;

    public bool IsActive => _target is not null;

    private void Retarget()
    {
        var now = _display.SelectedControl;
        if (ReferenceEquals(now, _target)) return;
        _target = now;
        NameError = null;
        FieldError = null;
        Refresh();
    }

    private void WatchLibrary()
    {
        if (_library is not null)
        {
            _library.LibraryChanged -= OnLibraryChanged;
            _library.Entries.CollectionChanged -= OnEntriesChanged;
        }
        _library = _display.Library;
        if (_library is not null)
        {
            _library.LibraryChanged += OnLibraryChanged;
            _library.Entries.CollectionChanged += OnEntriesChanged;
        }
        if (_target is not null) Refresh();
    }

    private void OnLibraryChanged(object? sender, EventArgs e) { if (_target is not null) Refresh(); }
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) { if (_target is not null) Refresh(); }

    private IPlotDataSources Sources => new LibraryDataSources(_display.Library);

    /// <summary>One undo step, then the inspector re-reads the model — also when nothing changed, so a refused or
    /// unchanged value puts the field back.</summary>
    private void Edit(Action<DisplayControl> edit, string what)
    {
        if (_target is not { } vm) return;
        // A refusal is about an entry the box has already put back; the next edit replaces it.
        NameError = null;
        FieldError = null;
        vm.Edit(edit, what);
        Refresh();
    }

    /// <summary>Re-reads every field from the model.</summary>
    internal void Refresh()
    {
        _syncing = true;
        try
        {
            RebuildPickers();
            foreach (var name in AllProperties) OnPropertyChanged(name);
        }
        finally { _syncing = false; }
    }

    /// <summary>A text field's value by its property name — what a box shows again after a refused entry.</summary>
    internal string? TextOf(string property) => property switch
    {
        nameof(NameText)   => NameText,
        nameof(LabelText)  => LabelText,
        nameof(StartText)  => StartText,
        nameof(StopText)   => StopText,
        nameof(StepText)   => StepText,
        nameof(PointsText) => PointsText,
        _ => null,
    };

    private static readonly string[] AllProperties =
    [
        nameof(IsActive), nameof(NameText), nameof(LabelText),
        nameof(IsRange), nameof(IsBound),
        nameof(StartText), nameof(StopText), nameof(StepText), nameof(PointsText), nameof(UnitText), nameof(Log),
        nameof(StepEnabled), nameof(StepPlaceholder),
        nameof(SelectedSource), nameof(SelectedQuantity), nameof(SelectedAxis), nameof(Problem), nameof(HasProblem),
        nameof(IsHorizontal), nameof(IsVertical),
        nameof(IsSmall), nameof(IsMedium), nameof(IsLarge), nameof(IsCustom),
        nameof(ShowLabel), nameof(ShowTicks), nameof(ShowEnds), nameof(ShowValue),
        nameof(AccentSwatch), nameof(IsDefaultAccent), nameof(Locked),
        nameof(IsStepped), nameof(IsSlider), nameof(IsButton), nameof(IsReadout), nameof(IsPanel), nameof(IsNotPanel),
        nameof(ShowCard),
        nameof(IsToggle), nameof(IsMomentary), nameof(IsLabelUnder), nameof(IsLabelOnFace),
        nameof(IsLamp), nameof(IsSwitch), nameof(HasOrientation),
        nameof(SelectedReadoutSource), nameof(IsShowValue), nameof(IsShowCoordinate), nameof(IsShowReading),
        nameof(SelectedReadoutAxis), nameof(ReadoutAxisEnabled), nameof(ReadoutShowName), nameof(AccentLabel),
    ];

    // ---- name and label --------------------------------------------------------------------------------

    public string NameText
    {
        get => Model?.Name ?? "";
        set
        {
            if (Model is not { } m) return;
            string name = (value ?? "").Trim();
            if (name == m.Name) { NameError = null; OnPropertyChanged(); return; }
            if (NameRefusal(name) is { } why)
            {
                FieldError = null;
                NameError = why;
                OnPropertyChanged();   // the box goes back to the name the control keeps
                return;
            }
            NameError = null;
            Edit(c => c.Name = name, "name");
        }
    }

    /// <summary>Why a name was refused — on the problem line until the next edit or selection.</summary>
    [ObservableProperty] private string? _nameError;

    /// <summary>Why a number was refused — on the problem line until the next edit or selection.</summary>
    [ObservableProperty] private string? _fieldError;

    partial void OnNameErrorChanged(string? value)  { OnPropertyChanged(nameof(Problem)); OnPropertyChanged(nameof(HasProblem)); }
    partial void OnFieldErrorChanged(string? value) { OnPropertyChanged(nameof(Problem)); OnPropertyChanged(nameof(HasProblem)); }

    /// <summary>A refused number: said on the problem line, and the box shows the model's value again.</summary>
    private void Refuse(string why)
    {
        NameError = null;
        FieldError = why;
        Refresh();
    }

    /// <summary>
    /// Null when <paramref name="name"/> may name this control: not empty, spelled so <c>@name</c> parses in a slice and
    /// in an expression, and not already a marker's or another control's anywhere in the document — a link names its
    /// source by that name.
    /// </summary>
    internal string? NameRefusal(string name)
    {
        if (name.Length == 0) return "A name is needed.";
        if (!char.IsLetter(name[0]) || !name.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            return "A name takes letters, digits and _ only.";
        var doc = _display.CurrentLinkDocument();
        if (doc.Markers().Any(s => string.Equals(s.Marker.Name, name, StringComparison.Ordinal)))
            return $"{name} is a marker's name.";
        var controls = _display.DocumentControlsProvider?.Invoke() ?? _display.Controls.Select(c => c.Model);
        if (controls.Any(c => !ReferenceEquals(c, Model) && string.Equals(c.Name, name, StringComparison.Ordinal)))
            return $"{name} is taken.";
        return null;
    }

    public string LabelText
    {
        get => Model?.Label ?? "";
        set
        {
            if (Model is null) return;
            string? label = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Edit(c => c.Label = label, "label");
        }
    }

    // ---- domain ----------------------------------------------------------------------------------------

    public bool IsRange => Slider is { Domain: SliderDomain.Range };
    public bool IsBound => Slider is { Domain: SliderDomain.Bound };

    [RelayCommand]
    private void SetRange()
    {
        if (Slider is not { Domain: SliderDomain.Bound }) return;
        Edit(c =>
        {
            var s = (SteppedControl)c;
            // A range taken from a frequency axis is written in the unit the control reads frequencies in.
            if (s.Unit.Length == 0 && SiUnit.Split(s.DisplayUnit).Base == "Hz" && s.FrequencyUnit is { Length: > 0 } fu)
                s.Unit = fu;
            double v = Convert(s.CurrentValue, s.DisplayUnit, s.Unit);
            s.Domain = SliderDomain.Range;
            s.Value = v;
            s.Value = s.Stops[s.StopIndex];
        }, "domain");
    }

    /// <summary><paramref name="v"/> in <paramref name="from"/> written in <paramref name="to"/>, when the two share a
    /// base unit (MHz and GHz); otherwise the number as it is.</summary>
    internal static double Convert(double v, string? from, string? to)
    {
        var (sf, bf) = SiUnit.Split(from);
        var (st, bt) = SiUnit.Split(to);
        return bf == bt ? v * sf / st : v;
    }

    /// <summary>Bound: when nothing is bound yet, the library's selected source (else its first) and its first quantity
    /// and axis are taken, so the slider has stops at once and the pickers show what to change.</summary>
    [RelayCommand]
    private void SetBound()
    {
        if (Slider is not { Domain: SliderDomain.Range }) return;
        Edit(c =>
        {
            var s = (SteppedControl)c;
            double v = s.CurrentValue;
            string unit = s.Unit;
            s.Domain = SliderDomain.Bound;
            if (s.SourcePath is null && DefaultEntry() is { FilePath: { } path } entry)
            {
                s.SourcePath = SourceKey(path);
                BindFirst(s, entry);
            }
            ControlLinkSource.Refresh(s, Sources);
            // The stop nearest the value it had, so a switch does not jump the slider to its first stop.
            if (s.BoundValues is { Length: > 0 } stops)
                s.Index = SteppedControl.NearestStop(stops, Convert(v, unit, s.BoundUnit));
        }, "domain");
    }

    private DataSourceEntryViewModel? DefaultEntry()
    {
        var loaded = _library?.Entries.Where(e => !e.IsBroken && e.FilePath is not null).ToList() ?? [];
        return loaded.FirstOrDefault(e => ReferenceEquals(e, _library!.SelectedEntry)) ?? loaded.FirstOrDefault();
    }

    /// <summary>How a control stores its source: as a trace stores one — relative to the results folder when the
    /// file is under it, else absolute. Never the "selected source" sentinel: a control is bound to one file.</summary>
    private string SourceKey(string absPath) => DataDisplayViewModel.ComputeSourceKey(absPath, _library);

    // ---- range -----------------------------------------------------------------------------------------

    private static string Num(double v) => v.ToString("G10", CultureInfo.InvariantCulture);

    private static bool TryNum(string? text, out double v)
    {
        text = (text ?? "").Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
    }

    /// <summary>A range edit, then the value onto the nearest of the new stops, so what is saved is what is shown.</summary>
    private void EditRange(Action<SteppedControl> edit, string what)
        => Edit(c =>
        {
            var s = (SteppedControl)c;
            edit(s);
            s.Value = s.Stops[s.StopIndex];
        }, what);

    public string StartText
    {
        get => Slider is { } s ? Num(s.Start) : "";
        set
        {
            if (Slider is null) return;
            if (TryNum(value, out var v) && double.IsFinite(v)) EditRange(s => s.Start = v, "start");
            else Refuse("Start must be a number.");
        }
    }

    public string StopText
    {
        get => Slider is { } s ? Num(s.Stop) : "";
        set
        {
            if (Slider is null) return;
            if (TryNum(value, out var v) && double.IsFinite(v)) EditRange(s => s.Stop = v, "stop");
            else Refuse("Stop must be a number.");
        }
    }

    /// <summary>The spacing; 0 is continuous. A point count governs instead when there is one, and the box then shows
    /// the step it gives, greyed; a log range has no single step and the box is empty.</summary>
    public string StepText
    {
        get => Slider switch
        {
            null => "",
            { Log: true } => "",
            { Points: int n } s when n > 1 => Num((s.Stop - s.Start) / (n - 1)),
            var s => Num(s.Step),
        };
        set
        {
            if (Slider is not { } slider || !StepEnabled) return;
            if (TryNum(value, out var v) && double.IsFinite(v) && v >= 0)
            {
                if (v != slider.Step) EditRange(s => s.Step = v, "step");
            }
            else Refuse("Step must be a number, 0 or more.");
        }
    }

    /// <summary>The step is typed only while no point count governs.</summary>
    public bool StepEnabled => Slider is { Log: false, Points: null };

    public string StepPlaceholder => Slider is { Log: true } ? "log" : "";

    /// <summary>Blank uses the step. Two or more otherwise; a log range keeps one.</summary>
    public string PointsText
    {
        get => Slider?.Points is { } p ? p.ToString(CultureInfo.InvariantCulture) : "";
        set
        {
            if (Slider is not { } slider) return;
            string t = (value ?? "").Trim();
            if (t.Length == 0)
            {
                if (slider.Log) Refuse("A log range needs a point count.");
                else EditRange(s => s.Points = null, "points");
                return;
            }
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 2 && n <= SteppedControl.ContinuousStops)
                EditRange(s => s.Points = n, "points");
            else Refuse($"Points must be a whole number from 2 to {SteppedControl.ContinuousStops}.");
        }
    }

    /// <summary>The units offered; any other may be typed.</summary>
    public IReadOnlyList<string> UnitChoices { get; } =
        ["Hz", "kHz", "MHz", "GHz", "dBm", "dB", "W", "mW", "V", "mV", "A", "mA", "Ω", "°", "s", "ns"];

    public string UnitText
    {
        get => Slider?.Unit ?? "";
        set
        {
            if (Slider is null) return;
            string unit = (value ?? "").Trim();
            Edit(c => ((SteppedControl)c).Unit = unit, "unit");
        }
    }

    /// <summary>Log spacing needs a point count, so turning it on with none gives eleven.</summary>
    public bool Log
    {
        get => Slider?.Log ?? false;
        set
        {
            if (Slider is null) return;
            EditRange(s =>
            {
                s.Log = value;
                if (value && s.Points is null) s.Points = 11;
            }, "log");
        }
    }

    // ---- bound: source → quantity → axis ---------------------------------------------------------------

    public ObservableCollection<ControlSourceItem> SourceItems   { get; } = new();
    public ObservableCollection<ControlPickItem>   QuantityItems { get; } = new();
    public ObservableCollection<ControlPickItem>   AxisItems     { get; } = new();

    private ControlSourceItem? _selectedSource;
    private ControlPickItem? _selectedQuantity, _selectedAxis;

    public ControlSourceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (_syncing || value is null || Equals(value, _selectedSource)) return;
            if (value.Entry is not { FilePath: { } path } entry) { Refresh(); return; }
            Edit(c =>
            {
                var s = (SteppedControl)c;
                s.SourcePath = SourceKey(path);
                // The quantity and the axis carry over when the new source has them; otherwise its first.
                var quantities = QuantitiesOf(entry.Data);
                if (quantities.FirstOrDefault(q => q.Name == s.CubeName) is { Cube: { } same }
                    && LinkableAxes(same).Any(a => a.Name == s.AxisName))
                {
                    s.Index = 0;
                }
                else BindFirst(s, entry);
                ControlLinkSource.Refresh(s, Sources);
            }, "source");
        }
    }

    public ControlPickItem? SelectedQuantity
    {
        get => _selectedQuantity;
        set
        {
            if (_syncing || value is null || value.Missing || Equals(value, _selectedQuantity)) return;
            if (CurrentEntry() is not { } entry
                || QuantitiesOf(entry.Data).FirstOrDefault(q => q.Name == value.Value) is not { Cube: { } cube })
            { Refresh(); return; }
            Edit(c =>
            {
                var s = (SteppedControl)c;
                s.CubeName = value.Value;
                var axes = LinkableAxes(cube);
                if (axes.All(a => a.Name != s.AxisName)) s.AxisName = axes.FirstOrDefault()?.Name;
                s.Index = 0;
                ControlLinkSource.Refresh(s, Sources);
            }, "quantity");
        }
    }

    public ControlPickItem? SelectedAxis
    {
        get => _selectedAxis;
        set
        {
            if (_syncing || value is null || value.Missing || Equals(value, _selectedAxis)) return;
            Edit(c =>
            {
                var s = (SteppedControl)c;
                s.AxisName = value.Value;
                s.Index = 0;
                ControlLinkSource.Refresh(s, Sources);
            }, "axis");
        }
    }

    /// <summary>
    /// The one problem line: a refused entry first, then why the bound axis cannot be read now — or, on a range, what
    /// about it is not what it says.
    /// </summary>
    public string? Problem => NameError ?? FieldError ?? (Model as ReadoutControl)?.Problem ?? Slider switch
    {
        { Domain: SliderDomain.Bound } s => ControlLinkSource.Problem(s, Sources),
        { Log: true } s when s.Start <= 0 || s.Stop <= 0 => "A log range needs a start and stop above 0.",
        { Log: false, Points: null } s when s.Step > 0 && Math.Abs(s.Stop - s.Start) / s.Step + 1 > SteppedControl.ContinuousStops + 1e-9
            => $"Step too fine: {SteppedControl.ContinuousStops} stops shown.",
        { } s when s.Start > s.Stop => "Start is above stop: the slider runs backwards.",
        _ => null,
    };

    public bool HasProblem => Problem is not null;

    /// <summary>The first quantity of <paramref name="entry"/> with a linkable axis, and that axis.</summary>
    private static void BindFirst(SteppedControl s, DataSourceEntryViewModel entry)
    {
        var first = QuantitiesOf(entry.Data).FirstOrDefault();
        s.CubeName = first.Name;
        s.AxisName = first.Cube is null ? null : LinkableAxes(first.Cube).FirstOrDefault()?.Name;
        s.Index = 0;
        if (first.Cube is null) { s.BoundValues = null; s.BoundUnit = null; }
    }

    private DataSourceEntryViewModel? CurrentEntry()
    {
        if (Slider?.SourcePath is not { } sp || _library is null) return null;
        string? abs = _library.ResolveAbs(sp);
        return abs is null ? null
            : _library.Entries.FirstOrDefault(e => string.Equals(e.FilePath, abs, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The quantities a slider can be bound to, named as a trace names them — bare in the default and measurements
    /// groups, <c>HB1.V</c> in an analysis group — leaving out what the trace picker leaves out (bookkeeping cubes, solver
    /// diagnostics) and any cube with no axis a link can follow. A Touchstone file's <c>S</c> stays: its frequency axis
    /// is a good thing to step.
    /// </summary>
    internal static List<(string Name, DataCube? Cube)> QuantitiesOf(DataSet? ds)
    {
        var list = new List<(string, DataCube?)>();
        if (ds is null) return list;
        foreach (var group in ds.Groups)
            foreach (var (bare, cube) in ds.CubesIn(group))
            {
                if (bare is "Z0" or "ToneFreqs" or "MetaMixOrder" || bare.StartsWith("__", StringComparison.Ordinal)) continue;
                if (bare.EndsWith("Converged", StringComparison.Ordinal) || bare.EndsWith("Residual", StringComparison.Ordinal)) continue;
                if (bare is "I" or "INl" && cube.Axes.Any(a => a.Name == "node")) continue;
                if (cube.Rank == 0 || LinkableAxes(cube).Count == 0) continue;
                string name = group == DataSet.DefaultGroup || group == DataSet.MeasurementsGroup ? bare : $"{group}.{bare}";
                list.Add((name, cube));
            }
        return list;
    }

    internal static List<Axis> LinkableAxes(DataCube cube)
        => [.. cube.Axes.Where(a => SliceTokenParser.IsLinkableAxis(a.Name, a.Labels, a.Length))];

    private static string AxisText(Axis a)
        => a.Unit is { Length: > 0 } u ? $"{a.Name} ({a.Length}, {u})" : $"{a.Name} ({a.Length})";

    private void RebuildPickers()
    {
        var sources = new List<ControlSourceItem>();
        foreach (var e in _library?.Entries ?? [])
            if (!e.IsBroken && e.FilePath is { } p)
                sources.Add(new ControlSourceItem(e, p, new PickerSourceItem(e).DisplayText));

        var quantities = new List<ControlPickItem>();
        var axes = new List<ControlPickItem>();
        ControlSourceItem? source = null;
        ControlPickItem? quantity = null, axis = null;

        if (Slider is { } s)
        {
            var entry = CurrentEntry();
            source = sources.FirstOrDefault(i => ReferenceEquals(i.Entry, entry));
            if (source is null && s.SourcePath is { } sp)
            {
                // Bound to a file that is not loaded: listed, so the picker still names it.
                string abs = _library?.ResolveAbs(sp) ?? sp;
                source = new ControlSourceItem(null, abs, $"{Path.GetFileNameWithoutExtension(abs)} (not loaded)");
                sources.Add(source);
            }

            List<(string Name, DataCube? Cube)> cubes = entry is null ? [] : QuantitiesOf(entry.Data);
            quantities.AddRange(cubes.Select(q => new ControlPickItem(q.Name, q.Name)));
            // The stored name may be another spelling of a listed cube — "SP1.S" reaches a flat file's "S" — so the
            // item is the one holding the cube the name resolves to.
            DataCube? cube = null;
            if (s.CubeName is { } cn)
            {
                var ds = entry?.Data;
                cube = ds is not null && ds.Contains(cn) ? ds[cn] : null;
                int at = cubes.FindIndex(q => q.Name == cn || (cube is not null && ReferenceEquals(q.Cube, cube)));
                quantity = at >= 0 ? quantities[at] : null;
                if (quantity is null) quantities.Add(quantity = new ControlPickItem(cn, $"{cn} (missing)", Missing: true));
            }

            if (cube is not null)
                axes.AddRange(LinkableAxes(cube).Select(a => new ControlPickItem(a.Name, AxisText(a))));
            if (s.AxisName is { } an)
            {
                axis = axes.FirstOrDefault(a => a.Value == an);
                if (axis is null) axes.Add(axis = new ControlPickItem(an, $"{an} (missing)", Missing: true));
            }
        }

        // Items first, then the selection, in this one path (src/Ui/CLAUDE.md): a selection set against the old list
        // is silently cleared.
        Replace(SourceItems, sources);
        Replace(QuantityItems, quantities);
        Replace(AxisItems, axes);
        _selectedSource = source;
        _selectedQuantity = quantity;
        _selectedAxis = axis;
        RebuildReadoutPickers();
    }

    /// <summary>Replaces the list's contents only when they differ, so an open drop-down is not rebuilt under the
    /// pointer by an unrelated change.</summary>
    private static void Replace<T>(ObservableCollection<T> target, List<T> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ---- orientation and size --------------------------------------------------------------------------

    public bool IsHorizontal => Model is SliderControl { Orientation: ControlOrientation.Horizontal }
                                    or ButtonControl { IsSwitch: true, Orientation: ControlOrientation.Horizontal };
    public bool IsVertical   => Model is SliderControl { Orientation: ControlOrientation.Vertical }
                                    or ButtonControl { IsSwitch: true, Orientation: ControlOrientation.Vertical };

    /// <summary>The Orientation row: a slider's, and a switch's.</summary>
    public bool HasOrientation => Model is SliderControl or ButtonControl { IsSwitch: true };

    [RelayCommand] private void SetHorizontal() => SetOrientation(ControlOrientation.Horizontal);
    [RelayCommand] private void SetVertical()   => SetOrientation(ControlOrientation.Vertical);

    /// <summary>A preset is re-applied, so the frame turns with the slider; a custom frame swaps its sides.</summary>
    private void SetOrientation(ControlOrientation o)
    {
        if (Model is ButtonControl { IsSwitch: true } sw)
        {
            if (sw.Orientation == o) return;
            Edit(c =>
            {
                var x = (ButtonControl)c;
                x.Orientation = o;
                if (x.Size == ControlSize.Custom) (x.Width, x.Height) = (x.Height, x.Width);
                else x.ApplySize(x.Size);
            }, "orientation");
            return;
        }
        if (Model is not SliderControl slider || slider.Orientation == o) return;
        Edit(c =>
        {
            var s = (SliderControl)c;
            s.Orientation = o;
            if (s.Size == ControlSize.Custom) (s.Width, s.Height) = (s.Height, s.Width);
            else s.ApplySize(s.Size);
        }, "orientation");
    }

    public bool IsSmall  => Model is { Size: ControlSize.Small };
    public bool IsMedium => Model is { Size: ControlSize.Medium };
    public bool IsLarge  => Model is { Size: ControlSize.Large };
    public bool IsCustom => Model is { Size: ControlSize.Custom };

    [RelayCommand] private void SetSmall()  => SetSize(ControlSize.Small);
    [RelayCommand] private void SetMedium() => SetSize(ControlSize.Medium);
    [RelayCommand] private void SetLarge()  => SetSize(ControlSize.Large);

    private void SetSize(ControlSize size) => Edit(c => c.ApplySize(size), "size");

    // ---- look ------------------------------------------------------------------------------------------

    /// <summary>The name or label drawn in the control: a slider's, a knob's or a button's.</summary>
    public bool ShowLabel
    {
        get => Model switch { SteppedControl s => s.ShowLabel, ButtonControl b => b.ShowLabel, _ => false };
        set
        {
            if (Model is SteppedControl) Edit(c => ((SteppedControl)c).ShowLabel = value, "label shown");
            else if (Model is ButtonControl) Edit(c =>
            {
                var b = (ButtonControl)c;
                // A preset button gives the label's strip back, or takes it, as the label is hidden or shown.
                b.ShowLabel = value;
                if (b.Size != ControlSize.Custom) b.ApplySize(b.Size);
            }, "label shown");
        }
    }

    /// <summary>The card the control is drawn on (every kind but a panel, which IS a card).</summary>
    public bool ShowCard
    {
        get => Model?.ShowCard ?? true;
        set { if (Model is not null) Edit(c => c.ShowCard = value, value ? "card shown" : "card hidden"); }
    }

    public bool ShowTicks
    {
        get => Slider?.ShowTicks ?? false;
        set { if (Slider is not null) Edit(c => ((SteppedControl)c).ShowTicks = value, "ticks"); }
    }

    public bool ShowEnds
    {
        get => Slider?.ShowEnds ?? false;
        set { if (Slider is not null) Edit(c => ((SteppedControl)c).ShowEnds = value, "ends"); }
    }

    public bool ShowValue
    {
        get => Slider?.ShowValue ?? false;
        set { if (Slider is not null) Edit(c => ((SteppedControl)c).ShowValue = value, "readout"); }
    }

    /// <summary>The colour the control is drawn in — its own, else the default accent it falls back to.</summary>
    public IBrush AccentSwatch
    {
        get
        {
            var c = Model is { } m ? ControlRenderer.AccentOf(m) : ControlRenderer.DefaultAccent;
            return new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        }
    }

    public bool IsDefaultAccent => Model is { Accent: null };

    /// <summary>Sets the accent, <c>#rrggbb</c>, or back to the default with null.</summary>
    internal void SetAccent(Color? color)
    {
        if (Model is null) return;
        string? hex = color is { } c ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : null;
        Edit(m => m.Accent = hex, "accent");
    }

    /// <summary>Lock Position, as the control's context menu sets it (R-ddl5-9).</summary>
    public bool Locked
    {
        get => Model?.Locked ?? false;
        set { if (Model is not null) Edit(c => c.Locked = value, value ? "lock" : "unlock"); }
    }

    // ---- kinds (brief-ddl-6) ---------------------------------------------------------------------------

    /// <summary>A slider or a knob: the domain, range and show rows.</summary>
    public bool IsStepped => Model is SteppedControl;
    public bool IsSlider  => Model is SliderControl;
    public bool IsButton  => Model is ButtonControl;
    public bool IsReadout => Model is ReadoutControl;
    public bool IsPanel   => Model is PanelControl;
    /// <summary>Everything but a panel has an accent and a card to turn off.</summary>
    public bool IsNotPanel => Model is not null and not PanelControl;

    /// <summary>A button's colour is the light it gives; every other control's is its accent.</summary>
    public string AccentLabel => IsButton ? "Colour" : "Accent";

    /// <summary>The eight colours a button offers before any other (R-ddl6-2).</summary>
    public IReadOnlyList<ColorPreset> ColorPresets { get; } =
        [.. ButtonControl.Presets.Select(h => new ColorPreset(h, new SolidColorBrush(Color.Parse(h))))];

    [RelayCommand]
    private void PickPreset(ColorPreset? preset)
    {
        if (preset is not null) SetAccent(Color.Parse(preset.Hex));
    }

    // ---- the button ------------------------------------------------------------------------------------

    public bool IsToggle    => Model is ButtonControl { Mode: ButtonMode.Toggle };
    public bool IsMomentary => Model is ButtonControl { Mode: ButtonMode.Momentary };

    [RelayCommand] private void SetToggle()    => SetMode(ButtonMode.Toggle);
    [RelayCommand] private void SetMomentary() => SetMode(ButtonMode.Momentary);

    /// <summary>A momentary button is dark at rest, so becoming one puts it out.</summary>
    private void SetMode(ButtonMode mode)
    {
        if (Model is not ButtonControl b || b.Mode == mode) return;
        Edit(c =>
        {
            var x = (ButtonControl)c;
            x.Mode = mode;
            if (mode == ButtonMode.Momentary) x.On = false;
        }, "mode");
    }

    public bool IsLamp   => Model is ButtonControl { Style: ButtonStyle.Lamp };
    public bool IsSwitch => Model is ButtonControl { Style: ButtonStyle.Switch };

    [RelayCommand] private void SetLamp()   => SetStyle(ButtonStyle.Lamp);
    [RelayCommand] private void SetSwitch() => SetStyle(ButtonStyle.Switch);

    /// <summary>A lamp or a switch; a preset frame takes the new style's shape.</summary>
    private void SetStyle(ButtonStyle style)
    {
        if (Model is not ButtonControl b || b.Style == style) return;
        Edit(c =>
        {
            var x = (ButtonControl)c;
            x.Style = style;
            if (x.Size != ControlSize.Custom) x.ApplySize(x.Size);
        }, "style");
    }

    public bool IsLabelUnder  => Model is ButtonControl { LabelPlacement: ButtonLabelPlacement.Under };
    public bool IsLabelOnFace => Model is ButtonControl { LabelPlacement: ButtonLabelPlacement.OnFace };

    [RelayCommand] private void SetLabelUnder()  => SetPlacement(ButtonLabelPlacement.Under);
    [RelayCommand] private void SetLabelOnFace() => SetPlacement(ButtonLabelPlacement.OnFace);

    /// <summary>A preset frame follows the label: under it the frame is taller.</summary>
    private void SetPlacement(ButtonLabelPlacement placement)
    {
        if (Model is not ButtonControl b || b.LabelPlacement == placement) return;
        Edit(c =>
        {
            var x = (ButtonControl)c;
            x.LabelPlacement = placement;
            if (x.Size != ControlSize.Custom) x.ApplySize(x.Size);
        }, "label place");
    }

    // ---- the readout -----------------------------------------------------------------------------------

    /// <summary>Every source a readout can show: the markers and the input controls of the document, and the trial
    /// selection.</summary>
    public ObservableCollection<ControlPickItem> ReadoutSourceItems { get; } = new();

    /// <summary>The coordinates the readout's source supplies.</summary>
    public ObservableCollection<ControlPickItem> ReadoutAxisItems { get; } = new();

    private ControlPickItem? _selectedReadoutSource, _selectedReadoutAxis;

    private void RebuildReadoutPickers()
    {
        var sources = new List<ControlPickItem>();
        var axes = new List<ControlPickItem>();
        ControlPickItem? source = null, axis = null;
        if (Model is ReadoutControl r)
        {
            var doc = _display.CurrentLinkDocument();
            foreach (var site in doc.Markers())
                sources.Add(new ControlPickItem(site.Marker.Id.ToString("D"), $"{site.Marker.Name}  (marker)"));
            foreach (var (_, c) in doc.Controls())
                if (ControlLinkSource.IsSource(c)) sources.Add(new ControlPickItem(c.Id, $"{c.Name}  ({c.KindName})"));
            sources.Add(new ControlPickItem(LinkDocument.SelectionId, "selected  (the trials selected)"));
            source = sources.FirstOrDefault(i => string.Equals(i.Value, r.Source, StringComparison.OrdinalIgnoreCase));
            if (source is null && (r.Source ?? r.SourceName) is { } gone)
                sources.Add(source = new ControlPickItem(gone, $"{r.SourceName ?? gone} (gone)", Missing: true));

            if (r.Source is { } id && doc.FindMarker(id) is { } m)
                foreach (var c in MarkerLinkSource.Resolve(m, Sources).Coordinates)
                    axes.Add(new ControlPickItem(c.Axis, c.Unit.Length > 0 ? $"{c.Axis} ({c.Unit})" : c.Axis));
            if (r.Axis is { } an)
            {
                axis = axes.FirstOrDefault(a => a.Value == an);
                if (axis is null) axes.Add(axis = new ControlPickItem(an, $"{an} (missing)", Missing: true));
            }
        }
        Replace(ReadoutSourceItems, sources);
        Replace(ReadoutAxisItems, axes);
        _selectedReadoutSource = source;
        _selectedReadoutAxis = axis;
    }

    public ControlPickItem? SelectedReadoutSource
    {
        get => _selectedReadoutSource;
        set
        {
            if (_syncing || value is null || value.Missing || Equals(value, _selectedReadoutSource) || Model is not ReadoutControl) return;
            var doc = _display.CurrentLinkDocument();
            string name = doc.FindMarker(value.Value)?.Marker.Name ?? doc.FindControl(value.Value)?.Name ?? value.Value;
            Edit(c =>
            {
                var r = (ReadoutControl)c;
                r.Source = value.Value;
                r.SourceName = name;
                // A coordinate is the new source's own: its first, when the readout shows one.
                r.Axis = r.Show == ReadoutShow.Coordinate && doc.FindMarker(value.Value) is { } m
                    ? MarkerLinkSource.Resolve(m, Sources).Coordinates.FirstOrDefault()?.Axis : null;
            }, "source");
        }
    }

    public bool IsShowValue      => Model is ReadoutControl { Show: ReadoutShow.Value };
    public bool IsShowCoordinate => Model is ReadoutControl { Show: ReadoutShow.Coordinate };
    public bool IsShowReading    => Model is ReadoutControl { Show: ReadoutShow.Reading };

    [RelayCommand] private void SetShowValue()      => SetShow(ReadoutShow.Value);
    [RelayCommand] private void SetShowCoordinate() => SetShow(ReadoutShow.Coordinate);
    [RelayCommand] private void SetShowReading()    => SetShow(ReadoutShow.Reading);

    private void SetShow(ReadoutShow show)
    {
        if (Model is not ReadoutControl r || r.Show == show) return;
        Edit(c =>
        {
            var x = (ReadoutControl)c;
            x.Show = show;
            if (show == ReadoutShow.Coordinate && x.Axis is null && x.Source is { } id
                && _display.CurrentLinkDocument().FindMarker(id) is { } m)
                x.Axis = MarkerLinkSource.Resolve(m, Sources).Coordinates.FirstOrDefault()?.Axis;
        }, "show");
    }

    /// <summary>The coordinate picker is live only while the readout shows a coordinate.</summary>
    public bool ReadoutAxisEnabled => IsShowCoordinate;

    public ControlPickItem? SelectedReadoutAxis
    {
        get => _selectedReadoutAxis;
        set
        {
            if (_syncing || value is null || value.Missing || Equals(value, _selectedReadoutAxis) || Model is not ReadoutControl) return;
            Edit(c => ((ReadoutControl)c).Axis = value.Value, "coordinate");
        }
    }

    public bool ReadoutShowName
    {
        get => (Model as ReadoutControl)?.ShowName ?? false;
        set { if (Model is ReadoutControl) Edit(c => ((ReadoutControl)c).ShowName = value, "name shown"); }
    }
}

/// <summary>One of a button's preset colours, as the inspector's swatch row shows it.</summary>
public sealed record ColorPreset(string Hex, IBrush Swatch);
