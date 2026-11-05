// brief-em3d-22 R-em3d22-5b — the panel's Problem picker and, for a static problem, its terminal table.
//
// NOTHING THAT AFFECTS THE ANSWER LIVES ONLY HERE (R-em-11): the picker writes Problem3D, the table
// writes Terminals3D and the ground box Ground3D, each through the same undoable CommitEdit every other
// control uses. The table is staged text: a row is written when its box loses focus, and a row whose
// name and net are both blank is not written at all.

using System.Collections.ObjectModel;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One row of the Problem picker.</summary>
public sealed record Em3dProblemChoice(Em3dProblemType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>brief-dfr31-1 R-dfr31-1-4 — one row of a terminal or ground picker: what it shows, and what it writes.
/// brief-dfr34-4 R-dfr34-4-2 — two lines: <paramref name="Label"/> (the name, marked "(ground)" or with the terminal it is
/// in) and <paramref name="Detail"/> (the layers it spans, its vias, its size).</summary>
/// <param name="Body">The body it stands for; null for the ground's "every body not in a terminal" and default-ground rows.</param>
public sealed record Em3dBodyPick(string Label, string Token, Em3dBodyChoice? Body, string Detail = "")
{
    /// <summary>True when the row has a second line.</summary>
    public bool HasDetail => Detail.Length > 0;

    public override string ToString() => Token;
}

/// <summary>brief-dfr31-1 R-dfr31-1-5 — one row of the Other metal picker.</summary>
public sealed record Em3dUnassignedChoice(EmUnassignedConductors Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the terminal table, as typed.</summary>
public sealed partial class Em3dTerminalRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _net = "";
    [ObservableProperty] private string _source = "";

    /// <summary>brief-dfr31-1 — a pick in the 3D view is armed for this row.</summary>
    [ObservableProperty] private bool _isPicking;

    /// <summary>brief-em3d-49 — the Net column names the terminal's conductors by object (comma-separated), which is how
    /// a 3D view's terminal is written; false for a terminal stated by net.</summary>
    public bool ByObjects { get; init; }
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>What a 3D setup solves.</summary>
    public static IReadOnlyList<Em3dProblemChoice> Problem3DChoices { get; } =
    [
        new(Em3dProblemType.Driven,        "Driven (S-parameters)"),
        new(Em3dProblemType.Electrostatic, "Electrostatic (C matrix)"),
        new(Em3dProblemType.Magnetostatic, "Magnetostatic (L matrix)"),
        new(Em3dProblemType.Eigenmode,     "Eigenmode (resonances and Q)"),
    ];

    [ObservableProperty] private Em3dProblemChoice _problem3DChoice = Problem3DChoices[0];

    /// <summary>The terminal table's rows, in matrix order.</summary>
    public ObservableCollection<Em3dTerminalRow> TerminalRows { get; } = [];

    [ObservableProperty] private string _ground3DText = "";

    // ── brief-dfr31-1 R-dfr31-1-4/5 — the bodies a terminal can name, Other metal, and a pick in the 3D view ──────────────

    /// <summary>What the ground picker's first row says: every body in no terminal is ground (UnassignedConductors3D: Ground).</summary>
    public const string EveryBodyLabel = "Every body not in a terminal";

    /// <summary>brief-dfr34-4 R-dfr34-4-1 — the ground picker's second row: the ground-reference layers, added to the list.</summary>
    public const string DefaultGroundLabel = "Default ground";

    /// <summary>Lists the problem's bodies — the elaboration a run would build. Set by the host; slow on a board, so it is run
    /// off the UI thread by <see cref="ListBodiesAsync"/>. Null: no picker rows, the boxes take typing only.</summary>
    public Func<CancellationToken, IReadOnlyList<Em3dBodyChoice>>? BodyLister { get; set; }

    /// <summary>The terminal picker's rows. A stable reference: refilled, never replaced.</summary>
    public ObservableCollection<Em3dBodyPick> BodyPicks { get; } = [];

    /// <summary>The ground picker's rows: <see cref="EveryBodyLabel"/>, then the bodies.</summary>
    public ObservableCollection<Em3dBodyPick> GroundPicks { get; } = [];

    /// <summary>"listing conductors…" while the list is built, then how many bodies it found (or why it found none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaticStatus))]
    private string _bodiesStatus = "";

    /// <summary>Why the last pick in the 3D view filled nothing; cleared when a pick is armed or lands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaticStatus))]
    private string? _pickRefusal;

    /// <summary>The line under the terminal table: while a Pick is armed, what to do (and why the last click took nothing);
    /// otherwise how many pieces of metal there are.</summary>
    public string StaticStatus => PickingRow is { } row
        ? (PickRefusal is { } why ? why + " " : "") + $"Click a conductor in the 3D view for \"{row.Name}\" (Pick again cancels)."
        : BodiesStatus;

    /// <summary>The row a one-shot pick in the 3D view will fill; null when none is armed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaticStatus))]
    private Em3dTerminalRow? _pickingRow;

    /// <summary>The bodies of the row being edited, by member object name: what the 3D view highlights.</summary>
    public event Action<IReadOnlyList<string>>? HighlightRequested;

    public static IReadOnlyList<Em3dUnassignedChoice> UnassignedChoices { get; } =
    [
        new(EmUnassignedConductors.Float,  "Float (carries no charge)"),
        new(EmUnassignedConductors.Ground, "Ground"),
        new(EmUnassignedConductors.Refuse, "Refuse the run"),
    ];

    [ObservableProperty] private Em3dUnassignedChoice _unassignedChoice = UnassignedChoices[0];

    /// <summary>True for an electrostatic 3D setup: Other metal applies to it alone.</summary>
    public bool IsElectrostaticSetup => Is3DSetup && Problem3DChoice.Value == Em3dProblemType.Electrostatic;

    /// <summary>What a blank ground box means now.</summary>
    public string GroundPlaceholder => UnassignedChoice.Value == EmUnassignedConductors.Ground && IsElectrostaticSetup
        ? EveryBodyLabel.ToLowerInvariant() : "ground-reference";

    /// <summary>True when the host can arm a pick in its 3D view (a .c3d's setup): the rows' Pick button.</summary>
    public bool CanPickInView { get; set; }

    /// <summary>Type-to-filter for both pickers: a row whose label, detail or name holds the typed text, ignoring case. A
    /// list's last entry is what is being typed (brief-dfr34-4: the ground and a terminal take several, comma-separated).</summary>
    public static bool PickMatches(string? typed, object? item)
    {
        if (item is not Em3dBodyPick p) return false;
        string t = (typed ?? "")[((typed ?? "").LastIndexOf(',') + 1)..].Trim();
        return t.Length == 0 || p.Label.Contains(t, StringComparison.OrdinalIgnoreCase) ||
               p.Detail.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Token.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<Em3dBodyChoice> _bodies = [];

    /// <summary>True while a view shows this panel: the host's <see cref="SetBodyLister"/> re-lists only then.</summary>
    public bool IsShown { get; set; }

    private int _listGeneration;

    /// <summary>Builds the picker rows off the UI thread. Called when the setup window opens, and again by
    /// <see cref="SetBodyLister"/>; a listing overtaken by a later one lands nothing.</summary>
    public async Task ListBodiesAsync(CancellationToken ct = default)
    {
        if ((BodyLister ?? LayoutBodyLister()) is not { } lister || !IsStaticSetup) return;
        int generation = ++_listGeneration;
        BodiesStatus = "listing conductors…";
        IReadOnlyList<Em3dBodyChoice> bodies;
        try { bodies = await Task.Run(() => lister(ct), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (generation == _listGeneration) BodiesStatus = "The conductors could not be listed: " + ex.Message; return; }
        if (generation == _listGeneration) SetBodies(bodies);
    }

    /// <summary>The host's bodies changed (a 3D view adopted a new elaboration — the first one, or one with an object
    /// drawn since): kept, and the rows rebuilt at once while the panel is shown. A panel built before the view's first
    /// elaboration had no lister, so its list stayed empty and every Pick in the view matched nothing.</summary>
    public void SetBodyLister(Func<CancellationToken, IReadOnlyList<Em3dBodyChoice>> lister)
    {
        BodyLister = lister;
        if (IsShown) _ = ListBodiesAsync();
    }

    /// <summary>A <c>.cem</c>'s lister: its layout's solids, as the generator builds them for this setup.</summary>
    private Func<CancellationToken, IReadOnlyList<Em3dBodyChoice>>? LayoutBodyLister()
    {
        if (IsEmbedded || ResolveLayout?.Invoke(Working.LayoutRef) is not { Technology: { } tech } source) return null;
        var setup = Working.Clone();
        double? fMax = null;
        try { fMax = setup.ExpandFrequencies().Where(f => f > 0).DefaultIfEmpty().Max(); }
        catch (Exception) { /* no sweep: no skin depth, so only a zero thickness makes a sheet */ }
        return _ => Em3dBodyList.Of(Em3dLayoutSolids.From(source, tech, null,
            new Em3dLayoutSolidsOptions(fMax is > 0 ? fMax : null, setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC, Instance: false)
            { RegionSetup = setup }));
    }

    /// <summary>Fills both pickers from <paramref name="bodies"/> (what <see cref="ListBodiesAsync"/> lands).</summary>
    public void SetBodies(IReadOnlyList<Em3dBodyChoice> bodies)
    {
        _bodies = bodies;
        RefreshPicks();
        BodiesStatus = bodies.Count == 0 ? "No conductor in this problem." : $"{bodies.Count} piece(s) of metal.";
    }

    /// <summary>brief-dfr34-4 R-dfr34-4-2 — the bodies the ground box names now: the default ground when it is included, and
    /// each named entry's.</summary>
    public IReadOnlySet<Em3dBodyChoice> GroundBodies()
    {
        bool withDefault = Em3dGroundList.IncludesDefault(Ground3DText);
        var names = Em3dGroundList.Names(Ground3DText);
        return _bodies.Where(b => (withDefault && b.IsDefaultGround) || names.Any(b.IsNamedBy)).ToHashSet();
    }

    /// <summary>
    /// brief-dfr34-4 R-dfr34-4-2 — the picker rows as the table and the ground now read: a ground body is marked "(ground)" and
    /// not offered to a terminal (naming it only leads to a refusal), and a body a terminal row names carries that row's name.
    /// Rebuilt only when a row would read differently, so an unchanged list is left alone.
    /// </summary>
    private void RefreshPicks()
    {
        var ground = GroundBodies();
        var inTerminal = new Dictionary<Em3dBodyChoice, string>();
        foreach (var row in TerminalRows)
            foreach (string t in row.Net.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                foreach (var b in _bodies.Where(b => b.IsNamedBy(t)))
                    inTerminal.TryAdd(b, row.Name.Trim());
        Em3dBodyPick Row(Em3dBodyChoice b)
        {
            string mark = ground.Contains(b) ? " (ground)" : inTerminal.TryGetValue(b, out var t) ? $" (in {t})" : "";
            return new Em3dBodyPick(b.Name + mark, b.Token, b, b.Detail);
        }
        List<Em3dBodyPick> body = [.. _bodies.Where(b => !ground.Contains(b)).Select(Row)];
        List<Em3dBodyPick> groundRows =
        [
            new(EveryBodyLabel, "", null),
            new(DefaultGroundLabel, Em3dGroundList.DefaultEntry, null, "the ground-reference layers and the metal touching them"),
            .. _bodies.Select(Row),
        ];
        Refill(BodyPicks, body);
        Refill(GroundPicks, groundRows);
    }

    private static void Refill(ObservableCollection<Em3dBodyPick> into, List<Em3dBodyPick> rows)
    {
        if (into.SequenceEqual(rows)) return;
        into.Clear();
        foreach (var r in rows) into.Add(r);
    }

    /// <summary>The member objects of every body <paramref name="text"/> names (a row's text, comma-separated).</summary>
    public IReadOnlyList<string> MembersOf(string text)
        => [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                   .SelectMany(t => _bodies.Where(b => b.IsNamedBy(t))).Distinct().SelectMany(b => b.Members)];

    /// <summary>Asks the 3D view to highlight the bodies <paramref name="row"/> names.</summary>
    public void HighlightRow(Em3dTerminalRow? row)
    {
        if (row is not null) HighlightRequested?.Invoke(MembersOf(row.Net));
    }

    /// <summary>A pick in the 3D view while a row is armed: fills that row with the picked object's body, writes the table, and
    /// disarms. False when nothing is armed or the object is in no body.</summary>
    public bool PickConductor(string objectName) => PickConductor([objectName]);

    /// <summary>brief-dfr34-4 R-dfr34-4-3 — a pick along the line of sight: <paramref name="alongRay"/> is every object the
    /// click's ray crosses, nearest first, and the first conductor among them is taken — through the dielectric and air a
    /// click on a many-layer board lands on. False when nothing is armed or no object on the ray is in a body.</summary>
    public bool PickConductor(IReadOnlyList<string> alongRay)
    {
        if (PickingRow is not { } row) return false;
        var body = alongRay.Select(o => _bodies.FirstOrDefault(b => b.Members.Contains(o, StringComparer.Ordinal)))
                           .FirstOrDefault(b => b is not null);
        if (body is null)
        {
            // Said, not swallowed: metal the problem leaves out, or a click beside the board.
            PickRefusal = _bodies.Count == 0 ? "The conductors are not listed yet."
                : alongRay.Count == 0 ? "Nothing is under the cursor."
                : $"\"{alongRay[0]}\" is not a conductor, and no conductor is behind it.";
            return false;
        }
        PickingRow = null;
        if (ReferenceEquals(row, GroundPickRow))
        {
            // Designer feedback round 34 — the Ground box takes a pick as a terminal row does. brief-dfr34-4 R-dfr34-4-1 — and
            // adds it to the ground, which keeps what it already held.
            Ground3DText = Em3dGroundList.Add(Working.Ground3D, body.Token);
            CommitTerminals();
            HighlightRequested?.Invoke(body.Members);
            return true;
        }
        row.Net = body.Token;
        CommitTerminals();
        HighlightRow(row);
        return true;
    }

    /// <summary>Designer feedback round 34 — the row a Pick for the Ground box arms. It is not in
    /// <see cref="TerminalRows"/>: <see cref="PickConductor"/> writes its pick to <see cref="Ground3DText"/>.</summary>
    public Em3dTerminalRow GroundPickRow { get; } = new() { Name = "Ground" };

    partial void OnPickingRowChanged(Em3dTerminalRow? oldValue, Em3dTerminalRow? newValue)
    {
        if (oldValue is not null) oldValue.IsPicking = false;
        if (newValue is not null) newValue.IsPicking = true;
        PickRefusal = null;
    }

    [RelayCommand]
    private void PickTerminal(Em3dTerminalRow? row) => PickingRow = ReferenceEquals(PickingRow, row) ? null : row;

    partial void OnUnassignedChoiceChanged(Em3dUnassignedChoice value)
    {
        OnPropertyChanged(nameof(GroundPlaceholder));
        if (_suppressCommit || value.Value == Working.UnassignedConductors3D) return;
        var before = SnapshotJson();
        Working.UnassignedConductors3D = value.Value;
        CommitEdit(before, "Change other metal");
    }

    /// <summary>The ground picker's row was chosen: "every body not in a terminal" clears the box and grounds the rest; any
    /// other row is added to the ground (brief-dfr34-4 R-dfr34-4-1), to what the setup held before the list was opened.</summary>
    public void PickGround(Em3dBodyPick? pick)
    {
        if (pick is null) return;
        if (pick.Body is null && pick.Token.Length == 0)
        {
            Ground3DText = "";
            CommitTerminals();
            UnassignedChoice = UnassignedChoices.First(c => c.Value == EmUnassignedConductors.Ground);
            return;
        }
        Ground3DText = Em3dGroundList.Add(Working.Ground3D, pick.Token);
        CommitTerminals();
    }

    /// <summary>The Problem picker and its note: an EM setup's. A thermal setup's problem is its solver's choice.</summary>
    public bool ShowProblem3D => Is3DSetup && !IsThermalSetup;

    /// <summary>True when the terminal table is shown: a static problem on a 3D setup.</summary>
    public bool IsStaticSetup => Is3DSetup && Problem3DChoice.Value is Em3dProblemType.Electrostatic or Em3dProblemType.Magnetostatic;

    /// <summary>brief-em3d-49 — the table's conductor column: a 3D view's terminals name objects (a drawn object has no net).</summary>
    public string TerminalConductorHeader => "Conductor";

    /// <summary>True when the table's Source column is read (magnetostatic only).</summary>
    public bool IsMagnetostaticSetup => Is3DSetup && Problem3DChoice.Value == Em3dProblemType.Magnetostatic;

    public string Problem3DDescription => Problem3DChoice.Value switch
    {
        Em3dProblemType.Electrostatic =>
            "The capacitance matrix between the terminals below, referred to the ground. Palace only; the sweep " +
            "and the port impedances are kept but not used. Naming any part of a piece of metal takes all of it; metal in " +
            "no terminal floats, is ground, or refuses the run, as Other metal says.",
        Em3dProblemType.Magnetostatic =>
            "The inductance matrix between the terminals below, each driven through the port named as its source. " +
            "Palace only; conductors are surfaces, so this is the external (RF) inductance, without the internal term.",
        Em3dProblemType.Eigenmode =>
            "The structure's resonant frequencies and Q: the number of modes asked for, above the target. Palace only; " +
            "a lumped port is its resistance, a load, so Q is loaded (the table also takes the ports' share out).",
        _ => "S-parameters over the sweep, with a port at each port label — lumped unless the table below makes it a wave port.",
    };

    partial void OnProblem3DChoiceChanged(Em3dProblemChoice value)
    {
        RaiseStaticVisibility();
        if (_suppressCommit) return;
        if (value.Value == Working.Problem3D) return;
        // Thermal is chosen as a SOLVER (the Solver picker's Thermal row), and the Problem picker is hidden for it: a thermal
        // setup's problem is never changed from here.
        if (IsThermalSetup) return;
        var before = SnapshotJson();
        Working.Problem3D = value.Value;
        CommitEdit(before, "Change 3D problem");
        Refresh();
    }

    private void RaiseStaticVisibility()
    {
        OnPropertyChanged(nameof(IsThermalSetup));
        OnPropertyChanged(nameof(ShowRobustMeshing));
        OnPropertyChanged(nameof(ShowMeshFrequency3D));
        OnPropertyChanged(nameof(ShowReturnPlane));
        OnPropertyChanged(nameof(ShowConductorsGroup));
        OnPropertyChanged(nameof(IsNotThermalSetup));
        OnPropertyChanged(nameof(ShowProblem3D));
        OnPropertyChanged(nameof(IsStaticSetup));
        OnPropertyChanged(nameof(IsMagnetostaticSetup));
        OnPropertyChanged(nameof(IsElectrostaticSetup));
        OnPropertyChanged(nameof(GroundPlaceholder));
        OnPropertyChanged(nameof(Problem3DDescription));
        OnPropertyChanged(nameof(RadiationPatternDisabledReason));
        OnPropertyChanged(nameof(ReferenceInputPowerEnabled));
        OnPropertyChanged(nameof(ShowCreateSnpSchematic));
        RaiseEigenVisibility();
    }

    private void SyncStaticFields()
    {
        // A thermal setup shows no Problem picker: it holds the first EM problem, which nothing reads until Thermal is left.
        Problem3DChoice = Problem3DChoices.FirstOrDefault(c => c.Value == Working.Problem3D) ?? Problem3DChoices[0];
        // brief-dfr31-1 — rows are updated in place when their number is unchanged: every commit re-applies the snapshot, and
        // clearing the list tore down the row whose picker had just been used (an exception inside the list's own teardown),
        // and lost the focus of the row being typed in.
        var rows = Working.Terminals3D.Select(t => new Em3dTerminalRow
        {
            Name = t.Name, Net = t.ByObjects ? string.Join(", ", t.Objects!) : t.Net, Source = t.Source ?? "",
            ByObjects = t.ByObjects || (IsEmbedded && t.Net.Length == 0),
        }).ToList();
        if (rows.Count == TerminalRows.Count && rows.Zip(TerminalRows).All(p => p.First.ByObjects == p.Second.ByObjects))
            for (int i = 0; i < rows.Count; i++)
            {
                TerminalRows[i].Name = rows[i].Name;
                TerminalRows[i].Net = rows[i].Net;
                TerminalRows[i].Source = rows[i].Source;
            }
        else
        {
            TerminalRows.Clear();
            foreach (var r in rows) TerminalRows.Add(r);
        }
        Ground3DText = Working.Ground3D;
        RefreshPicks();
        UnassignedChoice = UnassignedChoices.FirstOrDefault(c => c.Value == Working.UnassignedConductors3D) ?? UnassignedChoices[0];
        SyncEigenFields();
        RaiseStaticVisibility();
    }

    /// <summary>Writes the table and the ground box as they now read: one undoable edit, or none when
    /// nothing changed.</summary>
    public void CommitTerminals()
    {
        var before = SnapshotJson();
        Working.Terminals3D = [.. TerminalRows
            .Where(r => r.Name.Trim().Length > 0 || r.Net.Trim().Length > 0)
            .Select(r => r.ByObjects
                ? new EmTerminal3D(r.Name.Trim(), "", r.Source.Trim() is { Length: > 0 } src ? src : null,
                                   [.. r.Net.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)])
                : new EmTerminal3D(r.Name.Trim(), r.Net.Trim(), r.Source.Trim() is { Length: > 0 } s ? s : null))];
        Working.Ground3D = Ground3DText.Trim();
        RefreshPicks();
        if (SnapshotJson() == before) return;
        CommitEdit(before, "Change 3D terminals");
    }

    [RelayCommand]
    private void AddTerminal()
    {
        TerminalRows.Add(new Em3dTerminalRow { Name = $"T{TerminalRows.Count + 1}", ByObjects = IsEmbedded });
        CommitTerminals();
    }

    [RelayCommand]
    private void RemoveTerminal(Em3dTerminalRow? row)
    {
        if (row is null || !TerminalRows.Remove(row)) return;
        CommitTerminals();
    }
}
