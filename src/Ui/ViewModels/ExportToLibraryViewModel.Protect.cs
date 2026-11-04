// Export to Library's Protected model rows — brief vendorlib-16 R-vl16-2a, R-vl16-6a, and brief vendorlib-19 R-vl19-2a:
// one checkbox, OFF by default, and under it two choices always shown (greyed while it is unticked, so nothing moves when
// it is ticked) — Encapsulated netlist, preselected, and Compile netlist.
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
    /// <summary>Protect the model. Off by default: the user turns it on.</summary>
    [ObservableProperty] private bool _protectModel;

    /// <summary>The way it is protected: Encapsulated netlist (preselected) or, when false, Compile netlist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompileChosen))]
    private bool _encapsulateChosen = true;

    partial void OnProtectModelChanged(bool value) { RaiseProtectChoice(); Replan(); }
    partial void OnEncapsulateChosenChanged(bool value) { RaiseProtectChoice(); Replan(); }

    /// <summary>The second radio button: Compile netlist chosen.</summary>
    public bool CompileChosen
    {
        get => !EncapsulateChosen;
        set => EncapsulateChosen = !value;
    }

    /// <summary>Compile the cell's netlist into one protected module (brief vendorlib-16): protection on, Compile chosen.
    /// Setting it ticks the box and chooses Compile netlist; clearing it unticks the box.</summary>
    public bool CompileNetlist
    {
        get => ProtectModel && !EncapsulateChosen;
        set
        {
            if (value) EncapsulateChosen = false;
            ProtectModel = value;
        }
    }

    /// <summary>Encapsulated netlist (brief vendorlib-19): protection on, Encapsulated chosen.</summary>
    public bool Encapsulate => ProtectModel && EncapsulateChosen;

    /// <summary>The two choices are live only while the box is ticked.</summary>
    public bool IsProtectChoiceEnabled => ProtectModel;

    /// <summary>What Encapsulated netlist does, under its choice.</summary>
    public string EncapsulateNote => "Publishes the cell's linear network as one S-parameter block sampled over the Frequency plan, " +
                                     "with its devices placed against it. Needs no compiler.";

    /// <summary>What ticking the box does, or that it is off.</summary>
    public string ProtectNote => ProtectModel ? "" : "Off: the cell ships as it is.";

    private void RaiseProtectChoice()
    {
        OnPropertyChanged(nameof(CompileNetlist));
        OnPropertyChanged(nameof(Encapsulate));
        OnPropertyChanged(nameof(IsProtectChoiceEnabled));
        OnPropertyChanged(nameof(ProtectNote));
        OnPropertyChanged(nameof(IsFrequencyPlanEnabled));
        RaiseBuildState();
    }

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

    /// <summary>The Compile netlist choice: live while the box is ticked and a compiler is found — or while it is
    /// chosen, so it can always be left.</summary>
    public bool IsCompileNetlistEnabled => ProtectModel && (CompileNetlist || Plan is { CompileNetlistUnavailable: null });

    /// <summary>Why the Compile netlist choice is greyed, or what choosing it does.</summary>
    public string CompileNetlistNote => Plan is null ? ""
        : !CompileNetlist && Plan.CompileNetlistUnavailable is { } why ? why
        : !CompileNetlist ? "Publishes the cell as one compiled module, without its schematic."
        : $"The generated source goes to {Plan.SourcePath}, outside the library.";

    /// <summary>What is compiled, what stays outside, and the terminal count.</summary>
    public ObservableCollection<string> ProtectLines { get; } = [];

    /// <summary>The right-hand list's heading: what is copied, or — protected — what the model is made of.</summary>
    public string CopiedHeader => Plan is not { ProtectLines.Count: > 0 } ? "Copied into the model"
        : Encapsulate ? "The S-parameter block" : "Compiled into the model";

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
        OnPropertyChanged(nameof(CopiedHeader));
    }

    /// <summary>Set by <see cref="ShowForFigure"/>: nothing re-plans.</summary>
    private bool _planningOff;

    /// <summary>
    /// Documentation figures only: the dialog showing a STATED plan for a stated library, without planning. A real plan
    /// reads the file system and looks for a compiler, and a committed figure may depend on neither.
    /// </summary>
    internal void ShowForFigure(string library, ExportPlan plan, bool compileNetlist, bool encapsulate = false)
    {
        _planningOff = true;
        LibraryPath = library;
        CompileNetlist = compileNetlist;
        if (encapsulate) { EncapsulateChosen = true; ProtectModel = true; }
        Apply(plan, null);
    }

    /// <summary>The finished export's model folder, shown as a link that reveals it; null before one finishes.</summary>
    [ObservableProperty] private string? _finishedModelFolder;
    /// <summary>The folder the generated source was written to, shown as a link that reveals it.</summary>
    [ObservableProperty] private string? _finishedSourceFolder;

    /// <summary>The finished state's heading: the Builds table of a compiled export, or the export's own line.</summary>
    [ObservableProperty] private string _finishedHeader = "Builds";

    /// <summary>R-vl16-6a: the Builds table of a finished export; for an encapsulated one (R-vl19-2c), the model folder
    /// and the export's line with the proof's summary.</summary>
    public void ShowBuilds(ExportResult result)
    {
        BuildLines.Clear();
        FinishedHeader = result.Builds.Count > 0 ? "Builds" : "Exported";
        if (result.Builds.Count == 0 && result.Message is { } message) BuildLines.Add(message);
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
