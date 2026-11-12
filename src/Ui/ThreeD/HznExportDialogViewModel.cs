// HZN-4 R-hzn4-4 — the Export Hertzon Project dialog: a new project or an existing one, and the design's name.
//
// The document is turned into a design ONCE when the dialog opens (off the UI thread), so a refusal is on screen before anything is
// chosen; the name and the target only decide where it goes. Every rule is HznExport's — this is the dialog's state. A design name the
// project already has is shown here and blocks Export (the CLI refuses it naming --design-name); nothing is ever overwritten.

using System.Text;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class HznExportDialogViewModel : ObservableObject
{
    private readonly C3dDocument _document;
    private readonly string _documentPath;
    private readonly string? _workspaceCws;
    private readonly Technology? _technology;
    private C3dToHznResult? _design;

    /// <param name="document">A snapshot of the document as the editor holds it, saved or not.</param>
    /// <param name="documentPath">The file it is (or will be) saved as: the technology and the cell are found from it.</param>
    /// <param name="suggestedFolder">Where a new project is offered.</param>
    /// <param name="technology">The materials of a document that has no technology file (a design held in a Hertzon project).</param>
    /// <param name="defaultDesignName">The design's name when the path does not give it.</param>
    public HznExportDialogViewModel(C3dDocument document, string documentPath, string? workspaceCws, string suggestedFolder,
                                    Technology? technology = null, string? defaultDesignName = null)
    {
        _technology = technology;
        _document = document;
        _documentPath = documentPath;
        _workspaceCws = workspaceCws;
        _designName = defaultDesignName ?? HznExport.DefaultDesignName(documentPath);
        _targetPath = Path.Combine(suggestedFolder, _designName + ".hzn");
    }

    [ObservableProperty] private bool _newProject = true;
    [ObservableProperty] private string _targetPath;
    [ObservableProperty] private string _designName;
    [ObservableProperty] private bool _planning = true;
    [ObservableProperty] private bool _writing;
    [ObservableProperty] private string _report = "";
    [ObservableProperty] private string _problem = "";

    public bool ExistingProject { get => !NewProject; set => NewProject = !value; }
    public bool Busy => Planning || Writing;
    public string Title => "Export Hertzon Project";

    /// <summary>Set once the file is written.</summary>
    public HznExportOutcome? Outcome { get; private set; }

    /// <summary>The design as it will be written, once planned and not refused.</summary>
    public C3dToHznResult? Design => _design;

    public bool CanExport => !Busy && _design is { Ok: true } && Problem.Length == 0 && TargetPath.Trim().Length > 0 && DesignName.Trim().Length > 0;

    /// <summary>Raised with true once the file is written, false on Cancel: the window closes.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Asks the view for a file: true to save a new project, false to open an existing one. Null: cancelled.</summary>
    public Func<bool, Task<string?>>? Pick { get; set; }

    partial void OnNewProjectChanged(bool value)
    {
        OnPropertyChanged(nameof(ExistingProject));
        if (value && !TargetPath.EndsWith(".hzn", StringComparison.OrdinalIgnoreCase)) TargetPath = "";
        Recheck();
    }
    partial void OnTargetPathChanged(string value) => Recheck();
    partial void OnDesignNameChanged(string value) => Recheck();
    partial void OnPlanningChanged(bool value) => Notify();
    partial void OnWritingChanged(bool value) => Notify();
    partial void OnProblemChanged(string value) => Notify();

    private void Notify()
    {
        OnPropertyChanged(nameof(Busy));
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Turns the document into a design: the report says what will be written, or lists every item that stops it.</summary>
    public async Task PlanAsync()
    {
        Planning = true;
        try
        {
            var made = await Task.Run(() => HznExport.FromDocument(_document, _documentPath, DesignName, _workspaceCws, technology: _technology));
            _design = made;
            var sb = new StringBuilder();
            if (made.Ok)
            {
                int objects = made.Json!.GetArray("objects")!.Count, ports = made.Json.GetArray("excitations")!.Count, setups = made.Json.GetArray("setups")!.Count;
                sb.Append($"Writes {Count(objects, "object")}, {Count(ports, "port")} and {Count(setups, "setup")}.");
                foreach (string n in made.Notes) sb.Append("\n").Append(n);
            }
            else sb.Append(HznRefusalText.Render(made.Refusals));
            Report = sb.ToString();
        }
        finally
        {
            Planning = false;
            Recheck();
        }
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>The name against the project's designs, for an existing project.</summary>
    private void Recheck()
    {
        string problem = "";
        if (ExistingProject && TargetPath.Trim().Length > 0 && File.Exists(TargetPath))
        {
            var read = HznFile.Read(TargetPath);
            if (!read.Ok) problem = "Not a Hertzon project circuitRF reads.";
            else if (read.File!.Project.Designs.Any(d => string.Equals(d.Name, DesignName, StringComparison.Ordinal)))
                problem = $"The project already has a design named '{DesignName}'.";
        }
        else if (ExistingProject && TargetPath.Trim().Length > 0) problem = "That file does not exist.";
        Problem = problem;
        Notify();
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (Pick is null) return;
        if (await Pick(NewProject) is { Length: > 0 } path) TargetPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_design is not { Ok: true } design) return;
        Writing = true;
        try
        {
            var named = HznExport.Renamed(design, DesignName.Trim());
            string target = TargetPath.Trim();
            var outcome = await Task.Run(() => NewProject ? HznExport.ToNewFile(named, target) : HznExport.IntoExisting(named, target));
            if (outcome.Ok)
            {
                Outcome = outcome;
                CloseRequested?.Invoke(true);
            }
            else Problem = outcome.Error?.Render() ?? "The project was not written.";
        }
        finally { Writing = false; }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
