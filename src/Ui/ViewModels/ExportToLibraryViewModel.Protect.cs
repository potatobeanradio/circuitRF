// Export to Library's Compile netlist rows — brief vendorlib-16 R-vl16-2a, R-vl16-6a.
//
// A shell, as the rest of the dialog is: the partition, the refusals, the bias points' defaults and the Builds table all
// come from ModelLibraryExport. What is here is the checkbox (OFF until the user ticks it), the bias-point text, and the
// rows the dialog shows once an export has run.

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class ExportToLibraryViewModel
{
    /// <summary>Compile the cell's netlist into one protected module. Off by default: the user turns it on.</summary>
    [ObservableProperty] private bool _compileNetlist;

    partial void OnCompileNetlistChanged(bool value) => Replan();

    /// <summary>The proof's bias points, one per line (<c>g=-0.5 d=5</c>); empty is the default set.</summary>
    [ObservableProperty] private string _checkBiasText = "";

    partial void OnCheckBiasTextChanged(string value) => Replan();

    /// <summary>The finished export, when it compiled the netlist: the dialog stays open to show its Builds table.</summary>
    [ObservableProperty] private ExportResult? _finished;

    partial void OnFinishedChanged(ExportResult? value)
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CancelLabel));
    }

    /// <summary>Cancel until a protected export has finished; then Close, which returns it.</summary>
    public string CancelLabel => Finished is null ? "Cancel" : "Close";

    /// <summary>Live when a compiler is found — or when ticked, so it can always be unticked.</summary>
    public bool IsCompileNetlistEnabled => CompileNetlist || Plan is { CompileNetlistUnavailable: null };

    /// <summary>Why the checkbox is greyed, or what ticking it does.</summary>
    public string CompileNetlistNote => Plan is null ? ""
        : !CompileNetlist && Plan.CompileNetlistUnavailable is { } why ? why
        : !CompileNetlist ? "Publishes the cell as one compiled module, without its schematic. Off: the cell ships as it is."
        : $"The generated source goes to {Plan.SourcePath}, outside the library.";

    /// <summary>What is compiled, what stays outside, and the terminal count.</summary>
    public ObservableCollection<string> ProtectLines { get; } = [];

    /// <summary>The right-hand list's heading: what is copied, or — compiled — what the model is made of.</summary>
    public string CopiedHeader => Plan is { ProtectLines.Count: > 0 } ? "Compiled into the model" : "Copied into the model";

    /// <summary>The bias points the proof runs at, when none are typed.</summary>
    public string CheckBiasNote => Plan is { CheckBias.Count: > 0 } p && CheckBiasText.Trim().Length == 0
        ? "Proved at: " + string.Join("; ", p.CheckBias) + ". Type one point per line to choose your own."
        : "One point per line, pin=volts; pins not named are at 0 V.";

    /// <summary>After an export: one row per platform, the summary above them, and where the source went.</summary>
    public ObservableCollection<string> BuildLines { get; } = [];
    [ObservableProperty] private string _unavailableLine = "";
    public bool HasBuilds => BuildLines.Count > 0;

    private IReadOnlyList<string> CheckBiasPoints()
        => [.. CheckBiasText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private void RaiseProtectState()
    {
        ProtectLines.Clear();
        if (Plan is { } plan) foreach (string l in plan.ProtectLines) ProtectLines.Add(l);
        OnPropertyChanged(nameof(IsCompileNetlistEnabled));
        OnPropertyChanged(nameof(CompileNetlistNote));
        OnPropertyChanged(nameof(CheckBiasNote));
    }

    /// <summary>Set by <see cref="ShowForFigure"/>: nothing re-plans.</summary>
    private bool _planningOff;

    /// <summary>
    /// Documentation figures only: the dialog showing a STATED plan for a stated library, without planning. A real plan
    /// reads the file system and looks for a compiler, and a committed figure may depend on neither.
    /// </summary>
    internal void ShowForFigure(string library, ExportPlan plan, bool compileNetlist)
    {
        _planningOff = true;
        LibraryPath = library;
        CompileNetlist = compileNetlist;
        Apply(plan, null);
    }

    /// <summary>The finished export's model folder, shown as a link that reveals it; null before one finishes.</summary>
    [ObservableProperty] private string? _finishedModelFolder;
    /// <summary>The folder the generated source was written to, shown as a link that reveals it.</summary>
    [ObservableProperty] private string? _finishedSourceFolder;

    /// <summary>R-vl16-6a: the Builds table of a finished export.</summary>
    public void ShowBuilds(ExportResult result)
    {
        BuildLines.Clear();
        foreach (var b in result.Builds) BuildLines.Add(b.Describe());
        FinishedModelFolder = result.ModelDir;
        FinishedSourceFolder = result.SourcePath is { } source ? Path.GetDirectoryName(source) : null;
        if (FinishedSourceFolder is not null)
            BuildLines.Add("The source above is not in the library. Keep it: every platform's build is made from it.");
        UnavailableLine = ModelLibraryExport.UnavailableLine(result.Builds);
        OnPropertyChanged(nameof(HasBuilds));
        Finished = result;
    }
}
