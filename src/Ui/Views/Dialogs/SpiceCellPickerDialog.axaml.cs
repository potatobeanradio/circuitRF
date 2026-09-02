using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the user chose in the picker: which definitions, out of which reading of the file.</summary>
/// <param name="Candidates">The chosen definitions, in the file's own order. Never empty.</param>
/// <param name="Scan">
/// The scan they came from — <b>not necessarily the one the dialog opened with</b>. Choosing a
/// <c>.lib</c> section re-reads the file, and the caller must build from the reading the user was
/// actually looking at.
/// </param>
/// <param name="Section">The chosen section, or null for the whole file.</param>
public sealed record SpiceCellPick(
    IReadOnlyList<SpiceCellCandidate> Candidates,
    SpiceCellScan                     Scan,
    string?                           Section);

/// <summary>
/// Picks which <c>.model</c> card(s) or <c>.subckt</c> definition(s) in a file to import.
///
/// <para><b>Cards and subcircuits are listed together, not on two tabs.</b> A supplier's file
/// routinely holds both — the subcircuit that is the part, and the cards that are its transistors —
/// and a user opening it has "the file for this part", not a classification. Splitting them would
/// ask them to make a distinction before they can see what is there.</para>
///
/// <para><b>Everything is listed, including what circuitRF cannot build</b>, each with the reason it
/// was refused. Filtering them out would answer "why is my VDMOS not offered?" with an absence — and
/// the reason is precisely the thing the user needs, since it says whether the fix is a different
/// file or a feature circuitRF does not have. A refused row can be clicked but not imported; it is
/// there to be read.</para>
///
/// <para><b>Several definitions can be chosen at once</b>, and that is what makes a library file's
/// second part importable at all: two variants of a part routinely share a core cell, and importing
/// them one at a time writes that core with the first and then refuses the second on it. One
/// gesture means one shared dependency plan and the core written once.</para>
///
/// <para><b>The Section combo appears only for a file that declares sections.</b> Sections are
/// ALTERNATIVES, so a sectioned file read with none chosen offers nothing at all — which is why the
/// combo sits above the list rather than beside the Import button.</para>
///
/// <para>Mirrors <see cref="PCellGeneratorPickerDialog"/>'s return-or-null contract.</para>
/// </summary>
public partial class SpiceCellPickerDialog : Window
{
    /// <summary>One offered definition. <see cref="Detail"/> is either what would be built or why not.</summary>
    private sealed record Row(SpiceCellCandidate Candidate)
    {
        public string Name      => Candidate.Name;
        public string TypeLabel => Candidate.TypeLabel;
        public string Detail    => Candidate.Detail;
        public bool   Supported => Candidate.IsSupported;

        /// <summary>Refused rows read as unavailable without needing a second colour to explain it.</summary>
        public double NameOpacity => Supported ? 1.0 : 0.55;
    }

    /// <summary>The item behind a Section combo entry. Index 0 is "no section" — today's default.</summary>
    private sealed record SectionRowItem(string? Section, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly string _path = "";
    private SpiceCellScan   _scan = new([], [], [], null);
    private string?         _section;

    public SpiceCellPickerDialog() => InitializeComponent();

    public SpiceCellPickerDialog(string path, SpiceCellScan scan) : this()
    {
        _path    = path;
        _scan    = scan;
        _section = scan.Section;

        if (scan.SectionNames.Count > 0)
        {
            var items = new List<SectionRowItem> { new(null, "Whole file (no section)") };
            foreach (var s in scan.SectionNames) items.Add(new SectionRowItem(s, s));

            SectionCombo.ItemsSource   = items;
            SectionCombo.SelectedIndex =
                _section is null ? 0 : items.FindIndex(i => i.Section == _section) is var i && i > 0 ? i : 0;
            SectionRow.IsVisible = true;

            SectionCombo.SelectionChanged += (_, _) =>
            {
                if (SectionCombo.SelectedItem is not SectionRowItem picked) return;
                if (picked.Section == _section) return;
                _section = picked.Section;
                _scan    = SpiceCellImport.Scan(_path, _section);
                Populate();
            };
        }

        Populate();

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click     += (_, _) => Accept();
        ChoiceList.DoubleTapped += (_, _) => Accept();
    }

    /// <summary>
    /// Fills the list from the CURRENT scan. Called again whenever a section changes, because a
    /// section is a different set of definitions rather than a filter over one set.
    /// </summary>
    private void Populate()
    {
        var rows        = _scan.Candidates.Select(c => new Row(c)).ToList();
        int supported   = rows.Count(r => r.Supported);
        int subcircuits = rows.Count(r => r.Candidate.Subcircuit is not null);

        Intro.Text = rows.Count == 0
            ? $"{Path.GetFileName(_path)} holds no '.model' cards and no '.subckt' definitions"
              + (_section is null ? "." : $" in section '{_section}'.")
            : $"{Path.GetFileName(_path)} holds {rows.Count} definition(s) — {subcircuits} subcircuit(s) and "
              + $"{rows.Count - subcircuits} model card(s) — of which circuitRF can build {supported}. "
              + "Each one chosen becomes a new cell in this workspace: a subcircuit becomes its own "
              + "netlist with a generic box for a symbol, a model card becomes the native component "
              + "carrying its parameters, and either way the pins are already wired. Choose several "
              + "to import them together — anything they share is built once.";

        ChoiceList.ItemsSource = rows;
        ChoiceList.SelectedItems?.Clear();

        // The highest-level supported definition is pre-selected, not the first: a library file is a
        // part plus the pieces it is built from, and the first row is routinely one of the pieces.
        var called = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var s in _scan.Subcircuits)
            foreach (var dep in s.Dependencies) called.Add(dep);

        var seed = rows.FirstOrDefault(r => r.Supported && r.Candidate.Subcircuit is { } sub && !called.Contains(sub.Name))
                ?? rows.FirstOrDefault(r => r.Supported);
        if (seed is not null) ChoiceList.SelectedItem = seed;

        // Something the user cannot import must not leave Import enabled — the click would otherwise
        // have to fail with a message saying what the row already says.
        ChoiceList.SelectionChanged -= OnSelectionChanged;
        ChoiceList.SelectionChanged += OnSelectionChanged;
        UpdateOkEnabled();

        Footnote.IsVisible = supported < rows.Count;
        if (Footnote.IsVisible)
            Footnote.Text =
                "The greyed rows cannot be imported; each says why. A card names a device circuitRF "
                + "has no model for; a subcircuit holds something it could not read, or calls one "
                + "that does.";
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateOkEnabled();

    private void UpdateOkEnabled() => OkButton.IsEnabled = Chosen().Count > 0;

    /// <summary>
    /// The supported rows currently selected, in the FILE's order rather than click order — a shared
    /// core must be planned the same way however the user happened to tick the two parts.
    /// </summary>
    private List<SpiceCellCandidate> Chosen()
    {
        if (ChoiceList.ItemsSource is not IEnumerable<Row> all) return [];

        var picked = new HashSet<Row>();
        foreach (var item in ChoiceList.SelectedItems ?? System.Array.Empty<object>())
            if (item is Row r && r.Supported) picked.Add(r);

        return [.. all.Where(picked.Contains).Select(r => r.Candidate)];
    }

    private void Accept()
    {
        var chosen = Chosen();
        if (chosen.Count > 0) Close(new SpiceCellPick(chosen, _scan, _section));
    }

    /// <summary>Shows the picker and returns what was chosen, or null on cancel.</summary>
    public static async Task<SpiceCellPick?> ShowAsync(Window? owner, string path, SpiceCellScan scan)
    {
        var dialog = new SpiceCellPickerDialog(path, scan);
        return owner is null
            ? await dialog.ShowDialog<SpiceCellPick?>(new Window())
            : await dialog.ShowDialog<SpiceCellPick?>(owner);
    }
}
