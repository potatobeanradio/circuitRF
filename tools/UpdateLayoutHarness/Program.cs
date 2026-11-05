using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;

// UpdateLayoutHarness <design> -o <out-dir> [--tech <file.ctech>] [--no-connect] [--em-setup] [steps…]
//
// Copies <design> (a testdata/schematic-to-layout folder name, or the path of any cell folder holding schematic/)
// into a fresh scratch workspace under <out-dir>/ws, opens that workspace and the schematic in a real
// WorkspaceViewModel, and runs the steps IN ORDER. `run` is Design ▸ Update Layout from Schematic exactly as the
// menu item runs it (WorkspaceViewModel.RunLayoutUpdate, the user's preferences, the layout opened as a document);
// every message it posts is printed. See README.md for the steps.

string? designArg = null, outArg = null, techArg = null;
bool noConnect = false, emSetup = false;
var steps = new List<string>();
for (int a = 0; a < args.Length; a++)
{
    switch (args[a])
    {
        case "-o": outArg = args[++a]; break;
        case "--tech": techArg = args[++a]; break;
        case "--no-connect": noConnect = true; break;
        case "--em-setup": emSetup = true; break;
        default:
            if (designArg is null && !args[a].Contains(':') && args[a] is not ("run" or "restore" or "dump" or "undo" or "redo"))
                designArg = args[a];
            else steps.Add(args[a]);
            break;
    }
}
if (designArg is null || outArg is null)
{
    Console.Error.WriteLine("usage: UpdateLayoutHarness <design> -o <out-dir> [--tech <file.ctech>] [--no-connect] [--em-setup] [steps…]");
    return 2;
}

string repo = RepoRoot();
string outDir = Path.GetFullPath(outArg);
Directory.CreateDirectory(outDir);
// Never the user's own preferences: a scratch state directory unless the caller chose one.
if (Environment.GetEnvironmentVariable("CRF_STATE_DIR") is not { Length: > 0 })
    Environment.SetEnvironmentVariable("CRF_STATE_DIR", Path.Combine(outDir, "state"));

AppBuilder.Configure<HarnessApp>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
AppPreferencesIo.Update(p =>
{
    p.ConnectPartsOnUpdateLayout = !noConnect;
    p.EmSetupOnUpdateLayout = emSetup;
});

string designsRoot = Path.Combine(repo, "testdata", "schematic-to-layout");
string src = Directory.Exists(designArg) ? Path.GetFullPath(designArg) : Path.Combine(designsRoot, designArg);
if (!Directory.Exists(CellFolder.SubFolderPath(src, ViewType.Schematic)))
{
    Console.Error.WriteLine($"'{designArg}' is neither a design in {designsRoot} nor a cell folder with a schematic/ folder.");
    return 2;
}
string techSource = techArg is not null ? Path.GetFullPath(techArg) : Path.Combine(designsRoot, "microstrip-0508.ctech");
string design = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));

string ws = Path.Combine(outDir, "ws");
if (Directory.Exists(ws)) Directory.Delete(ws, recursive: true);
string techPath = Path.Combine(ws, "tech", Path.GetFileName(techSource));
Directory.CreateDirectory(Path.GetDirectoryName(techPath)!);
File.Copy(techSource, techPath);
// A technology names its material libraries relative to itself, and without them it does not load.
foreach (string lib in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(techSource))!, "*.cmat"))
    File.Copy(lib, Path.Combine(Path.GetDirectoryName(techPath)!, Path.GetFileName(lib)), true);
string cws = Path.Combine(ws, ".cws");
WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech/" + Path.GetFileName(techSource) });
string cellDir = Path.Combine(ws, design);
CopyTree(src, cellDir);
string csch = Directory.GetFiles(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), "*.csch").OrderBy(f => f, StringComparer.Ordinal).First();
string clay = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), Path.GetFileNameWithoutExtension(csch) + ".clay");

var messages = new Recorder();
var vm = new WorkspaceViewModel { MessagesOverride = messages };
var open = vm.OpenWorkspacePathAsync(cws);
for (int k = 0; k < 4000 && !open.IsCompleted; k++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
if (!open.IsCompleted) { Console.Error.WriteLine("the workspace did not open"); return 1; }
Pump();
if (!vm.OpenDocumentByPath(csch)) { Console.Error.WriteLine($"could not open {csch}"); return 1; }
Pump();
var doc = (SchematicDocument)vm.FindOpenDocument(Path.GetFullPath(csch))!;
var model = doc.ViewModel.EditModel;
var runLayoutUpdate = typeof(WorkspaceViewModel).GetMethod("RunLayoutUpdate", BindingFlags.NonPublic | BindingFlags.Instance)
                      ?? throw new MissingMethodException("WorkspaceViewModel.RunLayoutUpdate");
Console.WriteLine($"workspace {ws}");
Console.WriteLine($"schematic {csch}");

LayoutEditorViewModel Layout() =>
    (vm.FindOpenDocument(Path.GetFullPath(clay)) as LayoutDocument)?.ViewModel
    ?? throw new InvalidOperationException("no layout is open yet — `run` first");
EditableComponent Comp(string name) => model.Components.SingleOrDefault(c => c.InstanceName == name)
                                       ?? throw new ArgumentException($"no component '{name}' in the schematic");
LayoutInstance Inst(string name) => Layout().Model.Instances.SingleOrDefault(i => i.SchematicId == name)
                                    ?? throw new ArgumentException($"no linked instance '{name}' in the layout");

int exit = 0;
foreach (string step in steps)
{
    Console.WriteLine($"── {step}");
    int mark = messages.Lines.Count;
    try
    {
        var (verb, rest) = step.IndexOf(':') is var c and > 0 ? (step[..c], step[(c + 1)..]) : (step, "");
        switch (verb)
        {
            case "run":
                runLayoutUpdate.Invoke(vm, [doc, null, null, false]);
                break;
            case "restore":   // Layout ▸ Restore Removed Parts
                runLayoutUpdate.Invoke(vm, [doc, null, null, true]);
                break;
            case "resnap":    // Layout ▸ Re-snap to Schematic on these parts
                runLayoutUpdate.Invoke(vm, [doc, null, rest.Split(',').Select(n => Comp(n).InstanceName).ToHashSet(StringComparer.Ordinal), false]);
                break;
            case "set":
            {
                var (lhs, rhs) = Split(rest, '=');
                var (inst, name) = Split(lhs, '.');
                var p = Comp(inst).Parameters.SingleOrDefault(q => q.Name == name)
                        ?? throw new ArgumentException($"{inst} has no parameter '{name}'");
                Console.WriteLine($"   {inst}.{name}: {p.Expression} {p.Unit} → {rhs}");
                p.Expression = rhs;
                break;
            }
            case "rot":
                Comp(Split(rest, '=').Item1).Rotation = (SymbolRotation)int.Parse(Split(rest, '=').Item2);
                break;
            case "mirror":
                Comp(rest).MirrorX = !Comp(rest).MirrorX;
                break;
            case "move":
            {
                var (name, d) = Split(rest, '=');
                var (dx, dy) = Split(d, ',');
                int dbu = Layout().Model.DbuPerMicron;
                var i = Inst(name);
                i.X += (long)Math.Round(double.Parse(dx) * dbu);
                i.Y += (long)Math.Round(double.Parse(dy) * dbu);
                break;
            }
            case "turn":   // about the instance's own origin, degrees counter-clockwise
                Inst(Split(rest, '=').Item1).RotationDegrees += double.Parse(Split(rest, '=').Item2);
                break;
            case "delete":
                Layout().Model.Instances.Remove(Inst(rest));
                break;
            case "lock":
                Inst(rest).Locked = true;
                break;
            case "unlock":
                Inst(rest).Locked = false;
                break;
            case "undo":
                Layout().UndoCommand.Execute(null);
                break;
            case "redo":
                Layout().RedoCommand.Execute(null);
                break;
            case "save":
            {
                var l = Layout();
                LayoutPersistence.SaveToFile(clay, l.Model);
                string copy = Path.Combine(outDir, (rest.Length > 0 ? rest : design) + ".clay");
                File.Copy(clay, copy, overwrite: true);
                Console.WriteLine($"   {clay}");
                Console.WriteLine($"   {copy}  ({l.Model.Instances.Count} instances, {l.Model.Shapes.Count} shapes)");
                break;
            }
            case "dump":
                Dump(Layout().Model);
                break;
            default:
                throw new ArgumentException($"unknown step '{step}'");
        }
        Pump();
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or TargetInvocationException)
    {
        Console.WriteLine($"   ! {(ex as TargetInvocationException)?.InnerException?.ToString() ?? ex.Message}");
        exit = 1;
    }
    for (int k = mark; k < messages.Lines.Count; k++) Console.WriteLine("   " + messages.Lines[k]);
}
return exit;

void Dump(LayoutView l)
{
    string Um(long dbu) => (dbu / (double)l.DbuPerMicron).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    foreach (var i in l.Instances)
        Console.WriteLine($"   {i.SchematicId ?? i.DisplayRefDes ?? "-",-8} ({Um(i.X)}, {Um(i.Y)}) µm  {i.RotationDegrees}°" +
                          $"{(i.MirrorX ? " mirrored" : "")}{(i.Locked ? " locked" : "")}  {i.CellRef}");
    foreach (var s in l.Shapes.OfType<LabelShape>().Where(s => s.IsPort))
        Console.WriteLine($"   port '{s.Text}' ({Um(s.X)}, {Um(s.Y)}) µm  {s.PortDirection}  layer {s.Layer.Layer}/{s.Layer.Datatype}" +
                          $"{(s.SchematicId is { } id ? "  for " + id : "")}");
    foreach (var (k, g) in l.GeneratedPlacements.OrderBy(k => k.Key, StringComparer.Ordinal))
        Console.WriteLine($"   record {k}: ({Um(g.X)}, {Um(g.Y)}) µm {g.RotationDeg}°{(g.ChainAnchor ? " anchor" : "")}" +
                          $"{(g.Removed ? " removed" : "")}{(g.Text is { Length: > 0 } t ? " text " + t : "")}");
}

static (string, string) Split(string s, char c)
{
    int k = s.IndexOf(c);
    if (k < 0) throw new ArgumentException($"'{s}' needs a '{c}'");
    return (s[..k], s[(k + 1)..]);
}

static void Pump() { for (int k = 0; k < 10; k++) Dispatcher.UIThread.RunJobs(); }

static void CopyTree(string from, string to)
{
    foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    foreach (var v in new[] { ViewType.Schematic, ViewType.Symbol, ViewType.Layout })
        Directory.CreateDirectory(CellFolder.SubFolderPath(to, v));
}

static string RepoRoot()
{
    foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) return d.FullName;
    throw new DirectoryNotFoundException("run from inside the circuitRF repository");
}

/// <summary>The Messages panel, as lines: a grouped report prints its items under it.</summary>
sealed class Recorder : IMessageSink
{
    public List<string> Lines { get; } = [];
    public void Clear() { }
    public void Post(MessageLevel level, string text, string? filePath = null) => Lines.Add($"[{level}] {text}");
    public void PostItems(MessageLevel level, string text, IReadOnlyList<MessageItem> items, string? filePath = null)
    {
        Lines.Add($"[{level}] {text}");
        foreach (var i in items) Lines.Add($"      · {i.Text}");
    }
}

sealed class HarnessApp : Application
{
    public override void Initialize()
    {
        var baseUri = new Uri("avares://CircuitRF.Ui/");
        Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfResources.axaml") });
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfStyles.axaml") });
        var app = new App();
        app.Initialize();
        foreach (var t in app.DataTemplates) DataTemplates.Add(t);
    }
}
