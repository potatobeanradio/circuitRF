// ================================================================
//  NewThreeDViewOpensTests.cs — 3D editor round 1: creating a 3D view OPENS it, orthographic, and a
//  cell's context menu offers Open 3D View. The open path needs a live dock and a GPU backend, so the
//  creation half is held on the source, comments stripped.
// ================================================================

using System.Text.RegularExpressions;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class NewThreeDViewOpensTests
{
    /// <summary>Every creation path — New 3D View, New 3D View from Layout and File ▸ New 3D Design — opens what it wrote,
    /// and every 3D view opens orthographic: the editor on any .c3d and a setup's Show 3D View, each before its stored
    /// camera is put back, so a projection the user left is kept.</summary>
    [Fact]
    public void EveryCreationPath_OpensTheNewView_Orthographic()
    {
        string ws = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        int start = ws.IndexOf("public async Task New3DViewAsync(", StringComparison.Ordinal);
        string body = ws[start..ws.IndexOf("private string? CreateThreeDViewFile(", start, StringComparison.Ordinal)];
        Assert.Contains("OpenOrActivateC3dEditor(created)", body);

        // File ▸ New ▸ New 3D Design: a new cell whose .c3d is written by the same function and opened
        // the same way.
        start = ws.IndexOf("internal async Task<string?> CreateCellHoldingViewAsync(", StringComparison.Ordinal);
        body = ws[start..ws.IndexOf("public async Task NewSymbolAsync(", start, StringComparison.Ordinal)];
        Assert.Matches(@"ViewType\.ThreeD\)\s*\{\s*if \(CreateThreeDViewFile\(newCellDir, name\) is \{ \} created\) OpenOrActivateC3dEditor\(created\);", body);
        Assert.Contains("CreateCellHoldingViewAsync(name, ViewType.ThreeD)", ws);

        string td = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs"));
        start = td.IndexOf("private async Task NewThreeDViewFromLayoutAsync(", StringComparison.Ordinal);
        Assert.Contains("OpenOrActivateC3dEditor(path)", td[start..]);
        // HZN-3 — a design held in a Hertzon project has no per-file window state, so its camera is not restored (`!held`).
        Assert.Matches(@"vm\.Viewer\.IsPerspective = false;\s*if \(!held && StoredCamera\(full\) is \{ \} camera\) vm\.Viewer\.RestoreCamera\(camera\);", td);
        string v3 = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.Viewer3D.cs"));
        Assert.Matches(@"vm\.IsPerspective = false;\s*vm\.RestoreCamera\(StoredCamera\(full\)\);", v3);
    }

    /// <summary>3D editor round 5 — a .c3d's camera, projection included, is window state as a .cem view's is: restored on
    /// open, written with the session, and kept when the tab closes.</summary>
    [Fact]
    public void TheEditorsCamera_IsRestoredOnOpen_AndPersistedWithTheSession()
    {
        string td = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs"));
        int start = td.IndexOf("private void OpenC3dEditor(", StringComparison.Ordinal);
        Assert.Contains("if (!held && StoredCamera(full) is { } camera) vm.Viewer.RestoreCamera(camera);", td[start..]);   // HZN-3
        start = td.IndexOf("private void ClosedC3dEditor(", StringComparison.Ordinal);
        Assert.Contains("doc.ViewModel.Viewer.CameraToPersist()", td[start..td.IndexOf("doc.ViewModel.Dispose()", start, StringComparison.Ordinal)]);
        string v3 = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.Viewer3D.cs"));
        start = v3.IndexOf("Viewer3DCamerasToPersist()", StringComparison.Ordinal);
        // HZN-3 — IsPersistedFile: neither a scratch view nor a design held in a project has a per-file camera.
        Assert.Matches(@"OfType<CircuitRF\.Ui\.ThreeD\.C3dEditorDocument>\(\)\)\s*if \(doc\.IsPersistedFile", v3[start..]);
    }

    /// <summary>Settings ▸ General ▸ Open in realistic view: a per-user preference, off by default, applied by the workspace
    /// after the editor starts — exactly where the toolbar button would act — and never read from the .c3d.</summary>
    [Fact]
    public void RealisticOnOpen_IsAPerUserPreference_AppliedAfterTheEditorStarts()
    {
        string td = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs"));
        int start = td.IndexOf("private void OpenC3dEditor(", StringComparison.Ordinal);
        Assert.Matches(@"vm\.Start\(\);\s*if \(Realistic3DPreference\.OnOpen\) vm\.Viewer\.IsRealistic = true;", td[start..]);

        bool was = Realistic3DPreference.TestOverrideActive;
        var stored = Realistic3DPreference.TestOverrideStore;
        try
        {
            Realistic3DPreference.TestOverrideActive = true;
            Realistic3DPreference.TestOverrideStore = null;
            Assert.False(Realistic3DPreference.OnOpen);
        }
        finally { Realistic3DPreference.TestOverrideActive = was; Realistic3DPreference.TestOverrideStore = stored; }
    }

    /// <summary>The cell menu's Open 3D View sits with the other Open items and runs the tree's command.</summary>
    [Fact]
    public void CellMenu_OffersOpen3DView()
    {
        string axaml = Src("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");
        int layout = axaml.IndexOf("Header=\"Open Layout\"", StringComparison.Ordinal);
        int threeD = axaml.IndexOf("Header=\"Open 3D View\"", StringComparison.Ordinal);
        Assert.True(layout > 0 && threeD > layout, "Open 3D View must follow Open Layout.");
        Assert.Contains("Command=\"{Binding OpenThreeDCommand}\"", axaml[threeD..(threeD + 200)]);
    }

    private static string Src(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
