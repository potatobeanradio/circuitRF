using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Resistive films on the Stackup tab (brief stackup-render 9): the selection and <b>the only code that writes
/// a <see cref="StackupFilm"/></b>. The card's film row, the drawing's inline editor, its drags and its context
/// menu all arrive here, so the parse, the refusal and the undo entry are one thing.
/// </summary>
public sealed partial class TechEditorViewModel
{
    /// <summary>
    /// The selected film, by the drawing layer that patterns it. Exclusive of
    /// <see cref="SelectedStackupLayerName"/>: setting either clears the other. By key for the reason that one
    /// is by name: a reference does not survive a committed edit.
    /// </summary>
    [ObservableProperty] private LayerKey? _selectedFilmLayerKey;

    partial void OnSelectedFilmLayerKeyChanged(LayerKey? value)
    {
        if (value is { } key)
        {
            SelectedStackupLayerName = null;
            // The conductor's card has to be there to scroll to, as for a band.
            if (ResistiveFilms.ConductorOf(Working, key) is { } level && !Matches(level.Name, StackupFilter.Trim()))
                StackupFilter = "";
        }
        SyncFilmSelection();
    }

    /// <summary>The card a selection in the drawing scrolls to: the selected entry's, or the selected film's conductor's.</summary>
    internal StackupLayerRowViewModel? StackupScrollTargetRow =>
        SelectedStackupLayerRow ?? (SelectedFilmLayerKey is { } key && ResistiveFilms.ConductorOf(Working, key) is { } level
            ? StackupLayers.FirstOrDefault(r => string.Equals(r.Layer.Name, level.Name, System.StringComparison.Ordinal))
            : null);

    /// <summary>The card of the conductor a film is on.</summary>
    internal StackupLayerRowViewModel StackupScrollTargetRowFor(LayerKey key)
    {
        string level = ResistiveFilms.ConductorOf(Working, key)!.Name;
        return StackupLayers.First(r => string.Equals(r.Layer.Name, level, System.StringComparison.Ordinal));
    }

    /// <summary>Selects what the drawing was clicked on: a film, an entry, or nothing.</summary>
    internal void SelectFromDrawing(string? layerName, LayerKey? film)
    {
        if (film is not null) { SelectedFilmLayerKey = film; return; }
        SelectedFilmLayerKey = null;
        SelectedStackupLayerName = layerName;
    }

    private void SyncFilmSelection()
    {
        if (SelectedFilmLayerKey is { } key && ResistiveFilms.Of(Working, key) is null)
        {
            SelectedFilmLayerKey = null;   // removed, or undone away
            return;
        }
        foreach (var row in StackupLayers)
            foreach (var film in row.FilmRows)
                film.IsSelected = film.Key == SelectedFilmLayerKey;
    }

    // ── What may be offered ───────────────────────────────────────────────────────────────────────

    /// <summary>Drawing layers that are neither metal nor already a film: what Add Resistor Film offers.</summary>
    internal IReadOnlyList<(LayerKey Key, string Name)> FilmLayerChoices()
    {
        var metal = new HashSet<LayerKey>(Working.Stackup.Layers.SelectMany(l => l.DrawingLayers));
        return [.. Working.Layers.Where(l => !metal.Contains(l.Key) && ResistiveFilms.Of(Working, l.Key) is null)
                                 .Select(l => (l.Key, l.Name))];
    }

    /// <summary>The conductors a film may move to, for the menu: every level the binding accepts but its own.</summary>
    internal IReadOnlyList<string> FilmMoveTargets(LayerKey key)
    {
        var own = ResistiveFilms.ConductorOf(Working, key);
        return [.. Working.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && !ReferenceEquals(l, own) && ResistorFilmBinding.CanPlaceOn(Working, key, l.Name, out _))
            .Select(l => l.Name)];
    }

    /// <summary>The level's name and whether the entry can have films added, for a conductor card.</summary>
    internal bool AcceptsFilms(StackupLayer level) => level is { Kind: StackupKind.Conductor, IsGroundReference: false };

    /// <summary>What a typed sheet resistance is worth, in Ω/sq, or null when it is not a positive number.</summary>
    internal static double? ParseSheetResistance(string? text)
    {
        string s = (text ?? "").Replace("Ω/sq", "", System.StringComparison.Ordinal).Trim();
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
               && v > 0 && double.IsFinite(v) ? v : null;
    }

    // ── The one write path ────────────────────────────────────────────────────────────────────────

    /// <summary>Sets R_s from typed text. Returns the refusal, or null when it took.</summary>
    internal string? CommitFilmSheetResistance(LayerKey key, string? text)
    {
        if (ParseSheetResistance(text) is not { } rs) return "Enter a positive sheet resistance, e.g. 50.";
        if (ResistiveFilms.Of(Working, key) is not { } film) return null;
        var before = SnapshotJson();
        film.SheetResistanceOhmPerSq = rs;
        CommitEdit(before, $"Set {NameOfLayer(key)} sheet resistance");
        return null;
    }

    /// <summary>Sets the thickness from typed text, in the display unit unless it states its own; empty clears
    /// it, which lets the 3D generator pick. Returns the refusal, or null when it took.</summary>
    internal string? CommitFilmThickness(LayerKey key, string? text)
    {
        if (ResistiveFilms.Of(Working, key) is not { } film) return null;
        long? value = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!LayoutUnits.TryParse(text, Working.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron, out var dbu) || dbu <= 0)
                return "Enter a positive length, e.g. 50 nm, or leave it empty.";
            value = dbu;
        }
        var before = SnapshotJson();
        film.ThicknessDbu = value;
        CommitEdit(before, $"Set {NameOfLayer(key)} thickness");
        return null;
    }

    /// <summary>Puts a film on a face of its band. A face it is already on writes nothing, whichever statement
    /// put it there, so a level's own default is not turned into a statement by a gesture that changed nothing.</summary>
    internal bool SetFilmFace(LayerKey key, ConductorSheetSurface face)
    {
        if (ResistiveFilms.Of(Working, key) is not { } film || ResistiveFilms.ConductorOf(Working, key) is not { } level) return false;
        if (ResistiveFilms.IsFilmLevel(level)) return false;   // no metal, so no face: the drag to the other face writes nothing
        if (ResistiveFilms.FaceOf(film, level) == face) return false;
        var before = SnapshotJson();
        film.At = face;
        CommitEdit(before, $"Put {NameOfLayer(key)} on the {(face == ConductorSheetSurface.Top ? "top" : "bottom")} of {level.Name}");
        return true;
    }

    /// <summary>Moves a film to another conductor in ONE edit: two would leave a state between them that is a
    /// film on no level or on two. Returns false when the binding refused it, and then nothing was written.</summary>
    internal bool MoveFilm(LayerKey key, string conductorName)
    {
        var before = SnapshotJson();
        if (!ResistorFilmBinding.Move(Working, key, conductorName)) return false;
        CommitEdit(before, $"Move {NameOfLayer(key)} to {conductorName}");
        return true;
    }

    /// <summary>Removes a film's entry and leaves its drawing layer an ordinary row.</summary>
    internal void RemoveFilm(LayerKey key)
    {
        var before = SnapshotJson();
        if (ResistorFilmBinding.Remove(Working, key)) CommitEdit(before, $"Remove resistor film {NameOfLayer(key)}");
    }

    /// <summary>Makes an existing drawing layer a film on <paramref name="conductorName"/>.</summary>
    internal bool AddFilmOnLayer(string conductorName, LayerKey key, double ohmPerSq)
    {
        if (!(ohmPerSq > 0) || !ResistorFilmBinding.CanPlaceOn(Working, key, conductorName, out _)
                            || ResistiveFilms.Of(Working, key) is not null) return false;
        var before = SnapshotJson();
        (ConductorNamed(conductorName).Films ??= []).Add(new StackupFilm { Layer = key, SheetResistanceOhmPerSq = ohmPerSq });
        CommitEdit(before, $"Add resistor film {NameOfLayer(key)} on {conductorName}");
        return true;
    }

    /// <summary>Creates the drawing layer AND the film in one edit. The layer comes from the Layers tab's own
    /// function, so its key, colour and z-order are that tab's.</summary>
    internal bool AddResistorFilm(string conductorName, string name, double ohmPerSq)
    {
        if (!(ohmPerSq > 0) || string.IsNullOrWhiteSpace(name)) return false;
        var level = Working.Stackup.Layers.FirstOrDefault(l => string.Equals(l.Name, conductorName, System.StringComparison.Ordinal));
        if (level is null || !AcceptsFilms(level)) return false;
        var before = SnapshotJson();
        var def = AppendLayer(name.Trim());
        (level.Films ??= []).Add(new StackupFilm { Layer = def.Key, SheetResistanceOhmPerSq = ohmPerSq });
        CommitEdit(before, $"New resistor film {def.Name} on {conductorName}");
        return true;
    }

    // ── A film level (TFR-V R-tfrv-6) ─────────────────────────────────────────────────────────────

    /// <summary>What the film dialog's drawing-layer picker ends with: a name is typed only when creating one.</summary>
    internal const string NewFilmLayerChoice = "New layer…";

    /// <summary>The picker's rows: the layers no metal or film uses, then <see cref="NewFilmLayerChoice"/>.</summary>
    internal IReadOnlyList<string> FilmLayerPickerRows()
        => [.. FilmLayerChoices().Select(c => c.Name), NewFilmLayerChoice];

    /// <summary>The entries a new film level may be inserted below, in stackup order: anything that is not a via.</summary>
    internal IReadOnlyList<string> FilmLevelInsertTargets()
        => [.. Working.Stackup.Layers.Where(l => l.Kind != StackupKind.Via).Select(l => l.Name)];

    /// <summary>Where the picker opens: the first place no conductor would touch the level, else the first dielectric.</summary>
    internal string? DefaultFilmLevelInsertTarget()
    {
        var layers = Working.Stackup.Layers.Where(l => l.Kind != StackupKind.Via).ToList();
        for (int i = 0; i + 1 < layers.Count; i++)
            if (layers[i].Kind == StackupKind.Dielectric && layers[i + 1].Kind == StackupKind.Dielectric) return layers[i].Name;
        return layers.FirstOrDefault(l => l.Kind == StackupKind.Dielectric)?.Name ?? layers.FirstOrDefault()?.Name;
    }

    /// <summary>A new film level below <paramref name="below"/> holding one film, in ONE edit: a level with no film yet would be
    /// a conductor with no metal, which is a warning nobody asked for. <paramref name="existing"/> names the drawing layer, else
    /// <paramref name="newLayerName"/> creates one. It gains no Material or σ, because it has no metal.</summary>
    internal bool AddFilmLevel(string below, LayerKey? existing, string? newLayerName, double ohmPerSq)
    {
        if (!(ohmPerSq > 0)) return false;
        if (existing is null && string.IsNullOrWhiteSpace(newLayerName)) return false;
        if (existing is { } taken && ResistiveFilms.Of(Working, taken) is not null) return false;
        var layers = Working.Stackup.Layers;
        int at = layers.FindIndex(l => string.Equals(l.Name, below, System.StringComparison.Ordinal));
        if (at < 0) return false;

        var before = SnapshotJson();
        var key = existing ?? AppendLayer(newLayerName!.Trim()).Key;
        var level = new StackupLayer
        {
            Kind         = StackupKind.Conductor,
            Name         = NextFreeFilmLevelName(),
            ThicknessDbu = LayoutUnits.ToDbu(0.05m, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron),
            Films        = [new StackupFilm { Layer = key, SheetResistanceOhmPerSq = ohmPerSq }],
        };
        layers.Insert(at + 1, level);
        CommitEdit(before, $"Add film level {level.Name}");
        return true;
    }

    private string NextFreeFilmLevelName()
    {
        const string name = "TFR Level";
        var taken = Working.Stackup.Layers.Select(l => l.Name).ToHashSet(System.StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{name} {n}")) return $"{name} {n}";
    }

    private StackupLayer ConductorNamed(string name)
        => Working.Stackup.Layers.First(l => string.Equals(l.Name, name, System.StringComparison.Ordinal));

    private string NameOfLayer(LayerKey key)
        => Working.Layers.FirstOrDefault(l => l.Key == key)?.Name ?? $"{key.Layer}/{key.Datatype}";
}
