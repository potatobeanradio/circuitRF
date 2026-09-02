// ================================================================
//  RecoverySessionLifecycleTests.cs — a clean close leaves nothing to recover (2026-09-01)
//
//  Owner: closing the workspace window on macOS and then using the background
//  File menu's New Workspace could raise a prompt about a file, offering to open
//  or discard it.
//
//  That prompt is CheckForRecovery. Every WorkspaceViewModel runs it at construction,
//  and it reads any recovery directory that is not its own as a prior session left by
//  an ungraceful exit. Two things fed it a directory that should not have been there:
//
//   1. WorkspaceWindow.OnClosing called OnCleanExit — which is what deletes the session
//      directory — only on the branch that had unsaved work to prompt about. A window
//      closed with nothing dirty returned before reaching it, so its session directory
//      survived. On macOS closing the last window does not end the process, so the very
//      next WorkspaceViewModel read the remnant and offered to restore it.
//
//   2. FindPriorSessions excluded only the CALLER's directory, so a second workspace
//      window would also read a first, still-open one's autosaves as a prior session.
//
//  This file gates (2) directly and (1) at the RecoveryManager end — that a cleared
//  session leaves nothing findable. The OnClosing call site itself is Avalonia window
//  code with no headless seam.
// ================================================================

using CircuitRF.Ui;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(AppDataRootCollection.Name)]
public sealed class RecoverySessionLifecycleTests : IDisposable
{
    private readonly string _root;

    public RecoverySessionLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-recovery-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string RecoveryRoot => Path.Combine(_root, "recovery");

    /// <summary>A session directory of the shape an ungraceful exit leaves behind.</summary>
    private string PlantAbandonedSession()
    {
        var dir = Path.Combine(RecoveryRoot, "abandoned" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R1" });
        SchematicPersistence.SaveToFile(Path.Combine(dir, "Untitled.csch"), model, "Untitled");
        return dir;
    }

    private static void Autosave(RecoveryManager mgr, string docName)
    {
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Capacitor, InstanceName = "C1" });
        var vm  = new ViewModels.SchematicViewModel(model);
        var doc = new SchematicDocument(docName, vm);
        mgr.AutoSave(doc);
    }

    /// <summary>The control: a genuine remnant IS still offered. Everything below narrows this,
    /// so it has to be established first or a passing suite would only prove nothing is found.</summary>
    [Fact]
    public void AnAbandonedSession_IsStillOffered()
    {
        var abandoned = PlantAbandonedSession();
        var mgr = new RecoveryManager();

        Assert.Contains(abandoned, RecoveryManager.FindPriorSessions(mgr.SessionDir));
    }

    /// <summary>A session another live window is autosaving into is not a prior session. Reading it
    /// as one offers the user their own open work back.</summary>
    [Fact]
    public void AnotherLiveSession_IsNotAPriorSession()
    {
        var live = new RecoveryManager();
        Autosave(live, "Untitled");
        Assert.True(Directory.GetFiles(live.SessionDir, "*.csch").Length > 0);

        var second = new RecoveryManager();

        Assert.DoesNotContain(live.SessionDir, RecoveryManager.FindPriorSessions(second.SessionDir));
    }

    /// <summary>What a clean close is FOR: after ClearSession there is nothing on disk to offer,
    /// so the next WorkspaceViewModel in the same process shows no prompt.</summary>
    [Fact]
    public void AClearedSession_LeavesNothingToOffer()
    {
        var closing = new RecoveryManager();
        Autosave(closing, "Untitled");
        var dir = closing.SessionDir;

        closing.ClearSession();

        Assert.False(Directory.Exists(dir));
        var next = new RecoveryManager();
        Assert.Empty(RecoveryManager.FindPriorSessions(next.SessionDir));
    }

    /// <summary>Skipping a live session must not skip it forever: once that manager has cleared,
    /// its directory is an ordinary path again — otherwise the guard would suppress a REAL remnant
    /// that a later session happened to be handed the same name for.</summary>
    [Fact]
    public void ALiveSessionSkip_DoesNotOutliveTheSession()
    {
        var live = new RecoveryManager();
        Autosave(live, "Untitled");
        var dir = live.SessionDir;

        live.ClearSession();                 // deletes the directory and drops the live mark
        Directory.CreateDirectory(dir);      // stand in for an ungraceful exit at that same path
        File.Copy(Path.Combine(PlantAbandonedSession(), "Untitled.csch"),
                  Path.Combine(dir, "Untitled.csch"));

        var next = new RecoveryManager();
        Assert.Contains(dir, RecoveryManager.FindPriorSessions(next.SessionDir));
    }
}
