// ================================================================
//  PickerSourceItem.cs — one entry in a trace-card's source selector (R-dd-2)
// ================================================================

using System.IO;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// One row in the Add Trace picker's source combo: either a loaded dataset, or the
/// trailing "Add from file…" sentinel that loads a new dataset and immediately offers
/// its traces in the same gesture (R-dd-2). <see cref="Entry"/> is null for the sentinel.
/// </summary>
public sealed class PickerSourceItem
{
    public static readonly PickerSourceItem AddFromFile = new(null);

    public DataSourceEntryViewModel? Entry { get; }
    public bool IsAddFromFile => Entry is null;

    public string DisplayText => Entry is null
        ? "Add from file…"
        : (string.IsNullOrEmpty(Entry.Alias)
            ? Path.GetFileNameWithoutExtension(Entry.DisplayName)
            : Entry.Alias);

    public PickerSourceItem(DataSourceEntryViewModel? entry) => Entry = entry;

    public override string ToString() => DisplayText;
}
