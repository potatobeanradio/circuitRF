using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>One row of a conductor card's Resistive films section. It stages text and calls the view model's
/// one write path; it writes no <see cref="StackupFilm"/> itself. The drawing's inline editor stages and commits
/// through this same row, which is what makes the two the same edit.</summary>
public sealed partial class StackupFilmRowViewModel : ObservableObject
{
    private readonly TechEditorViewModel _owner;
    private bool _refreshing;

    public LayerKey Key { get; }
    public string LayerName { get; private set; } = "";
    public string ThicknessUnitSuffix => LayoutUnits.Suffix(_owner.Working.DefaultDisplayUnit);
    public IRelayCommand RemoveCommand { get; }

    [ObservableProperty] private string _stagedSheetResistance = "";
    [ObservableProperty] private string _stagedThickness = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isSelected;

    public bool HasError => Error is not null;
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public static IReadOnlyList<ConductorSheetSurface> FaceChoices { get; } =
        [ConductorSheetSurface.Top, ConductorSheetSurface.Bottom];

    private ConductorSheetSurface _selectedFace;
    public ConductorSheetSurface SelectedFace
    {
        get => _selectedFace;
        set
        {
            if (!SetProperty(ref _selectedFace, value) || _refreshing) return;
            _owner.SetFilmFace(Key, value);
        }
    }

    public StackupFilmRowViewModel(LayerKey key, TechEditorViewModel owner)
    {
        Key = key;
        _owner = owner;
        RemoveCommand = new RelayCommand(() => owner.RemoveFilm(Key));
        Refresh();
    }

    public void Refresh()
    {
        var tech = _owner.Working;
        if (ResistiveFilms.Of(tech, Key) is not { } film || ResistiveFilms.ConductorOf(tech, Key) is not { } level) return;
        _refreshing = true;
        LayerName = tech.Layers.FirstOrDefault(l => l.Key == Key)?.Name ?? $"{Key.Layer}/{Key.Datatype}";
        OnPropertyChanged(nameof(LayerName));
        OnPropertyChanged(nameof(ThicknessUnitSuffix));
        StagedSheetResistance = CircuitRF.Render.StackupScene.FilmSheetResistanceText(film.SheetResistanceOhmPerSq);
        StagedThickness = film.ThicknessDbu is { } t
            ? LayoutUnits.Format(t, tech.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron,
                                 LayoutUnits.SpellDecimals(tech.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron))
            : "";
        SelectedFace = ResistiveFilms.FaceOf(film, level);
        OnPropertyChanged(nameof(SelectedFace));
        _refreshing = false;
    }

    public void CommitSheetResistance()
    {
        string? refusal = _owner.CommitFilmSheetResistance(Key, StagedSheetResistance);
        if (refusal is not null) Refresh();
        Error = refusal;
    }

    public void CommitThickness()
    {
        string? refusal = _owner.CommitFilmThickness(Key, StagedThickness);
        if (refusal is not null) Refresh();
        Error = refusal;
    }
}
