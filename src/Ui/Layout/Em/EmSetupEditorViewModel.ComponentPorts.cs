// brief-emb-1 R-emb1-8 — the 3D setup panel's "Component ports" list: every part placed on the 3D view's layout, a box to
// include it (writing the setup's ComponentPorts through the panel's one undoable commit), "Every two-terminal part" for `*`,
// and Check, which builds and verifies the ports exactly as a run would (C3dProblemAssembly.Assemble — the run's own function)
// and posts each part's verdict to Messages: the same lines `explain --component-ports` and `check` print. The panel itself
// keeps one short line and marks a part that was left out; the sentences live in Messages, where they can be read and
// copied without crowding the setup. A picker, not a text box: the parts can be listed, so a misspelt refdes cannot be written.

using System.Collections.ObjectModel;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>What the Component ports list reads of the 3D view it is embedded in: its document, path, workspace and the
/// elaboration the view already holds (for the list of placed parts), and where Check's verdicts are posted (Messages:
/// the text, and whether it is a warning).</summary>
public sealed record EmComponentPortsContext(Func<C3dDocument> Document, Func<string> Path, Func<string?> Workspace,
                                             Func<C3dElaboration?> Elaboration, Action<string, bool>? Post = null);

/// <summary>One placed part in the list.</summary>
public sealed partial class EmComponentPortRow(EmSetupEditorViewModel owner, string refdes, int pads) : ObservableObject
{
    public string Refdes { get; } = refdes;

    /// <summary>"2 pads", or "3 pads: one port per signal pad".</summary>
    public string PadsText { get; } = pads == 2 ? "2 pads" : $"{pads} pads";

    [ObservableProperty] private bool _isIncluded;

    /// <summary>The last Check left this part out; why is in Messages.</summary>
    [ObservableProperty] private bool _isRefused;

    internal bool Syncing;

    partial void OnIsIncludedChanged(bool value)
    {
        if (!Syncing) owner.ComponentPortToggled();
    }
}

public sealed partial class EmSetupEditorViewModel
{
    private EmComponentPortsContext? _componentPortsContext;

    /// <summary>Set by the 3D view's editor; null on a <c>.cem</c>, which shows no list.</summary>
    public EmComponentPortsContext? ComponentPortsContext
    {
        get => _componentPortsContext;
        set
        {
            _componentPortsContext = value;
            OnPropertyChanged(nameof(ShowComponentPorts));
            ListComponentParts();
        }
    }

    /// <summary>An embedded 3D EM setup: a thermal one has no ports, a <c>.cem</c> builds none (component ports are a 3D view's).</summary>
    public bool ShowComponentPorts => IsEmbedded && Is3DSetup && !IsThermalSetup && ComponentPortsContext is not null;

    public ObservableCollection<EmComponentPortRow> ComponentPortRows { get; } = [];

    /// <summary>The setup's <c>"*"</c>: every placed two-terminal part.</summary>
    [ObservableProperty] private bool _allComponentParts;

    /// <summary>What the last Check found, or why the list is empty.</summary>
    [ObservableProperty] private string _componentPortsStatus = "";

    public static string ComponentPortsTip { get; } =
        "A lumped gap port across each chosen part's own pads, built on the board's copper and checked before meshing: the " +
        "sheet's ends land on the closest copper (a landing rectangle where that copper is a cap or an arc), and nothing it " +
        "touches bridges two nets. A part with more pads gets one port per signal pad against its own ground pad. A finished " +
        "driven run also writes <setup>_cosim.cnl: the board's N-port with each part's model across its port. With Create SnP schematic " +
        "on, the same circuit is also made as a <setup>_cosim cell.";

    /// <summary>Builds and verifies the ports as a run would, off the UI thread (a board's elaboration and every part's port
    /// is seconds of work): the document is read as it stands when Check is pressed, and the panel stays usable meanwhile.
    /// The button is disabled until it finishes.</summary>
    [RelayCommand]
    private async Task CheckComponentPorts()
    {
        if (ComponentPortsContext is not { } ctx) return;
        foreach (var row in ComponentPortRows) row.IsRefused = false;
        if (Working.ComponentPorts.Count == 0) { ComponentPortsStatus = "No part is chosen."; return; }
        string path = ctx.Path(), documentText = C3dPersistence.Serialize(ctx.Document());
        string? workspace = ctx.Workspace();
        var setup = C3dSetups.ForRun(Working, path);
        ComponentPortsStatus = "Checking…";
        var g = await Task.Run(() => C3dProblemAssembly.Assemble(setup, C3dPersistence.Deserialize(documentText), path, workspace));
        if (g.ComponentPorts is not { } build)
        {
            ctx.Post?.Invoke($"Component ports: {g.Refusal ?? "the ports were not built."}", true);
            ComponentPortsStatus = "Not built; see Messages.";
            return;
        }
        foreach (var p in build.Ports)
        {
            ctx.Post?.Invoke(p.Ok ? C3dComponentPorts.Describe(p) : p.Refusal!, !p.Ok);
            if (!p.Ok && ComponentPortRows.FirstOrDefault(r => r.Refdes.Equals(p.Refdes, StringComparison.OrdinalIgnoreCase)) is { } row)
                row.IsRefused = true;
        }
        // The left-out parts were just posted with their reasons; the build's other warnings (no model on the schematic) follow.
        var refusals = build.Ports.Where(p => p.Refusal is not null).Select(p => p.Refusal!).ToList();
        foreach (string w in build.Warnings.Where(w => !refusals.Any(r => w.StartsWith(r, StringComparison.Ordinal))))
            ctx.Post?.Invoke(w, true);
        if (build.Refusal is { } whole) ctx.Post?.Invoke(whole, true);
        int ok = build.Ports.Count(p => p.Ok), left = build.Ports.Count - ok;
        ComponentPortsStatus = build.Refusal is not null || g.Refusal is not null ? "Not built; see Messages."
            : left == 0 ? $"{ok} built; details in Messages."
            : $"{ok} built, {left} left out; see Messages.";
    }

    /// <summary>The placed parts, listed off the elaboration the view holds, each ticked as the setup names it.</summary>
    public void ListComponentParts()
    {
        ComponentPortRows.Clear();
        if (ComponentPortsContext is not { } ctx || !ShowComponentPorts) return;
        if (ctx.Elaboration() is not { } e) { ComponentPortsStatus = "The 3D view has not been built yet."; return; }
        foreach (var (refdes, pads, _) in C3dComponentPorts.PlacedParts(e))
            ComponentPortRows.Add(new EmComponentPortRow(this, refdes, pads));
        ComponentPortsStatus = ComponentPortRows.Count == 0 ? "No part with a reference designator is placed on this 3D view's layout." : "";
        SyncComponentPortRows();
    }

    /// <summary>The boxes, from the setup (after an undo, or a setup edited elsewhere).</summary>
    private void SyncComponentPortRows()
    {
        bool all = Working.ComponentPorts.Any(c => c.Refdes == EmSetup.AllComponents);
        _suppressCommit = true;
        AllComponentParts = all;
        _suppressCommit = false;
        foreach (var row in ComponentPortRows)
        {
            row.Syncing = true;
            row.IsIncluded = all ? row.PadsText == "2 pads"
                                 : Working.ComponentPorts.Any(c => c.Refdes.Equals(row.Refdes, StringComparison.OrdinalIgnoreCase));
            row.Syncing = false;
        }
    }

    partial void OnAllComponentPartsChanged(bool value)
    {
        if (_suppressCommit) return;
        string before = SnapshotJson();
        // A part's own settings (its Z0, its width) are kept beside "*": ticking every part must not throw them away.
        Working.ComponentPorts = value ? [new EmComponentPort(EmSetup.AllComponents), .. Working.ComponentPorts.Where(c => c.Refdes != EmSetup.AllComponents && !c.IsPlain)]
                               : [.. ComponentPortRows.Where(r => r.IsIncluded).Select(r => Keep(r.Refdes))];
        SyncComponentPortRows();
        CommitEdit(before, "Component ports");
    }

    /// <summary>A row's box was clicked: the setup names exactly the ticked parts (an entry's Z0 and Reference are kept).</summary>
    internal void ComponentPortToggled()
    {
        string before = SnapshotJson();
        Working.ComponentPorts = [.. ComponentPortRows.Where(r => r.IsIncluded).Select(r => Keep(r.Refdes))];
        _suppressCommit = true;
        AllComponentParts = false;
        _suppressCommit = false;
        foreach (var row in ComponentPortRows) row.IsRefused = false;
        ComponentPortsStatus = "";
        CommitEdit(before, "Component ports");
    }

    private EmComponentPort Keep(string refdes)
        => Working.ComponentPorts.FirstOrDefault(c => c.Refdes.Equals(refdes, StringComparison.OrdinalIgnoreCase)) ?? new EmComponentPort(refdes);
}
