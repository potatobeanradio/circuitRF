// HZN-3 R-hzn3-2 — a 3D design held in a Hertzon project (.hzn) opens in the 3D editor as an ordinary .c3d document.
//
// The translation is HznToC3d.Translate (HZN-2), reached through the editor's document source (HznDesignSource). When it
// refuses, nothing opens: the refusal appears as the kernel's does when a document cannot be opened — a Messages line and a
// dialog over the text — with every unsupported item listed and grouped.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel : IHznTreeActions
{
    /// <summary>The heading of the refusal on open.</summary>
    public const string HznCannotOpen = "This design cannot be opened.";

    /// <summary>The refusal's dialog (file name, the text); a test substitutes a recorder.</summary>
    internal Func<string, string, Task> HznRefusalDialog { get; set; } = null!;

    private async Task ShowHznRefusalAsync(string title, string text)
    {
        if (ResolveOwner(null) is not { } owner) return;
        await TextConfirmDialog.AskAsync(owner, title, HznCannotOpen, text, null);
    }

    /// <inheritdoc/>
    public void OpenHznDesign(ProjectTreeNodeViewModel design)
    {
        if (design.Kind == NodeKind.HznDesignCircuit)
        {
            Messages.Info(HznMenuText.CircuitNotOpened);
            return;
        }
        if (design.Kind == NodeKind.HznDesign3D) OpenHznDesign(design.AbsolutePath, design.Name);
    }

    /// <summary>Opens (or focuses) the 3D design <paramref name="design"/> of the project at <paramref name="hznPath"/>.</summary>
    internal void OpenHznDesign(string hznPath, string design)
    {
        string full = Path.GetFullPath(hznPath);
        string key = HznDesignSource.KeyFor(full, design);
        if (ActivateIfOpen(key)) return;

        var source = new HznDesignSource(full, design, _techCache);
        C3dDocument document;
        try { document = source.Load(); }
        catch (HznOpenException ex)
        {
            string text = ex.Message;
            Messages.Error($"{HznCannotOpen} {text}");
            _ = HznRefusalDialog(source.Title, text);
            return;
        }
        catch (Exception ex) when (ex is C3dReadException or IOException or UnauthorizedAccessException)
        {
            Messages.Error($"Could not open '{design}' from '{Path.GetFileName(full)}': {ex.Message}");
            return;
        }

        // The geometry kernel is asked about the translated document exactly as it is for a .c3d on disk.
        if (C3dKernelUse.RefusalOnOpen(document, KernelCapability) is { } why)
        {
            source.Release();
            Messages.Error($"{C3dKernelUse.CannotOpen} {why}");
            _ = KernelRefusalDialog(source.Title, why);
            return;
        }

        OpenC3dEditor(full, key, document, scratch: false, source);
        if (!_openDocsByPath.ContainsKey(key)) { source.Release(); return; }
        if (source.Translation is { } t)
            foreach (string note in t.Notes) Messages.Info(note, full);
    }

    // ── Convert to .c3d… and Simulate on a design row ──────────────────────────────────────────

    /// <summary>Convert is always offered: the dialog asks the rest, and a refusal is a Messages line.</summary>
    public string? ConvertToC3dUnavailable => null;

    public void ConvertToC3d(ProjectTreeNodeViewModel design) => _ = ConvertHznDesignFromTreeAsync(design.AbsolutePath, design.Name);

    /// <summary>Converts the design as the FILE holds it. An open tab with unsaved edits is asked about first, so the conversion is of what the person chose.</summary>
    internal async Task<string?> ConvertHznDesignFromTreeAsync(string hznPath, string design, Window? owner = null)
        => await SettleUnsavedHznDesignAsync(hznPath, design, "Convert Anyway") ? await ConvertHznDesignAsync(hznPath, design, owner) : null;

    /// <summary>Simulate is always offered on a 3D design: the editor's own gate says what is missing (no setup, no solver).</summary>
    public string? SimulateUnavailable => null;

    public void SimulateHznDesign(ProjectTreeNodeViewModel design) => _ = SimulateHznDesignAsync(design.AbsolutePath, design.Name);

    /// <summary>
    /// Simulate on a design row: the design opens in the 3D editor (or its tab comes forward) and the editor's Simulate runs its
    /// active setup, so a design runs through the one path a <c>.c3d</c> does and with what the tab shows, edits included.
    /// </summary>
    internal async Task SimulateHznDesignAsync(string hznPath, string design)
    {
        string full = Path.GetFullPath(hznPath);
        string key = HznDesignSource.KeyFor(full, design);
        if (!ActivateIfOpen(key)) OpenHznDesign(full, design);
        if (!_openDocsByPath.TryGetValue(key, out var opened) || opened is not C3dEditorDocument doc) return;     // the refusal is already in Messages
        if (doc.ViewModel.SimulateBlockedReason is { } blocked) { Messages.Warning(blocked); return; }
        await RunC3dSetupAsync(doc.ViewModel, null);
    }

    // ── a design with unsaved edits is not what Hertzon, or a conversion, reads ────────────────────

    /// <summary>The question of an open tab with unsaved edits (message, the label of the run-anyway button); a test substitutes the answer.</summary>
    internal Func<string, string, Task<SaveChangesResult>>? HznUnsavedAsk { get; set; }

    /// <summary>
    /// For an action that reads the project's FILE: true to go on. A design open with unsaved edits asks Save (written, then go on),
    /// <paramref name="anywayLabel"/> (go on with the file as it is) or Cancel.
    /// </summary>
    internal async Task<bool> SettleUnsavedHznDesignAsync(string hznPath, string design, string anywayLabel)
    {
        if (!_openDocsByPath.TryGetValue(HznDesignSource.KeyFor(Path.GetFullPath(hznPath), design), out var open)
            || open is not C3dEditorDocument { ViewModel.IsDirty: true } doc) return true;
        string text = HznUnsavedText(design, hznPath);
        var answer = HznUnsavedAsk is { } ask ? await ask(text, anywayLabel)
            : ResolveOwner(null) is { } window ? await HznUnsavedDialog(design, hznPath, anywayLabel).ShowDialog<SaveChangesResult>(window)
            : SaveChangesResult.Cancel;
        return answer switch
        {
            SaveChangesResult.Save => SaveC3d(doc),
            SaveChangesResult.DontSave => true,
            _ => false,
        };
    }

    public static string HznUnsavedText(string design, string hznPath)
        => $"'{design}' has unsaved edits, and this uses the file '{Path.GetFileName(hznPath)}'.";

    /// <summary>Save, <paramref name="anywayLabel"/> (the file as it is) or Cancel.</summary>
    public static SaveChangesDialog HznUnsavedDialog(string design, string hznPath, string anywayLabel)
        => new(HznUnsavedText(design, hznPath), saveLabel: "Save", dontSaveLabel: anywayLabel, cancelLabel: "Cancel", title: "Unsaved Edits");

    /// <summary>The Known Files that are Hertzon projects, resolved; a 3D view's picker lists their designs.</summary>
    private IReadOnlyList<string> KnownHznFiles()
    {
        if (CurrentWorkspacePath is not { } cwsPath) return [];
        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { return []; }
        string root = Path.GetDirectoryName(cwsPath) ?? "";
        return [.. cws.KnownFiles
            .Where(p => string.Equals(Path.GetExtension(p), ".hzn", StringComparison.OrdinalIgnoreCase))
            .Select(p => WorkspaceRefs.ResolveExternalRef(p, root))
            .OfType<string>()];
    }
}
