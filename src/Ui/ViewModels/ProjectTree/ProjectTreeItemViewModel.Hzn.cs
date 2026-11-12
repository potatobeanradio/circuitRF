// HZN-3 R-hzn3-1/-3 — a Hertzon project (.hzn) in the Project Tree: the node, its designs as children, and the menu.
//
// A .hzn is found by the ordinary scan (inside the workspace) or listed as a Known File (anywhere else). Either way
// the row expands to the project's designs, read on the first expansion and cached against the file's modification time
// (HznDesignList). A file that fails the version gate shows one child row carrying the refusal sentence, and nothing else.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels.ProjectTree;

/// <summary>What the Project Tree asks of the workspace for a Hertzon project and its designs. Each of the four actions
/// that belong to a later piece of the series reports why it is not offered yet; the piece that lands it returns null.</summary>
public interface IHznTreeActions
{
    /// <summary>Opens a 3D design in the 3D editor, or says why it cannot be opened.</summary>
    void OpenHznDesign(ProjectTreeNodeViewModel design);

    /// <summary>Opens the project in Hertzon.</summary>
    string? OpenInHertzonUnavailable { get; }
    void OpenInHertzon(ProjectTreeNodeViewModel project);

    /// <summary>Writes the design as a native .c3d.</summary>
    string? ConvertToC3dUnavailable { get; }
    void ConvertToC3d(ProjectTreeNodeViewModel design);

    /// <summary>Runs the design in Hertzon through its command line.</summary>
    string? RunInHertzonUnavailable { get; }
    void RunInHertzon(ProjectTreeNodeViewModel design);

    /// <summary>Runs the design on circuitRF's own EMerge driver.</summary>
    string? SimulateUnavailable { get; }
    void SimulateHznDesign(ProjectTreeNodeViewModel design);
}

public sealed partial class ProjectTreeNodeViewModel
{
    private HznDesignListing? _hznListing;

    private IHznTreeActions? Hzn => _actions as IHznTreeActions;

    /// <summary>A Hertzon project: a .hzn the scan found, or a Known File that is one and resolves.</summary>
    public bool IsHznProject =>
        Kind == NodeKind.HznFile
        || (Kind == NodeKind.KnownFile && !IsDirectory && !IsWarning
            && string.Equals(Path.GetExtension(AbsolutePath), ".hzn", StringComparison.OrdinalIgnoreCase));

    /// <summary>A design row: 3D or circuit.</summary>
    public bool IsHznDesign => Kind is NodeKind.HznDesign3D or NodeKind.HznDesignCircuit;

    public bool IsHznDesign3D => Kind == NodeKind.HznDesign3D;

    /// <summary>The project's name, from the file; null when it states none or cannot be read.</summary>
    public string? HznProjectName => IsHznProject ? HznDesignList.ProjectNameOf(AbsolutePath) : null;

    /// <summary>The Known File row's remove item names what it does: it forgets the path, the file stays.</summary>
    public string RemoveKnownFileHeader => IsHznProject ? "Remove from Known Files" : "Remove Reference";

    // The four items whose piece of the series has not landed. Each is disabled and its tooltip says so.
    public string? OpenInHertzonTip => IsHznProject ? Hzn is null ? HznMenuText.OpenInHertzon : Hzn.OpenInHertzonUnavailable : null;
    public string? ConvertToC3dTip => IsHznDesign3D ? Hzn is null ? HznMenuText.ConvertToC3d : Hzn.ConvertToC3dUnavailable : null;
    public string? RunInHertzonTip => IsHznDesign3D ? Hzn is null ? HznMenuText.RunInHertzon : Hzn.RunInHertzonUnavailable : null;
    public string? SimulateHznTip => IsHznDesign3D ? Hzn is null ? HznMenuText.Simulate : Hzn.SimulateUnavailable : null;

    public IRelayCommand OpenHznDesignCommand { get; private set; } = null!;
    public IRelayCommand OpenInHertzonCommand { get; private set; } = null!;
    public IRelayCommand ConvertToC3dCommand { get; private set; } = null!;
    public IRelayCommand RunInHertzonCommand { get; private set; } = null!;
    public IRelayCommand SimulateHznCommand { get; private set; } = null!;

    private void InitHznCommands()
    {
        OpenHznDesignCommand = new RelayCommand(() => Hzn?.OpenHznDesign(this), () => Hzn is not null && IsHznDesign);
        OpenInHertzonCommand = new RelayCommand(() => Hzn?.OpenInHertzon(this), () => Hzn is not null && IsHznProject && OpenInHertzonTip is null);
        ConvertToC3dCommand = new RelayCommand(() => Hzn?.ConvertToC3d(this), () => Hzn is not null && IsHznDesign3D && ConvertToC3dTip is null);
        RunInHertzonCommand = new RelayCommand(() => Hzn?.RunInHertzon(this), () => Hzn is not null && IsHznDesign3D && RunInHertzonTip is null);
        SimulateHznCommand = new RelayCommand(() => Hzn?.SimulateHznDesign(this), () => Hzn is not null && IsHznDesign3D && SimulateHznTip is null);
    }

    /// <summary>The children a project row starts with: the designs when it is already open, else one placeholder so the
    /// expander shows and the first expansion has something to replace.</summary>
    private IEnumerable<ProjectTreeNodeViewModel> HznInitialChildren(bool open)
    {
        if (open) return HznRows(_hznListing = HznDesignList.Of(AbsolutePath));
        var placeholder = new ProjectTreeNode(NodeKind.NotReadYet, "Not read yet — expand to list its designs", AbsolutePath, RelativePath);
        return [ChildRow(placeholder)];
    }

    /// <summary>Called when a project row is expanded: lists the designs now, or again if the file changed since.</summary>
    private void RefreshHznChildren()
    {
        var listing = HznDesignList.Of(AbsolutePath);
        if (ReferenceEquals(listing, _hznListing)) return;
        _hznListing = listing;
        Children.Clear();
        foreach (var row in HznRows(listing)) Children.Add(row);
        ApplyFilter(_parent?._searchSatisfied ?? false);
    }

    private IEnumerable<ProjectTreeNodeViewModel> HznRows(HznDesignListing listing)
    {
        if (!listing.Ok)
        {
            // The sentence names the file by its full path; the row sits under the file, so its name is enough.
            string sentence = listing.Refusal!.Render().Replace(AbsolutePath, Path.GetFileName(AbsolutePath), StringComparison.Ordinal);
            return [ChildRow(new ProjectTreeNode(NodeKind.HznRefusal, sentence, AbsolutePath, RelativePath, warningReason: sentence))];
        }
        return [.. listing.Designs.Select(d => ChildRow(new ProjectTreeNode(
            d.Kind == HznDesignKind.ThreeD ? NodeKind.HznDesign3D : NodeKind.HznDesignCircuit,
            d.Name, AbsolutePath, RelativePath)))];
    }

    private ProjectTreeNodeViewModel ChildRow(ProjectTreeNode node)
        => new(node, _filter, null, _actions, parent: this, onExpandUnreadReference: _onExpandUnreadReference);

    /// <summary>Whether a row under a project answers to the Known Files toggle (the project is one) or to Cells.</summary>
    private bool HznRowVisibleUnder(ProjectTreeFilterState f)
        => _parent is { Kind: NodeKind.KnownFile } ? f.KnownFiles : f.Cells;
}

/// <summary>What a disabled item says. Each names what is missing, not the series.</summary>
public static class HznMenuText
{
    public const string OpenInHertzon = "Not available yet: opening a project in Hertzon.";
    public const string ConvertToC3d = "Not available yet: converting a design to a .c3d.";
    public const string RunInHertzon = "Not available yet: running a design in Hertzon.";
    public const string Simulate = "Not available yet: running a Hertzon design on circuitRF's EMerge driver.";
    public const string CircuitNotOpened = "Circuit designs in .hzn files are not opened by circuitRF.";
}
