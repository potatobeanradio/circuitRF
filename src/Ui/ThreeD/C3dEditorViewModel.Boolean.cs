// brief-em3d-66 — booleans in the 3D editor: the Boolean panel (Subtract / Unite / Intersect…), Dissolve, entering a
// boolean to edit its operands (double-click, Ctrl/Cmd+]), and the refusals of a face or vertex edit on a result (D13).
//
// EVERY RULE IS C3dBooleans' (src/Design): what may be an operand and why not, which row is the Blank, what the result
// keeps, making and dissolving. This file asks the kernel, shows its answers, and pushes ONE undo entry.
//
// THE PREVIEW IS ASYNCHRONOUS AND IS WHAT THE COMMIT WILL BE (R-em3d66-2e/-2g). Every change of the panel builds the
// document OK would write, takes the tree its elaboration would hand the kernel (C3dElaborator.KernelTreeOf) and asks the
// kernel's PREVIEW session every question that elaboration asks (C3dElaborator.PreviewShape), the newest superseding the
// older. The view keeps rendering and the camera stays free. A reply is drawn by ONE scene built from that document, with
// the operands beside it as ghosts — translucent, never selectable, a subtraction's Tools in red — so the result is
// uploaded once per reply and never per frame. Since the preview asked the very questions of the very tree, OK's
// elaboration is answered from the cache both sessions share: the commit makes no worker call, and neither do undo and
// redo (the elaborator keeps every tree it lowered by its hash).
//
// AN ENTERED BOOLEAN IS EDITOR STATE, NOT A FRAME (R-em3d66-5a). Its operands are drawn as objects of their own — placed
// where the booleans above them put them, named "<boolean>:<tool>" and "<boolean>:Blank" so no name collides with a
// document object — and the result as a ghost. Each is addressed by an OPERAND INDEX (OperandBase + k): ObjectAt gives
// its world form, and ReplacementSlots writes an edit of it back into its top-level object as ONE replacement of that
// object, so every operation that edits an object by index (Move, Rotate, Mirror, Duplicate, Align, material, the face
// and vertex edits) edits an operand without knowing it is one. A drag of an operand is brief 46's preview of its own
// batch: the kernel is asked nothing until the release, which re-evaluates the boolean once.

using System.Collections.ObjectModel;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One row of the Boolean panel: a selected solid, and whether it is the Blank (every other row is a Tool).</summary>
public sealed partial class C3dBooleanRow(C3dEditorViewModel owner, int index, string name, string kind, string? material) : ObservableObject
{
    /// <summary>The object's index in the document.</summary>
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string MaterialText { get; } = material is { Length: > 0 } m ? m : "no material";

    [ObservableProperty] private bool _isBlank;

    /// <summary>"Blank" or "Tool".</summary>
    public string Role => IsBlank ? "Blank" : "Tool";

    public string Tip => C3dBooleans.RowsTip;

    internal bool Loading { get; set; }

    partial void OnIsBlankChanged(bool value)
    {
        OnPropertyChanged(nameof(Role));
        if (!Loading) owner.BooleanRowChanged(this);
    }
}

public sealed partial class C3dEditorViewModel
{
    // ── the kernel ────────────────────────────────────────────────────────────────────────────

    private readonly GeometryKernel? _kernel;

    /// <summary>The kernel this editor builds and previews with: the application's, unless a caller (a test) gave its own.</summary>
    public GeometryKernel Kernel => _kernel ?? GeometryKernel.Shared;

    /// <summary>R-em3d66-1c — a kernel command's tooltip when it is disabled for want of the kernel, or null: the capability's
    /// own sentence, never one of this editor's.</summary>
    public string? KernelMissing(string what) => GeometryKernel.DisabledReason(what, Kernel.Known);

    // ── what may be an operand (R-em3d66-1) ───────────────────────────────────────────────────

    /// <summary>
    /// Why Subtract, Unite and Intersect are disabled for the selection now, or null when they are enabled: the kernel first,
    /// then Object mode, then the first object that cannot be an operand, then fewer than two.
    /// </summary>
    public string? BooleanRefusal()
    {
        bool sheets = false;
        if (Viewer.SelectMode == Scene3DSelectMode.Object && Targets() is { Count: > 0 } picked && picked.All(t => !t.Instance && !IsOperandIndex(t.Index)))
            sheets = C3dBooleans.AllSheets([.. picked.Select(t => Document.Objects[t.Index])]);
        // brief-hzn-10 — sheets are combined here, in circuitRF: no kernel is needed for them.
        if (!sheets && KernelMissing("Boolean") is { } k) return k;
        if (Viewer.SelectMode != Scene3DSelectMode.Object) return C3dBooleans.ObjectModeOnly;
        var targets = Targets();
        if (targets.Any(t => !t.Instance && IsOperandIndex(t.Index)))
            return "Operands are combined inside their boolean: leave it (Esc) to combine top-level objects.";
        return C3dBooleans.SelectionRefusal([.. targets.Select(t => t.Instance ? null : Document.Objects[t.Index])]);
    }

    /// <summary>brief-hzn-10 — <see cref="KernelMissing"/> for an item that acts on a boolean: never for a Boolean of sheets.</summary>
    internal string? KernelMissingFor(string what, int boolean)
        => KernelMissingFor(what, boolean >= 0 && boolean < Document.Objects.Count ? Document.Objects[boolean] : null);

    internal string? KernelMissingFor(string what, C3dObject? boolean)
        => boolean is not null && C3dSheetBooleans.IsSheetBoolean(boolean) ? null : KernelMissing(what);

    /// <summary>The one selected top-level boolean, or −1.</summary>
    public int SelectedBoolean()
        => Viewer.SelectMode == Scene3DSelectMode.Object && Targets() is [{ Instance: false } t] && !IsOperandIndex(t.Index)
           && Document.Objects[t.Index] is C3dBoolean ? t.Index : -1;

    /// <summary>brief-em3d-67 — the path of the boolean a top-level object is, under its fillets and chamfers ("" when it is
    /// one itself), or null when it is none.</summary>
    private static string? BooleanPathOf(C3dObject o) => C3dFillets.Core(o) is (C3dBoolean, var path) ? path : null;

    // ── the panel (R-em3d66-2) ────────────────────────────────────────────────────────────────

    public static IReadOnlyList<C3dBooleanOp> BooleanOps { get; } = Enum.GetValues<C3dBooleanOp>();

    /// <summary>The selected solids in selection order, one Blank among them.</summary>
    public ObservableCollection<C3dBooleanRow> BooleanRows { get; } = [];

    [ObservableProperty] private bool _booleanOpen;
    [ObservableProperty] private C3dBooleanOp _booleanOp;
    /// <summary>D10 — off by default and remembered for the editor's lifetime: editor state, never the document's.</summary>
    [ObservableProperty] private bool _booleanKeepTools;
    [ObservableProperty] private string _booleanKeeps = "";
    [ObservableProperty] private string? _booleanError;
    [ObservableProperty] private string? _booleanNote;
    [ObservableProperty] private bool _booleanBusy;

    /// <summary>The panel's title: the operation's verb.</summary>
    public string BooleanTitle => C3dBooleans.Verb(BooleanOp);

    /// <summary>Swap is offered for two rows only; with more, the radio is the control.</summary>
    public bool BooleanCanSwap => BooleanRows.Count == 2;

    public bool BooleanKeepToolsEnabled => BooleanOp == C3dBooleanOp.Subtract;

    public static string BooleanKeepToolsTip => C3dBooleans.KeepToolsTip;

    /// <summary>OK: a reply is drawn, nothing is outstanding and nothing refused.</summary>
    public bool CanAcceptBoolean => BooleanOpen && !BooleanBusy && BooleanError is null && _booleanShown is not null;

    /// <summary>Preview requests sent, replies drawn, and replies discarded because a newer request had superseded them.</summary>
    public int BooleanPreviewsRequested { get; private set; }
    public int BooleanPreviewsDrawn { get; private set; }
    public int BooleanRepliesDiscarded { get; private set; }

    /// <summary>Booleans committed.</summary>
    public int BooleanCommits { get; private set; }

    private long _booleanSeq;
    /// <summary>The preview drawn: the document OK would write, and the kernel's reply (none for a Boolean of sheets, which
    /// circuitRF builds itself, brief-hzn-10).</summary>
    private (C3dDocument Doc, int At, C3dBoolean Boolean, C3dShapePreview? Reply)? _booleanShown;
    private IReadOnlyList<string> _booleanSelection = [];
    private bool _booleanRowsLoading;

    partial void OnBooleanOpChanged(C3dBooleanOp value)
    {
        OnPropertyChanged(nameof(BooleanTitle));
        OnPropertyChanged(nameof(BooleanKeepToolsEnabled));
        RequestBooleanPreview();
    }

    partial void OnBooleanKeepToolsChanged(bool value) => RequestBooleanPreview();

    partial void OnBooleanOpenChanged(bool value) => NotifyBoolean();
    partial void OnBooleanBusyChanged(bool value) => NotifyBoolean();
    partial void OnBooleanErrorChanged(string? value) => NotifyBoolean();

    private void NotifyBoolean()
    {
        OnPropertyChanged(nameof(CanAcceptBoolean));
        AcceptBooleanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Subtract…, Unite… or Intersect… on the selection: the panel, its rows in selection order with the last-selected the
    /// Blank (D4), and the first preview request. A refusal is said on the status line and nothing opens.
    /// </summary>
    public void OpenBoolean(C3dBooleanOp op)
    {
        if (BooleanRefusal() is { } why) { StatusMessage = why; return; }
        SetTool(null);
        CloseArray();
        CancelBoolean();
        var targets = Targets();
        _booleanSelection = [.. targets.Select(t => Document.Objects[t.Index].Name)];
        _booleanRowsLoading = true;
        BooleanRows.Clear();
        int blank = C3dBooleans.DefaultBlank(targets.Count);
        for (int r = 0; r < targets.Count; r++)
        {
            var o = Document.Objects[targets[r].Index];
            var row = new C3dBooleanRow(this, targets[r].Index, o.Name, C3dObject.KindOf(o), C3dValidation.EffectiveMaterial(o)) { Loading = true };
            row.IsBlank = r == blank;
            row.Loading = false;
            BooleanRows.Add(row);
        }
        _booleanRowsLoading = false;
        OnPropertyChanged(nameof(BooleanCanSwap));
        StatusMessage = "";
        _loadingBooleanOp = true;
        BooleanOp = op;
        _loadingBooleanOp = false;
        OnPropertyChanged(nameof(BooleanTitle));
        OnPropertyChanged(nameof(BooleanKeepToolsEnabled));
        BooleanOpen = true;
        RequestBooleanPreview();
    }

    private bool _loadingBooleanOp;

    /// <summary>A row's Blank radio: exactly one row is the Blank.</summary>
    internal void BooleanRowChanged(C3dBooleanRow row)
    {
        if (_booleanRowsLoading) return;
        if (!row.IsBlank)
        {
            // A radio is never unchecked by a click; a row left with no Blank at all takes it back.
            if (!BooleanRows.Any(r => r.IsBlank)) { row.Loading = true; row.IsBlank = true; row.Loading = false; }
            return;
        }
        _booleanRowsLoading = true;
        foreach (var r in BooleanRows.Where(r => r != row && r.IsBlank)) { r.Loading = true; r.IsBlank = false; r.Loading = false; }
        _booleanRowsLoading = false;
        RequestBooleanPreview();
    }

    /// <summary>Swap (two rows): the Tool becomes the Blank and the Blank the Tool.</summary>
    [RelayCommand]
    public void SwapBoolean()
    {
        if (BooleanRows.Count != 2) return;
        var tool = BooleanRows.First(r => !r.IsBlank);
        tool.IsBlank = true;
    }

    /// <summary>The document OK would write: the boolean at the Blank's place in construction order, the Tools gone from
    /// the top-level list. A copy; the document itself is untouched.</summary>
    private (C3dDocument Doc, int At, C3dBoolean Boolean) BooleanCandidate()
    {
        var indices = BooleanRows.Select(r => r.Index).ToList();
        int blank = BooleanRows.ToList().FindIndex(r => r.IsBlank);
        var b = C3dBooleans.Make(BooleanOp, [.. indices.Select(i => Document.Objects[i])], blank, BooleanKeepTools);
        var doc = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        int blankIndex = indices[blank];
        doc.Objects[blankIndex] = b;
        foreach (int i in indices.Where(i => i != blankIndex).OrderByDescending(i => i)) doc.Objects.RemoveAt(i);
        return (doc, blankIndex - indices.Count(i => i < blankIndex), b);
    }

    /// <summary>R-em3d66-2e — one preview request for the panel as it is now; the reply supersedes nothing older than it.</summary>
    private void RequestBooleanPreview()
    {
        if (!BooleanOpen || _loadingBooleanOp || _booleanRowsLoading || BooleanRows.Count < 2) return;
        var (doc, at, b) = BooleanCandidate();
        BooleanKeeps = C3dBooleans.Keeps(BooleanOp, b.Name, C3dValidation.EffectiveMaterial(b),
                                         [.. b.Tools.Select(t => (t.Name, C3dValidation.EffectiveMaterial(t)))], b.KeepTools);
        BooleanError = null;
        BooleanNote = null;
        long seq = ++_booleanSeq;
        BooleanPreviewsRequested++;
        if (C3dSheetBooleans.IsSheetBoolean(b)) { SheetBooleanPreview(doc, at, b); return; }
        GeometryKernelTree tree;
        try { tree = C3dElaborator.KernelTreeOf(doc, at, FilePath, Cell); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            BooleanBusy = false;
            BooleanError = e.Message;
            return;
        }
        BooleanBusy = true;
        NotifyBoolean();
        C3dElaborator.PreviewShape(Kernel, tree, b.Name)
                     .ContinueWith(t => _post(() => BooleanReplied(seq, t, doc, at, b)), TaskScheduler.Default);
    }

    /// <summary>
    /// brief-hzn-10 R-hzn10-3 — a Boolean of sheets previews at once, built here as the elaboration builds it: its operands'
    /// polygons combined (<see cref="C3dSheetBooleans.Result"/>). An empty result is said in the operation's own words, and the
    /// scene draws the result, never the operands overlapped.
    /// </summary>
    private void SheetBooleanPreview(C3dDocument doc, int at, C3dBoolean b)
    {
        bool wasShown = _booleanShown is not null;
        _booleanShown = null;
        var result = C3dSheetBooleans.Result(b);
        if (result.Pieces.Count == 0) BooleanError = C3dBooleans.EmptyResult(b, b.Name);
        else
        {
            BooleanNote = C3dBooleans.Pieces(result.Pieces.Count);
            _booleanShown = (doc, at, b, null);
            BooleanPreviewsDrawn++;
        }
        if (wasShown || _booleanShown is not null) Viewer.Regenerate();
        BooleanBusy = false;
        NotifyBoolean();
    }

    /// <summary>A reply: drawn when it is the latest, discarded (and counted) when a newer request superseded it. The scene
    /// is requested BEFORE the panel says it is no longer busy, so whoever waits for the reply finds its scene asked for.</summary>
    private void BooleanReplied(long seq, Task<C3dShapePreview?> t, C3dDocument doc, int at, C3dBoolean b)
    {
        if (!BooleanOpen || seq != _booleanSeq) { BooleanRepliesDiscarded++; return; }
        bool wasShown = _booleanShown is not null;
        _booleanShown = null;
        var failure = t.Exception?.InnerException;
        string? error = null, note = null;
        bool drawn = false;
        if (failure is OperationCanceledException || (failure is null && t.Result is null)) BooleanRepliesDiscarded++;
        // R-em3d66-2f — the worker's words, made readable; an empty result in the operation's own.
        else if (failure is GeometryKernelException { Code: C3dElaborator.EmptyCode }) error = C3dBooleans.EmptyResult(b, b.Name);
        else if (failure is not null) error = failure.Message;
        else if (t.Result is { } r)
        {
            if (r.Build.Solids == 0) error = C3dBooleans.EmptyResult(b, b.Name);
            else
            {
                note = C3dBooleans.Pieces(r.Build.Solids);
                _booleanShown = (doc, at, b, r);
                drawn = true;
            }
        }
        if (wasShown || _booleanShown is not null) Viewer.Regenerate();
        BooleanError = error;
        BooleanNote = note;
        if (drawn) BooleanPreviewsDrawn++;
        BooleanBusy = false;
        NotifyBoolean();
    }

    /// <summary>R-em3d66-2g — OK: one document change, one undo entry, the Boolean at its Blank's place. The reply already
    /// holds every answer the elaboration will ask for, so the commit asks the kernel nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptBoolean))]
    public void AcceptBoolean()
    {
        if (!CanAcceptBoolean || _booleanShown is not { } shown) return;
        var before = C3dListsEdit.Of(Document);
        var after = C3dListsEdit.Of(shown.Doc);
        string description = C3dBooleans.Description(shown.Boolean.Op, shown.Boolean.Name, [.. shown.Boolean.Tools.Select(t => t.Name)]);
        _booleanShown = null;
        CloseBooleanPanel();
        Viewer.SetSelection([]);
        _selectAfterAdopt = [shown.Boolean.Name];
        Push(new C3dListsEdit(description, before, after, ApplyLists));
        BooleanCommits++;
        StatusMessage = $"{description}." + (BooleanNote is { } n ? " " + n : "");
    }

    /// <summary>Cancel and Esc: nothing changed; the preview's scene goes, and the selection comes back.</summary>
    [RelayCommand]
    public void CancelBoolean()
    {
        if (!BooleanOpen) return;
        bool shown = _booleanShown is not null;
        _booleanShown = null;
        CloseBooleanPanel();
        Kernel.CancelPreview();
        if (shown)
        {
            _selectAfterAdopt = [.. _booleanSelection];
            Viewer.Regenerate();
        }
    }

    private void CloseBooleanPanel()
    {
        ++_booleanSeq;
        BooleanOpen = false;
        BooleanBusy = false;
        BooleanError = BooleanNote = null;
        BooleanRows.Clear();
        OnPropertyChanged(nameof(BooleanCanSwap));
    }

    // ── Dissolve, the Blank, a Tool leaving (R-em3d66-6, -3d, -4) ────────────────────────────

    /// <summary>Dissolve Boolean: the Blank at the boolean's place under its name, then the Tools — one undo entry, no
    /// worker call (they are managed objects, or trees already cached).</summary>
    public string? DissolveBoolean(int index)
    {
        if (index < 0 || index >= Document.Objects.Count || Document.Objects[index] is not C3dBoolean b)
            return StatusMessage = "Select one boolean to dissolve.";
        var before = C3dListsEdit.Of(Document);
        var objects = before.Objects.ToList();
        objects.RemoveAt(index);
        objects.InsertRange(index, C3dBooleans.Dissolve(b).Select(C3dPersistence.SerializeObject));
        LeaveBooleanFully();
        Viewer.SetSelection([]);
        _selectAfterAdopt = [b.Name, .. b.Tools.Select(t => t.Name)];
        Push(new C3dListsEdit($"Dissolve {b.Name}", before, (objects, before.Instances), ApplyLists));
        StatusMessage = $"Dissolved {b.Name}: its Blank and {b.Tools.Count} Tool{(b.Tools.Count == 1 ? "" : "s")} are objects of their own again.";
        return null;
    }

    /// <summary>
    /// Make Blank (a Tool) — and the inspector's Blank combo and Swap: Tool <paramref name="tool"/> of the boolean at
    /// <paramref name="booleanPath"/> under top-level object <paramref name="top"/> becomes its Blank, and the Blank a Tool in
    /// its place. The names follow their objects: the boolean takes its new Blank's name and the old Blank, now a Tool, the
    /// one the boolean had. One undo entry.
    /// </summary>
    public string? MakeBlank(int top, string booleanPath, int tool)
    {
        if (top < 0 || top >= Document.Objects.Count) return null;
        var root = Document.Objects[top];
        if (C3dBooleans.At(root, booleanPath) is not C3dBoolean b || tool < 0 || tool >= b.Tools.Count || b.Blank is null) return null;
        var swapped = (C3dBoolean)C3dBooleans.Copy(b);
        var newBlank = swapped.Tools[tool];
        var oldBlank = swapped.Blank!;
        string toolName = newBlank.Name;
        // A Blank that was itself nameless (a boolean that is its parent's Blank) needs a name to become a Tool.
        oldBlank.Name = b.Name.Length > 0 ? b.Name : NextName(C3dObject.KindOf(oldBlank).ToLowerInvariant());
        swapped.Name = b.Name.Length > 0 ? toolName : "";
        newBlank.Name = "";
        swapped.Tools[tool] = oldBlank;
        swapped.Blank = newBlank;
        var after = C3dBooleans.With(root, booleanPath, swapped);
        string label = b.Name.Length > 0 ? b.Name : SceneLabelOf(root.Name, booleanPath);
        if (!Push(new C3dEdit($"Make {toolName} the Blank of {label}",
                              [new C3dEditSlot(false, top, C3dPersistence.SerializeObject(root), C3dPersistence.SerializeObject(after))], ApplySlots)))
            return StatusMessage;
        if (booleanPath.Length == 0 && _entered is { } e && e.Top == root.Name) { _entered = (after.Name, e.Path); RefreshEntered(); }
        return null;
    }

    /// <summary>
    /// Remove from Boolean (a Tool, when more than one remains): the Tool at <paramref name="path"/> leaves its boolean and
    /// becomes a top-level object right after the boolean's top-level object, where the boolean put it. One undo entry.
    /// </summary>
    public string? RemoveFromBoolean(int top, string path)
    {
        if (top < 0 || top >= Document.Objects.Count) return null;
        var root = Document.Objects[top];
        if (C3dBooleans.LastStep(path) is not (>= 0 and var k) || C3dBooleans.At(root, C3dBooleans.ParentPath(path)) is not C3dBoolean parent)
            return StatusMessage = C3dBooleans.BlankNeeded;
        if (parent.Tools.Count < 2) return StatusMessage = "A boolean needs a Tool: this is its last one. Dissolve it instead.";
        var world = C3dBooleans.Copy(parent.Tools[k]);
        var carry = C3dBooleans.ParentTransform(root, path);
        if (!IsIdentity(carry)) world.Placement = world.Placement.Then(carry, out _);
        world.Group = root.Group;                    // out of the boolean, into the boolean's group (C3dGroups)
        world.Transparency = root.Transparency;      // brief-em3d-92 — and at the boolean's transparency, as Dissolve leaves it
        world.Appearance = root.Appearance?.Clone(); // brief-em3d-105 — and its look, likewise
        world.Model = root.Model;                    // brief-em3d-93 — and modelled as the boolean is
        var newRoot = WithoutTool(root, path);
        var before = C3dListsEdit.Of(Document);
        var objects = before.Objects.ToList();
        objects[top] = C3dPersistence.SerializeObject(newRoot);
        objects.Insert(top + 1, C3dPersistence.SerializeObject(world));
        Viewer.SetSelection([]);
        _selectAfterAdopt = [world.Name];
        Push(new C3dListsEdit($"Remove {world.Name} from {root.Name}", before, (objects, before.Instances), ApplyLists));
        return null;
    }

    /// <summary>Delete on a Tool: removed from its boolean and deleted, one undo entry (R-em3d66-6).</summary>
    public string? DeleteTool(int top, string path)
    {
        if (top < 0 || top >= Document.Objects.Count) return null;
        var root = Document.Objects[top];
        if (C3dBooleans.LastStep(path) is not (>= 0 and var k)) return StatusMessage = C3dBooleans.BlankNeeded;
        if (C3dBooleans.At(root, C3dBooleans.ParentPath(path)) is not C3dBoolean parent) return null;
        if (parent.Tools.Count < 2) return StatusMessage = "A boolean needs a Tool: this is its last one. Dissolve it instead.";
        Viewer.SetSelection([]);
        Push(new C3dEdit($"Delete {parent.Tools[k].Name} from {root.Name}",
                         [new C3dEditSlot(false, top, C3dPersistence.SerializeObject(root), C3dPersistence.SerializeObject(WithoutTool(root, path)))], ApplySlots));
        return null;
    }

    /// <summary>A copy of <paramref name="root"/> with the Tool at <paramref name="path"/> gone from its boolean.</summary>
    private static C3dObject WithoutTool(C3dObject root, string path)
    {
        string parentPath = C3dBooleans.ParentPath(path);
        var parent = (C3dBoolean)C3dBooleans.Copy(C3dBooleans.At(root, parentPath)!);
        parent.Tools.RemoveAt(C3dBooleans.LastStep(path));
        return C3dBooleans.With(root, parentPath, parent);
    }

    /// <summary>Delete of an entered operand: a Tool is removed from its boolean and deleted; the Blank is refused.</summary>
    private bool DeleteOperands(IReadOnlyList<int> operands)
    {
        if (operands.Count != 1) { StatusMessage = "Delete one operand at a time."; return true; }
        int top = TopOf(operands[0], out string path);
        if (top >= 0) DeleteTool(top, path);
        return true;
    }

    /// <summary>R-em3d66-4 — a switch of an operation (Enabled, KeepTools, Op) at <paramref name="index"/>: one entry.</summary>
    public void SetBooleanSwitch(int index, string description, Action<C3dBoolean> set)
        => ChangeObjects(description, [index], o => { if (o is C3dBoolean b) set(b); });

    /// <summary>Enabled on any operation — a boolean, and later a fillet or a chamfer (overview §1f).</summary>
    public void SetOperationEnabled(int index, bool enabled)
    {
        if (ObjectAt(index) is not C3dOperation op) return;
        ChangeObjects($"{(enabled ? "Enable" : "Disable")} {ObjectLabel(index)}", [index], o => { if (o is C3dOperation x) x.Enabled = enabled; });
    }

    /// <summary>One undo entry changing the operand at <paramref name="path"/> under top-level object <paramref name="top"/>,
    /// in its own (local) form — what the tree's rows edit, entered or not.</summary>
    public void ChangeOperand(string description, int top, string path, Action<C3dObject> mutate)
    {
        if (top < 0 || top >= Document.Objects.Count || C3dBooleans.At(Document.Objects[top], path) is not { } local) return;
        var copy = C3dBooleans.Copy(local);
        mutate(copy);
        var root = Document.Objects[top];
        string before = C3dPersistence.SerializeObject(root), after = C3dPersistence.SerializeObject(C3dBooleans.With(root, path, copy));
        if (after != before) Push(new C3dEdit(description, [new C3dEditSlot(false, top, before, after)], ApplySlots));
    }

    /// <summary>R-em3d66-5a — a click on an operand's row: its boolean is entered (when it is not already) and the operand
    /// selected in the scene once that scene is up.</summary>
    public void SelectOperand(int top, string path)
    {
        string booleanPath = C3dBooleans.ParentPath(path);
        string sceneName = SceneLabelOf(Document.Objects[top].Name, booleanPath) + ":" +
                           (C3dBooleans.LastStep(path) < 0 ? "Blank" : C3dBooleans.At(Document.Objects[top], path)?.Name);
        if (_entered is { } e && e.Top == Document.Objects[top].Name && e.Path == booleanPath && SceneObject(sceneName) is { } now)
        {
            Viewer.SelectMode = Scene3DSelectMode.Object;
            Viewer.SetSelection([Scene3DItem.OfObject(now.Id)]);
            return;
        }
        if (EnterBoolean(top, booleanPath) is not null) return;
        _selectAfterAdopt = [sceneName];
    }

    /// <summary>R-em3d66-4 — a boolean the kernel refused (an evaluation that failed is not rolled back) carries the refusal
    /// on its row, as a wire whose end is on no pad does.</summary>
    private void RefreshKernelFlags()
    {
        var refusals = Elaboration?.KernelRefusals;
        foreach (var item in AllTreeItems().Where(i => i.OperandPath is null && i.ObjectIndex >= 0 && i.ObjectIndex < Document.Objects.Count
                                                       && C3dOperands.IsKernel(Document.Objects[i.ObjectIndex])))
            // brief-em3d-67 — a feature row carries its object's refusal too (the row is labelled, not named).
            item.Refusal = refusals is not null && refusals.TryGetValue(Document.Objects[item.ObjectIndex].Name, out var why) ? why : null;
    }

    // ── entering a boolean (R-em3d66-5a) ──────────────────────────────────────────────────────

    /// <summary>The operand indices: an entered boolean's operands are OperandBase + k.</summary>
    public const int OperandBase = 1 << 28;

    public static bool IsOperandIndex(int index) => index >= OperandBase;

    /// <summary>One operand of the entered boolean: its path from its top-level object, and its scene name.</summary>
    private sealed record EnteredOperand(string Path, string SceneName, string Label);

    /// <summary>The entered boolean: its top-level object's NAME (an index moves when an object before it goes) and its path
    /// under it ("" for the top-level object itself).</summary>
    private (string Top, string Path)? _entered;
    private List<EnteredOperand> _enteredOperands = [];

    /// <summary>Whether a boolean is entered.</summary>
    public bool IsInBoolean => _entered is not null;

    /// <summary>The status line's breadcrumb while a boolean is entered.</summary>
    public string BooleanBreadcrumb => _entered is { } e ? $"Editing operands of '{EnteredLabel(e)}' — Esc to leave" : "";

    private string EnteredLabel((string Top, string Path) e) => e.Path.Length == 0 ? e.Top : SceneLabelOf(e.Top, e.Path);

    /// <summary>The top-level index of the entered boolean's object, or −1.</summary>
    private int EnteredTop => _entered is { } e ? Document.Objects.FindIndex(o => o.Name == e.Top) : -1;

    /// <summary>Edit Operands: enter the boolean at <paramref name="path"/> under top-level object <paramref name="top"/>.
    /// Null when entered, else why not.</summary>
    public string? EnterBoolean(int top, string path = "")
    {
        if (KernelMissingFor("Edit Operands", top) is { } k) return StatusMessage = k;
        if (top < 0 || top >= Document.Objects.Count || C3dBooleans.At(Document.Objects[top], path) is not C3dBoolean)
            return StatusMessage = "Select one boolean to edit its operands.";
        CancelBoolean();
        SetTool(null);
        _entered = (Document.Objects[top].Name, path);
        RefreshEntered();
        Viewer.SelectMode = Scene3DSelectMode.Object;
        Viewer.SetSelection([]);
        StatusMessage = "";
        Viewer.Regenerate();
        return null;
    }

    /// <summary>Esc or Ctrl/Cmd+[: out of the entered boolean — into the one around it, when it is nested.</summary>
    public void LeaveBoolean()
    {
        if (_entered is not { } e) return;
        SetTool(null);
        if (e.Path.Length == 0) _entered = null;
        else _entered = (e.Top, C3dBooleans.ParentPath(e.Path));
        RefreshEntered();
        Viewer.SetSelection([]);
        if (_entered is null) _selectAfterAdopt = [e.Top];
        StatusMessage = "";
        Viewer.Regenerate();
    }

    /// <summary>
    /// 3D editor bugs round 5 — a plain click, with a boolean entered, on anything that is not one of its operands: on
    /// nothing (its ghosted result included, which is not selectable) or on another object. Entering is a mode the tree
    /// puts the editor in by a single click on an operand's row, so leaving it by clicking away is what makes it one a
    /// user can get out of without knowing its key — before this, a click away cleared the selection and left the Tools
    /// drawn in place of the result until Esc. Leaves every level; another object clicked is selected once the result's
    /// scene is up. True when the click was taken here (the object's selection is this call's, not the viewer's).
    /// </summary>
    private bool ClickAwayLeaves(Avalonia.Input.KeyModifiers modifiers)
    {
        if (_entered is null || modifiers != Avalonia.Input.KeyModifiers.None) return false;
        var hovered = Viewer.HoveredItem is { } h ? Viewer.Scene.Object(h.Object) : null;
        if (hovered is not null && OperandIndexOf(hovered) >= 0) return false;
        string? other = hovered is null || Viewer.SelectMode != Scene3DSelectMode.Object ? null
            : DocumentIndex(hovered) is >= 0 and var di ? Document.Objects[di].Name
            : InstanceOf(hovered)?.Split('/', '[')[0];
        LeaveBooleanFully();
        StatusMessage = "";
        Viewer.SetSelection([]);
        _selectAfterAdopt = other is null ? null : [other];
        Viewer.Regenerate();
        return true;
    }

    /// <summary>Leaves every level at once (a Dissolve, a reload).</summary>
    private void LeaveBooleanFully()
    {
        if (_entered is null) return;
        _entered = null;
        RefreshEntered();
    }

    /// <summary>The entered boolean's operands, after any change of the document: a boolean that is gone is left.</summary>
    private void RefreshEntered()
    {
        _enteredOperands = [];
        if (_entered is { } e)
        {
            int top = EnteredTop;
            if (top < 0 || C3dBooleans.At(Document.Objects[top], e.Path) is not C3dBoolean b) _entered = null;
            else
            {
                string label = EnteredLabel(e);
                foreach (var (prefix, operand) in C3dOperands.Of(b))
                {
                    bool blank = prefix == "Blank.";
                    _enteredOperands.Add(new EnteredOperand(e.Path + prefix, label + ":" + (blank ? "Blank" : operand.Name),
                                                            blank ? $"{label}'s Blank" : operand.Name));
                }
            }
        }
        OnPropertyChanged(nameof(IsInBoolean));
        OnPropertyChanged(nameof(BooleanBreadcrumb));
        OnPropertyChanged(nameof(ViewportLine));
    }

    /// <summary>The label an operand is shown under: a Tool's own name, a Blank as "<its boolean>:Blank".</summary>
    private string SceneLabelOf(string top, string path)
    {
        int t = Document.Objects.FindIndex(o => o.Name == top);
        if (t < 0) return top;
        string label = top;
        string walked = "";
        foreach (int s in C3dBooleans.Steps(path) ?? [])
        {
            walked += C3dBooleans.StepText(s);
            // brief-em3d-67 — a fillet's Target is the solid under the same name: it adds nothing to the label.
            if (s == C3dBooleans.TargetStep) continue;
            label = s < 0 ? label + ":Blank" : C3dBooleans.At(Document.Objects[t], walked)?.Name ?? label;
        }
        return label;
    }

    /// <summary>The operand index of a scene object that is an entered operand, or −1.</summary>
    public int OperandIndexOf(Scene3DObject o)
    {
        for (int k = 0; k < _enteredOperands.Count; k++)
            if (_enteredOperands[k].SceneName == o.Name) return OperandBase + k;
        return -1;
    }

    /// <summary>The scene name an operand index is drawn under.</summary>
    private string? OperandSceneName(int index)
        => IsOperandIndex(index) && index - OperandBase < _enteredOperands.Count ? _enteredOperands[index - OperandBase].SceneName : null;

    /// <summary>An index as its top-level object's index and the path under it ("" for a top-level index).</summary>
    public (int Top, string Path) AddressOf(int index) => (TopOf(index, out string path), path);

    /// <summary>The top-level index an index belongs to, and the operand's path under it ("" for a top-level index).</summary>
    private int TopOf(int index, out string path)
    {
        path = "";
        if (!IsOperandIndex(index)) return index;
        int k = index - OperandBase;
        if (k >= _enteredOperands.Count) return -1;
        path = _enteredOperands[k].Path;
        return EnteredTop;
    }

    /// <summary>
    /// The object at <paramref name="index"/> — a top-level object, or an entered operand in its WORLD form (placed where the
    /// booleans above it put it, named as the scene draws it). A copy for an operand; the document's own object otherwise.
    /// </summary>
    public C3dObject? ObjectAt(int index)
    {
        if (!IsOperandIndex(index)) return index >= 0 && index < Document.Objects.Count ? Document.Objects[index] : null;
        int k = index - OperandBase;
        return k < _enteredOperands.Count && EnteredTop >= 0 ? WorldOperand(k) : null;
    }

    /// <summary>What an index is called in a sentence: an object's name, a Tool's name, "<boolean>'s Blank".</summary>
    public string ObjectLabel(int index)
        => IsOperandIndex(index) ? (index - OperandBase < _enteredOperands.Count ? _enteredOperands[index - OperandBase].Label : "")
                                 : Document.Objects[index].Name;

    /// <summary>What a copy of an operand is named after: a Tool's name, or a Blank's boolean's.</summary>
    private string CopyBaseName(int index)
    {
        int top = TopOf(index, out string path);
        var o = top >= 0 ? C3dBooleans.At(Document.Objects[top], path) : null;
        if (o is { Name.Length: > 0 }) return o.Name;
        return top >= 0 ? Document.Objects[top].Name : "copy";
    }

    /// <summary>The name an index's object is elaborated (and drawn) under.</summary>
    private string ElaboratedName(int index) => OperandSceneName(index) ?? Document.Objects[index].Name;

    private C3dObject WorldOperand(int k)
    {
        var op = _enteredOperands[k];
        var root = Document.Objects[EnteredTop];
        var local = C3dBooleans.At(root, op.Path)!;
        var w = C3dBooleans.Copy(local);
        var parent = C3dBooleans.ParentTransform(root, op.Path);
        if (!IsIdentity(parent)) w.Placement = w.Placement.Then(parent, out _);
        w.Name = op.SceneName;
        return w;
    }

    private static bool IsIdentity(C3dTransform t) => t == C3dTransform.Identity;

    /// <summary>An operand's world form, back in its boolean: its placement relative to the booleans above it again, and its
    /// own name (a Blank has none; a Tool's is kept unless the edit renamed it).</summary>
    private C3dObject LocalOperand(int k, C3dObject world)
    {
        var op = _enteredOperands[k];
        var root = Document.Objects[EnteredTop];
        var was = C3dBooleans.At(root, op.Path)!;
        var local = C3dBooleans.Copy(world);
        var parent = C3dBooleans.ParentTransform(root, op.Path);
        if (!IsIdentity(parent)) local.Placement = local.Placement.Then(parent.Inverse(), out _);
        local.Name = world.Name == op.SceneName ? was.Name : world.Name;
        return local;
    }

    /// <summary>
    /// Replacement slots for objects given their new forms by index — a top-level object as it is, an operand in its world
    /// form — with every operand of one top-level object written into ONE replacement of that object.
    /// </summary>
    internal List<C3dEditSlot> ReplacementSlots(IEnumerable<(int Index, C3dObject Object)> changes)
    {
        var slots = new List<C3dEditSlot>();
        var byTop = new Dictionary<int, C3dObject>();
        foreach (var (index, obj) in changes)
        {
            if (!IsOperandIndex(index))
            {
                string before = C3dPersistence.SerializeObject(Document.Objects[index]);
                string after = C3dPersistence.SerializeObject(obj);
                if (after != before) slots.Add(new C3dEditSlot(false, index, before, after));
                continue;
            }
            int top = TopOf(index, out string path);
            if (top < 0) continue;
            var root = byTop.TryGetValue(top, out var r) ? r : Document.Objects[top];
            byTop[top] = C3dBooleans.With(root, path, LocalOperand(index - OperandBase, obj));
        }
        foreach (var (top, root) in byTop)
        {
            string before = C3dPersistence.SerializeObject(Document.Objects[top]);
            string after = C3dPersistence.SerializeObject(root);
            if (after != before) slots.Add(new C3dEditSlot(false, top, before, after));
        }
        return slots;
    }

    /// <summary>The scene's inputs while a boolean is previewed or entered: the document text it draws, and its ghosts.
    /// Null when neither is so.</summary>
    private (string Text, Dictionary<string, Scene3DGhost> Ghosts)? BooleanScene()
    {
        if (BooleanOpen && _booleanShown is { } shown) return PreviewScene(shown.Doc, shown.Boolean);
        if (_entered is { } e && EnteredTop is var top and >= 0) return EnteredScene(top);
        return null;
    }

    /// <summary>The panel's preview: the document OK would write, with every operand beside it as a ghost.</summary>
    private (string, Dictionary<string, Scene3DGhost>) PreviewScene(C3dDocument doc, C3dBoolean b)
    {
        var ghosts = new Dictionary<string, Scene3DGhost>(StringComparer.Ordinal);
        var objects = doc.Objects;
        var shown = new List<C3dObject>(objects);
        foreach (var row in BooleanRows)
        {
            var g = C3dBooleans.Copy(Document.Objects[row.Index]);
            g.Name = row.Name + ":preview";
            g.Hidden = false;
            shown.Add(g);
            ghosts[g.Name] = !row.IsBlank && b.Op == C3dBooleanOp.Subtract ? Scene3DGhost.Taken : Scene3DGhost.Ghost;
        }
        doc.Objects = shown;
        try { return (C3dPersistence.Serialize(doc), ghosts); }
        finally { doc.Objects = objects; }
    }

    /// <summary>An entered boolean: the document with its result ghosted (or, disabled, left out — its operands are drawn
    /// anyway), and the operands as objects of their own; a face or vertex drag's stand-in for its object.</summary>
    private (string, Dictionary<string, Scene3DGhost>) EnteredScene(int top)
    {
        var ghosts = new Dictionary<string, Scene3DGhost>(StringComparer.Ordinal);
        var objects = Document.Objects;
        var variables = Document.Variables;
        var shown = new List<C3dObject>(objects);
        if (_facePreview is { } p && !IsOperandIndex(p.Index) && p.Index < shown.Count) shown[p.Index] = p.Object;
        var root = objects[top];
        if (root is C3dOperation { Enabled: false }) shown.RemoveAt(top);
        else
        {
            ghosts[root.Name] = Scene3DGhost.Ghost;
            if (root is C3dBoolean { Op: C3dBooleanOp.Subtract, KeepTools: true } kept)
                foreach (var t in kept.Tools) ghosts[t.Name] = Scene3DGhost.Ghost;
        }
        for (int k = 0; k < _enteredOperands.Count; k++)
        {
            var w = _facePreview is { } fp && fp.Index == OperandBase + k ? fp.Object : WorldOperand(k);
            shown.Add(w);
        }
        Document.Objects = shown;
        if (_namePreview is { } names) Document.Variables = names.Variables;
        try { return (C3dPersistence.Serialize(Document), ghosts); }
        finally { Document.Objects = objects; Document.Variables = variables; }
    }

    /// <summary>A double-click in Object mode with no tool armed: on a boolean's result — or on an entered operand that is
    /// itself a boolean — enter it. True when it did.</summary>
    private bool DoubleClickEnter()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object || Viewer.Scene.Object(Viewer.LastPick.Object) is not { } o) return false;
        if (OperandIndexOf(o) is int oi and >= 0)
        {
            int top = TopOf(oi, out string path);
            if (C3dBooleans.At(Document.Objects[top], path) is C3dBoolean) { EnterBoolean(top, path); return true; }
            return false;
        }
        int i = DocumentIndex(o);
        if (i < 0) i = BooleanOwningTool(o.Name);
        // brief-em3d-67 — a rounded boolean is entered through its fillets.
        if (i >= 0 && BooleanPathOf(Document.Objects[i]) is { } bp) { EnterBoolean(i, bp); return true; }
        return false;
    }

    /// <summary>The top-level boolean whose Tool (at any depth) is named <paramref name="name"/> — a disabled boolean draws its
    /// Tools under their own names — or −1.</summary>
    private int BooleanOwningTool(string name)
        => Document.Objects.FindIndex(o => o is C3dBoolean && C3dOperands.SelfAndDescendants(o).Skip(1).Any(d => d.Name == name));

    /// <summary>The boolean keys: Ctrl/Cmd+] enters the selected boolean (or an entered operand that is one), Ctrl/Cmd+[
    /// and Esc leave, and with the panel open Esc cancels and Enter is OK.</summary>
    private bool BooleanKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        bool command = (modifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Meta)) != 0;
        if (BooleanOpen)
        {
            if (key == Avalonia.Input.Key.Escape) { CancelBoolean(); return true; }
            if (key == Avalonia.Input.Key.Enter && modifiers == Avalonia.Input.KeyModifiers.None) { AcceptBoolean(); return true; }
        }
        if (command && key == Avalonia.Input.Key.OemCloseBrackets && SelectedInstance() < 0)
        {
            if (SelectedBoolean() is >= 0 and var b) { EnterBoolean(b); return true; }
            if (Targets() is [{ Instance: false } rt] && !IsOperandIndex(rt.Index) && BooleanPathOf(Document.Objects[rt.Index]) is { Length: > 0 } rp)
            {
                EnterBoolean(rt.Index, rp);
                return true;
            }
            if (Targets() is [{ Instance: false } t] && IsOperandIndex(t.Index) && TopOf(t.Index, out string path) is >= 0 and var top
                && C3dBooleans.At(Document.Objects[top], path) is C3dBoolean)
            {
                EnterBoolean(top, path);
                return true;
            }
        }
        if (_entered is not null && _tool is null &&
            ((command && key == Avalonia.Input.Key.OemOpenBrackets) || (key == Avalonia.Input.Key.Escape && modifiers == Avalonia.Input.KeyModifiers.None)))
        {
            LeaveBoolean();
            return true;
        }
        return false;
    }

    // ── D13: a kernel-made solid's faces are read, never edited (R-em3d66-5c) ─────────────────

    /// <summary>Why a face or vertex edit on <paramref name="obj"/> is refused, or null: a Boolean, Fillet, Chamfer or Step
    /// object — enabled or not — is edited through its operands or the operation.</summary>
    private static string? ResultEditRefusal(C3dObject obj)
        => C3dFillets.IsFeature(obj) ? C3dFillets.RoundedNotEditable(obj.Name, obj)          // brief-em3d-67 R-em3d67-6e
         : C3dOperands.IsKernel(obj) || C3dSheetBooleans.IsSheetBoolean(obj) ? C3dBooleans.ResultNotEditable(obj.Name, obj) : null;

    // ── the menus (R-em3d66-3d, -7) ───────────────────────────────────────────────────────────

    /// <summary>The canvas menu's Boolean submenu: Subtract…, Unite…, Intersect…, Dissolve Boolean, Edit Operands.</summary>
    private IEnumerable<Viewer3DMenuItem> BooleanMenuItems()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object || Targets().Count == 0) yield break;
        yield return new Viewer3DMenuItem("Boolean", Children: [.. BooleanItems(SelectedBoolean())]);
    }

    /// <summary>The five items, for the canvas and the tree: each disabled with its reason, never hidden.</summary>
    private IEnumerable<Viewer3DMenuItem> BooleanItems(int boolean)
    {
        string? why = BooleanRefusal();
        foreach (var op in BooleanOps)
        {
            var o = op;
            yield return new Viewer3DMenuItem(C3dBooleans.Verb(op) + "…", () => OpenBoolean(o), Enabled: why is null,
                Tip: why ?? $"{C3dBooleans.RowsTip} The result previews before OK.");
        }
        yield return Viewer3DMenuItem.Separator;
        string? one = KernelMissingFor("Dissolve Boolean", boolean) ?? (boolean < 0 ? "Select one boolean." : null);
        yield return new Viewer3DMenuItem("Dissolve Boolean", () => DissolveBoolean(boolean), Enabled: one is null,
            Tip: one ?? "Its operands become top-level objects again: the Blank under the boolean's name, then the Tools.");
        string? enter = KernelMissingFor("Edit Operands", boolean) ?? (boolean < 0 ? "Select one boolean." : null);
        yield return new Viewer3DMenuItem("Edit Operands", () => EnterBoolean(boolean), Enabled: enter is null,
            Tip: enter ?? "Ctrl/Cmd+], or double-click it. Esc or Ctrl/Cmd+[ leaves.");
    }

    /// <summary>3D ▸ Boolean's items, by name — the functions the context menu calls.</summary>
    private bool RunBooleanModify(string which)
    {
        switch (which)
        {
            case "BooleanSubtract": OpenBoolean(C3dBooleanOp.Subtract); return true;
            case "BooleanUnite": OpenBoolean(C3dBooleanOp.Unite); return true;
            case "BooleanIntersect": OpenBoolean(C3dBooleanOp.Intersect); return true;
            case "BooleanDissolve":
                if (KernelMissingFor("Dissolve Boolean", SelectedBoolean()) is { } k1) StatusMessage = k1; else DissolveBoolean(SelectedBoolean());
                return true;
            case "BooleanEnter":
                if (SelectedBoolean() is >= 0 and var b) EnterBoolean(b);
                else StatusMessage = KernelMissing("Edit Operands") ?? "Select one boolean to edit its operands.";
                return true;
        }
        return false;
    }

    /// <summary>The tree's items for a boolean's row (<paramref name="item"/> is one): the same functions as everywhere else.</summary>
    private IEnumerable<Viewer3DMenuItem> BooleanTreeItems(C3dTreeItem item)
    {
        var obj = item.OperandPath is { Length: > 0 } ? C3dBooleans.At(Document.Objects[item.ObjectIndex], item.OperandPath) : Document.Objects[item.ObjectIndex];
        string? kernel = KernelMissingFor("Edit Operands", obj);
        if (obj is C3dBoolean b)
        {
            yield return new Viewer3DMenuItem("Edit Operands", () => EnterBoolean(item.ObjectIndex, item.OperandPath ?? ""), Enabled: kernel is null, Tip: kernel);
            if (item.OperandPath is null)
                yield return new Viewer3DMenuItem("Dissolve Boolean", () => DissolveBoolean(item.ObjectIndex), Enabled: kernel is null, Tip: kernel);
            yield return new Viewer3DMenuItem(b.Enabled ? "✓ Enabled" : "Enabled", () => SetOperandEnabled(item, !b.Enabled), Enabled: kernel is null,
                Tip: kernel ?? "Unticked, the Blank and the Tools are objects of their own — drawn, edited and simulated — as Dissolve would write them.");
        }
    }

    private void SetOperandEnabled(C3dTreeItem item, bool enabled)
    {
        if (item.OperandPath is not { Length: > 0 } path) { SetOperationEnabled(item.ObjectIndex, enabled); return; }
        var root = Document.Objects[item.ObjectIndex];
        if (C3dBooleans.At(root, path) is not C3dOperation op) return;
        var copy = C3dBooleans.Copy(op);
        ((C3dOperation)copy).Enabled = enabled;
        Push(new C3dEdit($"{(enabled ? "Enable" : "Disable")} {item.Name}",
                         [new C3dEditSlot(false, item.ObjectIndex, C3dPersistence.SerializeObject(root),
                                          C3dPersistence.SerializeObject(C3dBooleans.With(root, path, copy)))], ApplySlots));
    }
}
