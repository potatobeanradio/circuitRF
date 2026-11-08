using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-de-4 — the TRL analysis's body in the Analysis dialog: the three SnP blocks on this bench holding the measured standards,
/// which side of the Smith chart the Reflect is on, and where the two fixtures are written. Paths picked with Browse… are stored as
/// an SnP's <c>File</c> is (<see cref="SnpPathPolicy.ToStored"/>), so the string here is the one a Deembed's <c>File</c> takes.
/// </summary>
public sealed partial class TrlBodyViewModel : ObservableObject
{
    /// <summary>The two Reflect choices, as the directive spells them.</summary>
    public static IReadOnlyList<string> ReflectTypes { get; } = [TrlAnalysis.ReflectShort, TrlAnalysis.ReflectOpen];

    private readonly string? _workspaceRoot;
    private readonly string? _schematicDirectory;

    /// <summary>The SnP instances on this bench, the choices for each standard.</summary>
    public IReadOnlyList<string> SnpInstances { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid))] private string? _thru;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid))] private string? _reflect;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Problem), nameof(IsValid))] private string? _line;
    [ObservableProperty] private string _reflectType = TrlAnalysis.ReflectShort;
    [ObservableProperty] private string _fixtureA = "";
    [ObservableProperty] private string _fixtureB = "";

    /// <summary>The default each fixture box shows when empty, which follows the analysis name.</summary>
    [ObservableProperty] private string _fixtureAHint = "";
    [ObservableProperty] private string _fixtureBHint = "";

    public TrlBodyViewModel(SchematicEditModel model, string? workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _schematicDirectory = model.SchematicDirectory;
        SnpInstances = [.. model.Components.Where(c => c.Symbol == SymbolKind.Snp).Select(c => c.InstanceName).OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)];
        // A bench drawn for TRL names its blocks after the standards; otherwise the user picks.
        Thru    = Guess("THRU");
        Reflect = Guess("REFLECT");
        Line    = Guess("LINE");
        UseName("CAL1");
    }

    private string? Guess(string word) => SnpInstances.FirstOrDefault(n => n.Contains(word, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The body for an existing analysis.</summary>
    public static TrlBodyViewModel From(TrlAnalysis a, SchematicEditModel model, string? workspaceRoot) => new(model, workspaceRoot)
    {
        Thru = a.Thru, Reflect = a.Reflect, Line = a.Line,
        ReflectType = a.ReflectIsOpen ? TrlAnalysis.ReflectOpen : TrlAnalysis.ReflectShort,
        FixtureA = a.FixtureA, FixtureB = a.FixtureB,
    };

    /// <summary>Keeps the empty boxes' hints on the analysis's current name.</summary>
    public void UseName(string name)
    {
        string n = string.IsNullOrWhiteSpace(name) ? "CAL1" : name.Trim();
        FixtureAHint = TrlAnalysis.DefaultFixture(n, 'A');
        FixtureBHint = TrlAnalysis.DefaultFixture(n, 'B');
    }

    /// <summary>Stores a path picked with Browse…, workspace-relative when it can be, as an SnP's File is stored.</summary>
    public void ApplyPickedFixture(bool a, string absolutePath)
    {
        string stored = SnpPathPolicy.ToStored(absolutePath, _workspaceRoot, _schematicDirectory);
        if (a) FixtureA = stored; else FixtureB = stored;
    }

    /// <summary>What stops the analysis being built, or null.</summary>
    public string? Problem =>
        SnpInstances.Count == 0 ? "This bench has no SnP block: place one each for the measured Thru, Reflect and Line."
        : string.IsNullOrWhiteSpace(Thru) || string.IsNullOrWhiteSpace(Reflect) || string.IsNullOrWhiteSpace(Line)
            ? "Choose the SnP block holding each standard."
        : new[] { Thru, Reflect, Line }.Distinct(System.StringComparer.Ordinal).Count() < 3
            ? "Each standard needs its own SnP block."
        : null;

    public bool IsValid => Problem is null;

    public TrlAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled, Thru = Thru ?? "", Reflect = Reflect ?? "", Line = Line ?? "", ReflectType = ReflectType,
        FixtureA = FixtureA.Trim(), FixtureB = FixtureB.Trim(),
    };
}
