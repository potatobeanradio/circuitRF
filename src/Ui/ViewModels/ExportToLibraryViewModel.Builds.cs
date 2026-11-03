// Export to Library's compiled-model rows — brief vendorlib-15 R-vl15-1d.
//
// A shell, as the rest of the dialog is: what ships compiled, which platform folders a build goes in, and every refusal
// come from ModelLibraryExport.Plan. What is here is the checkbox (OFF until the user ticks it), the list of builds
// made on other platforms, and the sentences that say why either is greyed.

using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One build made on another platform: its runtime identifier, picked from the platforms circuitRF runs on, and
/// the file.</summary>
public sealed partial class ExportBuildRowViewModel : ObservableObject
{
    [ObservableProperty] private string _rid;
    [ObservableProperty] private string _path = "";

    public ExportBuildRowViewModel(string rid) => _rid = rid;
}

public sealed partial class ExportToLibraryViewModel
{
    /// <summary>Compile each Verilog-A source the cell places and ship the build without the source. Off by default:
    /// the user turns it on.</summary>
    [ObservableProperty] private bool _compileModels;

    partial void OnCompileModelsChanged(bool value) => Replan();

    /// <summary>The checkbox is live only when the cell places Verilog-A source and a compiler is found — or when it is
    /// ticked, so it can always be unticked.</summary>
    public bool IsCompileEnabled => CompileModels || Plan is { CompileUnavailable: null };

    /// <summary>Why the checkbox is greyed, or what ticking it does.</summary>
    public string CompileNote => Plan is null ? ""
        : !CompileModels && Plan.CompileUnavailable is { } why ? why
        : $"The model ships the compiled build for {OsdiBuilds.CurrentRid}, and not its Verilog-A source.";

    /// <summary>The platforms a build can be listed for: every one circuitRF runs on but this one, whose build is the
    /// one the export runs with.</summary>
    public IReadOnlyList<string> OtherPlatforms { get; } = [.. OsdiBuilds.Rids.Where(r => r != OsdiBuilds.CurrentRid)];

    public ObservableCollection<ExportBuildRowViewModel> BuildRows { get; } = [];

    /// <summary>Builds for other platforms apply when the export ships a compiled model.</summary>
    public bool IsBuildsEnabled => Plan is { ShipsCompiled: true } && !CompileNetlist;

    public string BuildsNote => CompileNetlist
        ? "Compile netlist builds every platform the compiler can reach; another platform's build is added by exporting there."
        : Plan is { ShipsCompiled: true }
        ? $"The {OsdiBuilds.CurrentRid} build is the one this export runs and checks. A build listed here is copied, not run."
        : "Only a model that ships compiled has builds for other platforms.";

    [RelayCommand]
    private void AddBuild()
    {
        string rid = OtherPlatforms.FirstOrDefault(r => BuildRows.All(b => b.Rid != r)) ?? OtherPlatforms[0];
        var row = new ExportBuildRowViewModel(rid);
        row.PropertyChanged += (_, _) => Replan();
        BuildRows.Add(row);
        Replan();
    }

    [RelayCommand]
    private void RemoveBuild(ExportBuildRowViewModel row)
    {
        if (BuildRows.Remove(row)) Replan();
    }

    /// <summary>The rows the options carry: each with a file named. A later row for the same platform wins.</summary>
    private Dictionary<string, string> BuildsOption()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in BuildRows)
            if (r.Path.Trim().Length > 0) map[r.Rid] = r.Path.Trim();
        return map;
    }

    private void RaiseBuildState()
    {
        RaiseProtectState();
        OnPropertyChanged(nameof(IsCompileEnabled));
        OnPropertyChanged(nameof(CompileNote));
        OnPropertyChanged(nameof(IsBuildsEnabled));
        OnPropertyChanged(nameof(BuildsNote));
    }
}
