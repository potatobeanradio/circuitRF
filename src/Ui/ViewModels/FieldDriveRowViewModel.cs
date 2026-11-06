using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of a Fields analysis's Drive port list: the port, its amplitude when several are driven, and its remove button.</summary>
public sealed partial class FieldDriveRowViewModel : ObservableObject
{
    private readonly FieldsBodyViewModel _owner;

    internal FieldDriveRowViewModel(FieldsBodyViewModel owner, string driver, string weight)
    {
        _owner = owner;
        _driver = driver;
        _weight = weight;
        Items = [driver];
    }

    /// <summary>What the row can pick: the schematic's drivers, and what a file states if it is none of them, so nothing is lost by opening it.</summary>
    public IReadOnlyList<string> Items { get; private set; }

    [ObservableProperty] private string _driver;

    /// <summary>This drive's complex amplitude: a number or an expression over the globals. Empty is 1, or a P1Tone's Pavl and Phase.</summary>
    [ObservableProperty] private string _weight;

    partial void OnDriverChanged(string value) => _owner.DrivesChanged();
    partial void OnWeightChanged(string value) => _owner.DrivesChanged();

    /// <summary>What an empty amplitude means on this row.</summary>
    public string AmplitudeHint => _owner.HasPavl(this) ? "from Pavl and Phase" : "relative amplitude: 1";

    /// <summary>What a run would refuse about this row; null when it would run.</summary>
    public string? Problem => _owner.ProblemOf(this);

    public bool CanRemove => _owner.Drives.Count > 1;

    [RelayCommand]
    private void Remove() => _owner.RemoveDrive(this);

    internal void SetItems(IReadOnlyList<string> items)
    {
        Items = items;
        OnPropertyChanged(nameof(Items));
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(AmplitudeHint));
        OnPropertyChanged(nameof(CanRemove));
    }
}
