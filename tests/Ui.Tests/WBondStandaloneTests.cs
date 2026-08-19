using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-E M1/M2/M4 — the third binary's build configuration, its shell's own rules, the
/// reference-geometry report, and the macOS document-type declarations.
///
/// <para><b>Most of this is structural or source-level, deliberately.</b> An <c>Application</c>, a
/// <c>Window</c> and a <c>NativeMenu</c> attach cannot be constructed in this suite (this project's
/// tests must not touch the Avalonia runtime), so the properties that matter are asserted over the
/// real XAML, the real csproj and the real plists — the same fallback every prior phase's
/// menu/dialog work in this repository has used. What CAN be driven headlessly —
/// <see cref="WBondReferenceGeometry"/> and the Touchstone export — is driven for real.</para>
/// </summary>
public class WBondStandaloneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wbond-standalone-" + Guid.NewGuid().ToString("N")[..8]);

    public WBondStandaloneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static string ReadRepoFile(params string[] parts) => File.ReadAllText(RepoFile(parts));

    /// <summary>
    /// A C# source file with its comments removed.
    ///
    /// <para><b>Required, not tidiness</b> — this is the trap H8 already recorded once: a file that
    /// deliberately does NOT do something says so in its own doc comment, so a scan asserting the
    /// absence of <c>WorkspaceWindow</c> finds it in the sentence explaining that there isn't one.
    /// Only executable text is scanned.</para>
    /// </summary>
    private static string ReadRepoCode(params string[] parts)
    {
        string text = ReadRepoFile(parts);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join("\n", text.Split('\n')
            .Select(line => { int i = line.IndexOf("//", StringComparison.Ordinal); return i < 0 ? line : line[..i]; }));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M1 — the third binary exists, and the build configuration says so
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// WB39 / R-wbe-1 — <c>&lt;StartupObject&gt;</c> is named for EVERY <c>CrfApp</c> value including
    /// the default. This project sets <c>TreatWarningsAsErrors</c>, so a third <c>Main</c> is CS0017
    /// the moment one is missing — verified directly by deleting the wbond line, which reports
    /// <c>error CS0017</c> against circuitRF's OWN Program.cs.
    /// </summary>
    [Fact]
    public void EveryCrfAppValue_NamesItsOwnStartupObject_IncludingTheDefault()
    {
        string csproj = ReadRepoFile("src", "Ui", "CircuitRF.Ui.csproj");

        Assert.Contains("<StartupObject Condition=\"'$(CrfApp)' == 'circuitrf'\">CircuitRF.Ui.Program<", csproj);
        Assert.Contains("<StartupObject Condition=\"'$(CrfApp)' == 'harmonica'\">CircuitRF.Ui.ProgramHarmonica<", csproj);
        Assert.Contains("<StartupObject Condition=\"'$(CrfApp)' == 'wbond'\">CircuitRF.Ui.ProgramWBond<", csproj);
    }

    /// <summary>A typo in <c>CrfApp</c> must be an MSBuild error, never a silent circuitRF build.</summary>
    [Fact]
    public void AnUnknownCrfAppValue_IsAnMsbuildError_NamingTheThreeThatAreValid()
    {
        string csproj = ReadRepoFile("src", "Ui", "CircuitRF.Ui.csproj");

        Assert.Contains(
            "'$(CrfApp)' != 'circuitrf' and '$(CrfApp)' != 'harmonica' and '$(CrfApp)' != 'wbond'",
            csproj);
        Assert.Contains("CrfApp must be 'circuitrf', 'harmonica' or 'wbond'", csproj);
    }

    /// <summary>
    /// WB40 — the assembly name stays <c>CircuitRF.Ui</c> for all three. RfCore's
    /// <c>InternalsVisibleTo</c> names it, so a rename loses <c>SNP.CreateBroken</c>/<c>RefreshFrom</c>
    /// and the Data Display half stops compiling. Asserted as the ABSENCE of an override, because that
    /// is what actually keeps it true.
    /// </summary>
    [Fact]
    public void TheAssemblyName_IsNeverOverriddenPerApp()
    {
        string csproj = ReadRepoFile("src", "Ui", "CircuitRF.Ui.csproj");
        Assert.DoesNotContain("<AssemblyName", csproj);

        // …and RfCore still grants it, which is the half that would break silently.
        Assert.Contains("CircuitRF.Ui", ReadRepoFile("src", "RfCore", "RfCore.csproj"));
    }

    /// <summary>
    /// <b>R-wbe-2 — the style/resource superset holds BY CONSTRUCTION, and the check is structural
    /// rather than a grep for one StyleInclude.</b>
    ///
    /// <para>The way this fails is silent: omit the ColorPicker Fluent include and <c>ColorView</c>
    /// renders as an empty box with no error. The wBond editor consumes MORE of the shared surface
    /// than harmonicaRF does — the Layout Editor canvas and renderer, the Properties dock's wire
    /// inspector, the DRC panel and the technology resolution behind the reference geometry are all
    /// application-scope consumers — so "did somebody remember to mirror it" is not a check worth
    /// resting on. All three Applications include the same two files and none declares an
    /// application-scope style or resource of its own.</para>
    /// </summary>
    [Fact]
    public void AllThreeApplications_IncludeTheSameStylesAndResources_AndDeclareNoneOfTheirOwn()
    {
        var shared = new[]
        {
            "avares://CircuitRF.Ui/Styles/CircuitRfResources.axaml",
            "avares://CircuitRF.Ui/Styles/CircuitRfStyles.axaml",
        };

        foreach (string app in new[] { "App.axaml", "HarmonicaApp.axaml", "WBondApp.axaml" })
        {
            var doc = XDocument.Parse(ReadRepoFile("src", "Ui", app));

            var included = doc.Descendants()
                .Where(e => e.Name.LocalName is "ResourceInclude" or "StyleInclude")
                .Select(e => (string?)e.Attribute("Source"))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            foreach (string file in shared)
                Assert.True(included.Contains(file), $"{app} does not include {file}.");

            // The application-scope blocks may hold merged dictionaries and includes, and nothing
            // else: a colour, a brush or a Style declared here exists in ONE application only, which
            // is exactly the divergence this rule prevents.
            foreach (string block in new[] { "Resources", "Styles" })
            {
                var root = doc.Root!.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "Application." + block);
                if (root is null) continue;

                foreach (var declared in root.Descendants())
                {
                    string n = declared.Name.LocalName;
                    bool structural = n is "ResourceDictionary" or "ResourceDictionary.MergedDictionaries"
                                        or "ResourceInclude" or "StyleInclude";
                    Assert.True(structural,
                        $"{app} declares an application-scope <{n}> of its own. All three " +
                        "Applications must share ONE style/resource set — see R-wbe-2.");
                }
            }
        }
    }

    /// <summary>
    /// R-wbe-1's second half — <c>WBondApp</c> stands up no workspace, no launch action and claims no
    /// <c>.crfw</c>, but KEEPS both <c>ProcessExit</c> cleanups and the saved theme.
    ///
    /// <para>The PCell cleanup is the wBond-specific one and is not optional: a wBond's reference
    /// geometry can hold PCells, whose kit resolvers own an interpreter process each.</para>
    /// </summary>
    [Fact]
    public void TheStandaloneApplication_StandsUpNoWorkspace_AndKeepsBothProcessExitCleanups()
    {
        string app = ReadRepoCode("src", "Ui", "WBondApp.axaml.cs");

        Assert.DoesNotContain("WorkspaceWindow", app);
        Assert.DoesNotContain("WorkspaceViewModel", app);
        Assert.DoesNotContain("LaunchAction", app);
        Assert.DoesNotContain("ProcessTechnologyRecognizers", app);
        Assert.DoesNotContain(".crfw", app);

        Assert.Contains("ExternalDeviceRegistry.ResetResolved", app);
        Assert.Contains("PCellRegistry.ClearResolvers", app);
        Assert.Contains("ThemeResolver.SetBuiltInProvider", app);
        Assert.Contains("ActiveThemeName", app);
    }

    /// <summary>
    /// Startup files land on ONE method regardless of platform: argv on Windows and Linux,
    /// <c>IActivatableLifetime</c> on macOS.
    /// </summary>
    [Fact]
    public void StartupFiles_ArriveByBothRoutes_AndLandOnTheSameShellMethod()
    {
        string app = ReadRepoCode("src", "Ui", "WBondApp.axaml.cs");
        string program = ReadRepoFile("src", "Ui", "ProgramWBond.cs");

        Assert.Contains("WBondApp.StartupFiles", program);
        Assert.Contains("IActivatableLifetime", app);
        Assert.Contains("FileActivatedEventArgs", app);

        // Both routes reach a shell window's own open, never a second loader.
        Assert.Contains("shell.OpenWBond(path)", app);
        Assert.Contains("WBondShellWindow.OpenInNewWindow(path)", app);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M2 — the shell, and the one file both binaries open
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>M5's structural half.</b> Both binaries open a <c>.wBond</c> through the SAME
    /// <see cref="WBondDocument.Open"/> — so "two binaries, one file" holds by construction rather
    /// than by two implementations agreeing today.
    /// </summary>
    [Fact]
    public void BothBinaries_OpenAWBondThroughTheSameDocumentOpen()
    {
        Assert.Contains("WBondDocument.Open(",
            ReadRepoFile("src", "Ui", "Views", "WBond", "WBondShellWindow.axaml.cs"));
        Assert.Contains("WBondDocument.Open(",
            ReadRepoFile("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
    }

    /// <summary>
    /// R-wbe-3 — the shell is a plain <c>Window</c> hosting the EXISTING editor view, with no Dock.
    /// The fixed-grid answer: the editor was already a Grid with GridSplitters, so bringing Dock
    /// along would have imported the whole tool-window lifecycle for no gain.
    /// </summary>
    [Fact]
    public void TheShell_IsAPlainWindowOverTheExistingEditorView_WithNoDock()
    {
        string xaml = ReadRepoFile("src", "Ui", "Views", "WBond", "WBondShellWindow.axaml");

        Assert.Contains("<Window", xaml);
        Assert.Contains("WBondEditorView", xaml);
        Assert.DoesNotContain("DockControl", xaml);
        Assert.DoesNotContain("dockCtrl", xaml);

        // The editor's own splits are its own; the shell adds no second layout mechanism.
        string editor = ReadRepoFile("src", "Ui", "Views", "WBond", "WBondEditorView.axaml");
        Assert.Contains("GridSplitter", editor);
    }

    /// <summary>
    /// R-wbe-4 — the macOS menu bar is attached PER WINDOW, guarded by a type-NAME comparison so a
    /// docked tab never replaces circuitRF's own bar.
    ///
    /// <para>Pinned because a rename would otherwise silently stop the menu bar appearing with
    /// nothing failing to compile — the exact failure H8 recorded and this copies.</para>
    /// </summary>
    [Fact]
    public void TheMenuBar_AttachesPerWindow_GuardedByATypeNameComparison()
    {
        string menu = ReadRepoFile("src", "Ui", "Views", "WBond", "WBondMenuView.axaml.cs");

        Assert.Contains("NativeMenu.SetMenu(window, menu)", menu);
        Assert.Contains("WorkspaceWindowTypeName = \"WorkspaceWindow\"", menu);
        Assert.Contains("window.GetType().Name == WorkspaceWindowTypeName", menu);

        // The guard is only meaningful if the type it names still exists under that name.
        Assert.Equal("WorkspaceWindow", typeof(Views.WorkspaceWindow).Name);
    }

    /// <summary>
    /// M2's File menu exists on BOTH surfaces. Anything present on one only exists on one platform,
    /// which is the failure both hand-mirrored bars in this repository already guard against.
    /// </summary>
    [Theory]
    [InlineData("NewDocumentCommand")]
    [InlineData("OpenDocumentCommand")]
    [InlineData("SaveDocumentCommand")]
    [InlineData("SaveDocumentAsCommand")]
    [InlineData("CloseWindowCommand")]
    [InlineData("ImportWireTableCommand")]
    [InlineData("ImportWiresDxfCommand")]
    [InlineData("ExportDxfCommand")]
    [InlineData("ExportTouchstoneCommand")]
    [InlineData("PreferencesCommand")]
    public void EveryMenuCommand_AppearsOnBothSurfaces(string command)
    {
        var doc = XDocument.Parse(ReadRepoFile("src", "Ui", "Views", "WBond", "WBondMenuView.axaml"));

        int native = doc.Descendants()
            .Count(e => e.Name.LocalName == "NativeMenuItem" &&
                        ((string?)e.Attribute("Command"))?.Contains(command) == true);

        int inWindow = doc.Descendants()
            .Count(e => e.Name.LocalName == "MenuItem" &&
                        ((string?)e.Attribute("Command"))?.Contains(command) == true);

        Assert.True(native >= 1, $"{command} is missing from the macOS NativeMenu.");
        Assert.True(inWindow >= 1, $"{command} is missing from the in-window Menu.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M2 / R-wbe-6 — references that resolve to nothing are reported, then re-pointed
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Writes a cell whose layout carries one rect, and returns its folder.</summary>
    private string MakeCell(string parent, string name)
    {
        Directory.CreateDirectory(parent);
        string cellDir = CellFolder.CreateCellFolder(parent, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var view = new LayoutView();
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, Layer = new LayerKey(1, 0) });

        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), view);
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName),
                                   new CcellFile { PrimaryLayout = name + ".clay" });

        CellLayoutResolver.InvalidateUnder(_root);
        return cellDir;
    }

    private (LayoutView View, string BaseDir) MakeRoot(params string[] cellRefs)
    {
        string rootLayoutDir = Path.Combine(_root, "__top", CellFolder.LayoutSubFolder);
        Directory.CreateDirectory(rootLayoutDir);

        var view = new LayoutView();
        foreach (string cellRef in cellRefs)
            view.Instances.Add(new LayoutInstance { CellRef = cellRef });

        return (view, rootLayoutDir);
    }

    /// <summary>A design whose references all resolve reports nothing — silence is the normal case.</summary>
    [Fact]
    public void AResolvableDesign_ReportsNoUnresolvedReferences()
    {
        string cell = MakeCell(_root, "Pad");
        string rootLayoutDir = Path.Combine(_root, "__top", CellFolder.LayoutSubFolder);
        Directory.CreateDirectory(rootLayoutDir);

        var (view, baseDir) = MakeRoot(Path.GetRelativePath(rootLayoutDir, cell));

        Assert.Empty(WBondReferenceGeometry.Unresolved(view, baseDir));
    }

    /// <summary>
    /// WB35 — an unresolved reference is REPORTED BY NAME. The name is what makes the report
    /// actionable; a count tells a user nothing about where to look.
    /// </summary>
    [Fact]
    public void AnUnresolvableReference_IsReportedByName_NotMerelyCounted()
    {
        var (view, baseDir) = MakeRoot("../../NotHere", "../../AlsoNotHere");

        var missing = WBondReferenceGeometry.Unresolved(view, baseDir);

        Assert.Equal(2, missing.Count);
        Assert.Contains("../../NotHere", missing);
        Assert.Contains("../../AlsoNotHere", missing);
    }

    /// <summary>
    /// A design with no instances at all reports nothing. Having no reference geometry is one of the
    /// two states that open completely standalone — it is not a missing reference.
    /// </summary>
    [Fact]
    public void ADesignWithNoGeometryAtAll_IsNotReportedAsMissingAnything()
    {
        var (view, baseDir) = MakeRoot();
        Assert.Empty(WBondReferenceGeometry.Unresolved(view, baseDir));
    }

    /// <summary>
    /// The re-point: a folder naming the cells makes the reference resolve, and the design is left
    /// carrying a reference that actually points at them.
    /// </summary>
    [Fact]
    public void Repointing_AtAFolderHoldingTheCells_MakesTheReferenceResolve()
    {
        string library = Path.Combine(_root, "library");
        MakeCell(library, "Pad");

        var (view, baseDir) = MakeRoot("../../gone/Pad");
        Assert.Single(WBondReferenceGeometry.Unresolved(view, baseDir));

        int moved = WBondReferenceGeometry.Repoint(view, baseDir, library);

        Assert.Equal(1, moved);
        Assert.Empty(WBondReferenceGeometry.Unresolved(view, baseDir));
    }

    /// <summary>
    /// <b>Re-pointing never touches a reference that already resolved.</b> Moving the layout's own
    /// base directory would have been simpler and would turn a partial miss into a total one — so
    /// each unresolved instance is re-pointed individually.
    /// </summary>
    [Fact]
    public void Repointing_LeavesAlreadyResolvingReferencesExactlyWhereTheyWere()
    {
        string library = Path.Combine(_root, "library");
        MakeCell(library, "Pad");
        string good = MakeCell(_root, "Bump");

        string rootLayoutDir = Path.Combine(_root, "__top", CellFolder.LayoutSubFolder);
        Directory.CreateDirectory(rootLayoutDir);
        string goodRef = Path.GetRelativePath(rootLayoutDir, good);

        var (view, baseDir) = MakeRoot(goodRef, "../../gone/Pad");

        int moved = WBondReferenceGeometry.Repoint(view, baseDir, library);

        Assert.Equal(1, moved);
        Assert.Equal(goodRef, view.Instances[0].CellRef);
        Assert.Empty(WBondReferenceGeometry.Unresolved(view, baseDir));
    }

    /// <summary>
    /// <b>Nothing is written on a guess.</b> A folder that does not hold a cell of that name leaves
    /// every reference exactly as it was, so the report afterwards is still honest.
    /// </summary>
    [Fact]
    public void Repointing_AtAFolderThatHoldsNothing_ChangesNothingAndStaysHonest()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var (view, baseDir) = MakeRoot("../../gone/Pad");

        int moved = WBondReferenceGeometry.Repoint(view, baseDir, empty);

        Assert.Equal(0, moved);
        Assert.Equal("../../gone/Pad", view.Instances[0].CellRef);
        Assert.Single(WBondReferenceGeometry.Unresolved(view, baseDir));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M4 — packaging and identity
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a plist's own dictionary, with comments stripped first.
    ///
    /// <para><b>Stripped rather than reworded, deliberately.</b> These files carry
    /// <c>--entitlements</c> inside a comment — which <c>plutil</c> accepts and <c>System.Xml</c>
    /// refuses, since XML forbids <c>--</c> in a comment. The comment is the useful half (it records
    /// the three-place bundle-identifier trap), so the test adapts rather than making a
    /// documentation file worse to satisfy a parser.</para>
    /// </summary>
    private static XElement Plist(string file)
    {
        string text = ReadRepoFile("src", "Ui", "Assets", "macOS", file);
        string stripped = System.Text.RegularExpressions.Regex.Replace(
            text, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        return XDocument.Parse(stripped).Root!.Element("dict")!;
    }

    private static IEnumerable<XElement> DictsUnder(XElement plistDict, string key)
    {
        var array = plistDict.Elements()
            .SkipWhile(e => !(e.Name == "key" && e.Value == key))
            .Skip(1)
            .FirstOrDefault();

        return array?.Name == "array" ? array.Elements("dict") : [];
    }

    private static string? StringFor(XElement dict, string key) =>
        dict.Elements()
            .SkipWhile(e => !(e.Name == "key" && e.Value == key))
            .Skip(1)
            .FirstOrDefault(e => e.Name == "string")?.Value;

    /// <summary>
    /// R-wbe-7 — <c>.wBond</c> is EXPORTED by exactly one application and IMPORTED by the other.
    /// Two applications both exporting one UTI is what Launch Services cannot arbitrate.
    /// </summary>
    [Fact]
    public void TheWBondType_IsExportedByCircuitRfOnly_AndImportedByTheStandalone()
    {
        const string uti = "com.circuitrf.wbond";

        var exported = DictsUnder(Plist("Info.plist"), "UTExportedTypeDeclarations")
            .Select(d => StringFor(d, "UTTypeIdentifier")).ToList();
        Assert.Contains(uti, exported);

        var imported = DictsUnder(Plist("WBond-Info.plist"), "UTImportedTypeDeclarations")
            .Select(d => StringFor(d, "UTTypeIdentifier")).ToList();
        Assert.Contains(uti, imported);

        // Exported by ONE application only — the standalone imports, it does not define.
        var standaloneExports = DictsUnder(Plist("WBond-Info.plist"), "UTExportedTypeDeclarations")
            .Select(d => StringFor(d, "UTTypeIdentifier")).ToList();
        Assert.DoesNotContain(uti, standaloneExports);

        // harmonicaRF has no business opening a .wBond, so it declares the type nowhere at all.
        Assert.DoesNotContain(uti, Plist("Harmonica-Info.plist").ToString());
    }

    /// <summary>
    /// circuitRF's role is Viewer and the standalone's is Editor. Both declaring themselves the
    /// editor makes which one opens a double-click a matter of whichever Launch Services saw last.
    /// </summary>
    [Fact]
    public void TheDocumentTypeRoles_AreViewerInCircuitRf_AndEditorInTheStandalone()
    {
        const string uti = "com.circuitrf.wbond";

        var inCircuitRf = DictsUnder(Plist("Info.plist"), "CFBundleDocumentTypes")
            .First(d => (StringFor(d, "CFBundleTypeName") ?? "").Contains("wBond"));
        Assert.Equal("Viewer", StringFor(inCircuitRf, "CFBundleTypeRole"));
        Assert.Contains(uti, inCircuitRf.ToString());

        var inStandalone = DictsUnder(Plist("WBond-Info.plist"), "CFBundleDocumentTypes")
            .First(d => (StringFor(d, "CFBundleTypeName") ?? "").Contains("wBond"));
        Assert.Equal("Editor", StringFor(inStandalone, "CFBundleTypeRole"));
    }

    /// <summary>
    /// The standalone claims NO <c>.crfw</c> and no <c>.charm</c> — offering an application that
    /// cannot open a workspace in "Open With" for every workspace on the machine would be a lie.
    /// </summary>
    [Fact]
    public void TheStandaloneClaimsNeitherWorkspaceNorHarmonicaDocuments()
    {
        string plist = Plist("WBond-Info.plist").ToString();
        Assert.DoesNotContain("com.circuitrf.crfw", plist);
        Assert.DoesNotContain("com.circuitrf.charm", plist);
    }

    /// <summary>
    /// R-h8-9's three-place trap, applied a third time: the identifier lives in the plist, in the
    /// bundle script and in codesign's arguments, and nothing derives one from another. All three
    /// bundles must also differ — two applications claiming one identifier collide in Launch
    /// Services, in the Dock and in the quarantine database, and every symptom is remote from the
    /// cause.
    /// </summary>
    [Fact]
    public void EveryBundleIdentifier_IsDistinct_AndTheScriptAgreesWithItsOwnPlist()
    {
        string circuitRf  = StringFor(Plist("Info.plist"), "CFBundleIdentifier")!;
        string harmonica  = StringFor(Plist("Harmonica-Info.plist"), "CFBundleIdentifier")!;
        string wbond      = StringFor(Plist("WBond-Info.plist"), "CFBundleIdentifier")!;

        Assert.Equal(3, new HashSet<string>([circuitRf, harmonica, wbond]).Count);

        string script = ReadRepoFile("src", "Ui", "bundleForWBondMacOS.sh");
        Assert.Contains($"BUNDLE_ID=\"{wbond}\"", script);

        // The script is the one place the plist and its own constant are actually compared.
        Assert.Contains("PLIST_ID=$(/usr/libexec/PlistBuddy", script);
        Assert.Contains("if [ \"$PLIST_ID\" != \"$BUNDLE_ID\" ]", script);

        // …and it records the publish command in the repository rather than in a shell history.
        Assert.Contains("dotnet publish -r $RID -c Release --self-contained -p:CrfApp=wbond", script);
    }

    /// <summary>
    /// <b>The executable inside every bundle is named after the APPLICATION, and its plist and its
    /// bundle script must say the same name.</b> A bundle whose <c>CFBundleExecutable</c> is not a
    /// file in <c>Contents/MacOS/</c> does not launch at all, and says so only in the system log.
    ///
    /// <para>This used to assert the shared ASSEMBLY name (WB40) in all three, because that is what
    /// <c>dotnet publish</c> named the native host. The assembly is still shared and still called
    /// <c>CircuitRF.Ui</c> — WB40 is unchanged — but shipping a <c>CircuitRF.Ui</c> binary put a
    /// build-system detail in front of users on every platform, so <c>CircuitRF.Ui.csproj</c>'s
    /// <c>CrfRenameApphost</c> target renames the host after publish. The invariant is no longer
    /// "all three agree with the assembly" but "each agrees with its own script".</para>
    /// </summary>
    [Theory]
    [InlineData("Info.plist", "bundleForMacOS.sh", "circuitRF")]
    [InlineData("Harmonica-Info.plist", "bundleForHarmonicaMacOS.sh", "harmonicaRF")]
    [InlineData("WBond-Info.plist", "bundleForWBondMacOS.sh", "wBond")]
    public void EveryBundle_NamesItsOwnRenamedHostAsItsExecutable(string plist, string script, string expected)
    {
        Assert.Equal(expected, StringFor(Plist(plist), "CFBundleExecutable"));
        Assert.Contains($"EXECUTABLE_NAME=\"{expected}\"", ReadRepoFile("src", "Ui", script));
    }

    /// <summary>
    /// The three published host names are distinct, and none of them is the assembly name — two
    /// applications sharing an executable name inside <c>/Applications</c> is how one ends up
    /// launching under the other's identity.
    /// </summary>
    [Fact]
    public void TheThreeRenamedHosts_AreDistinct_AndNoneIsTheAssemblyName()
    {
        string[] names =
        [
            StringFor(Plist("Info.plist"), "CFBundleExecutable")!,
            StringFor(Plist("Harmonica-Info.plist"), "CFBundleExecutable")!,
            StringFor(Plist("WBond-Info.plist"), "CFBundleExecutable")!,
        ];

        Assert.Equal(3, new HashSet<string>(names).Count);
        Assert.DoesNotContain("CircuitRF.Ui", names);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Windows and Linux — the two platforms with no .app bundle to lean on
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A plist that CLAIMS a document type obliges the dispatcher to open it.</b>
    ///
    /// <para>This caught a real bug: circuitRF's <c>Info.plist</c> gained a Viewer role for
    /// <c>.wBond</c> in M4, but <c>App.OpenFiles</c> still handled only <c>.crfw</c>/<c>.cws</c> and
    /// <c>.charm</c> — so "double-click a .wBond while circuitRF is running and it opens there"
    /// (M4's own gate) launched circuitRF and opened nothing. That is precisely the failure
    /// <c>OpenFiles</c>' own note says it exists to have fixed, reached from a new direction: a type
    /// was declared without the dispatcher being told.</para>
    ///
    /// <para>The check is on the ONE dispatcher every arrival route funnels through — argv, the
    /// macOS Apple Event and the Windows second-instance pipe — so it covers all three platforms.</para>
    /// </summary>
    [Fact]
    public void EveryDocumentTypeCircuitRfClaims_IsActuallyHandledByItsOpenFilesDispatcher()
    {
        string app = ReadRepoCode("src", "Ui", "App.axaml.cs");

        // Extensions the plist claims, lower-cased the way the dispatcher compares them.
        var claimed = DictsUnder(Plist("Info.plist"), "CFBundleDocumentTypes")
            .SelectMany(d => d.Descendants("string").Select(s => s.Value))
            .Where(v => v is "crfw" or "cws" or "charm" or "wBond" or "wbond")
            .Select(v => v.ToLowerInvariant())
            .Distinct();

        foreach (string ext in claimed)
            Assert.True(app.Contains($"case \".{ext}\":", StringComparison.Ordinal),
                $"Info.plist claims *.{ext} but App.OpenFiles has no case for it — double-clicking " +
                "one would launch circuitRF and open nothing, which reads as a broken file.");
    }

    /// <summary>
    /// <b>Windows reads the application's identity out of the BINARY, not out of its file name.</b>
    ///
    /// <para><c>Description</c> becomes the executable's File Description — the name Windows shows in
    /// Task Manager and in the file's Properties — and <c>Product</c> its product name. When this was
    /// written, WB40 forbade changing the assembly name and these were the ONLY things telling three
    /// identically-named <c>CircuitRF.Ui.exe</c> files apart. The published host is now renamed per
    /// app (<c>CrfRenameApphost</c>), so the file names differ too — but the metadata still has to be
    /// per-app, because the File Description is what Task Manager shows and it is read from inside the
    /// binary regardless of what the file is called.</para>
    /// </summary>
    [Fact]
    public void EveryAppHasItsOwnWindowsIdentityMetadata()
    {
        string csproj = ReadRepoFile("src", "Ui", "CircuitRF.Ui.csproj");

        foreach (var (app, product) in new[]
                 { ("circuitrf", "circuitRF"), ("harmonica", "harmonicaRF"), ("wbond", "wBond") })
        {
            Assert.Contains($"<PropertyGroup Condition=\"'$(CrfApp)' == '{app}'\">", csproj);
            Assert.Contains($"<Product>{product}</Product>", csproj);
        }

        // One Description per app, and none of them shared: three identical File Descriptions in Task
        // Manager is the state this replaced.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(csproj, "<Description>").Count);
    }

    /// <summary>
    /// The Windows PE icon is chosen PER APP. It was platform-conditioned only, so all three
    /// executables would have embedded circuitRF's icon the moment one existed — R-h8-9's rule
    /// ("two apps sharing an icon is its own kind of wrong") unenforced on Windows.
    /// </summary>
    [Fact]
    public void TheWindowsExecutableIcon_IsChosenPerApp_NotSharedAcrossAllThree()
    {
        string csproj = ReadRepoFile("src", "Ui", "CircuitRF.Ui.csproj");

        foreach (string icon in new[] { "circuitRFIcon.ico", "harmonicaRFIcon.ico", "wBondIcon.ico" })
            Assert.Contains(icon, csproj);

        // Every ApplicationIcon line is gated on which app is being built.
        foreach (var line in csproj.Split('\n').Where(l => l.Contains("<ApplicationIcon")))
            Assert.Contains("$(CrfApp)", line);
    }

    /// <summary>
    /// <b>Linux: an application is its <c>.desktop</c> file.</b> Without one it has no menu entry and
    /// its documents do not open by double-click — the Linux equivalent of shipping no plist.
    /// Each entry claims only the type its own application can open.
    /// </summary>
    [Theory]
    [InlineData("circuitrf.desktop",   "circuitRF",   "application/x-circuitrf-workspace")]
    [InlineData("harmonicarf.desktop", "harmonicaRF", "application/x-harmonicarf-document")]
    [InlineData("wbond.desktop",       "wBond",       "application/x-wbond-design")]
    public void EveryApplicationHasItsOwnLinuxDesktopEntry(string file, string name, string mime)
    {
        string desktop = ReadRepoFile("src", "Ui", "linux", file);

        Assert.Contains($"Name={name}", desktop);
        Assert.Contains(mime, desktop);
        Assert.Contains("Type=Application", desktop);

        // The three binaries are all called CircuitRF.Ui, so each Exec= must name a RENAMED one —
        // three entries pointing at one path would make the menu three ways to launch one app.
        Assert.Contains("Exec=/usr/bin/", desktop);
        Assert.DoesNotContain("CircuitRF.Ui", desktop.Split("# NOTE")[0]);
    }

    /// <summary>
    /// The MIME types the three <c>.desktop</c> files claim are all actually DEFINED — a desktop entry
    /// naming a type nothing declares associates with nothing at all, silently.
    /// </summary>
    [Fact]
    public void EveryMimeTypeADesktopEntryClaims_IsDefinedInTheMimeFile()
    {
        string mimeXml = ReadRepoFile("src", "Ui", "linux", "circuitrf-mime.xml");

        foreach (string file in new[] { "circuitrf.desktop", "harmonicarf.desktop", "wbond.desktop" })
        {
            string desktop = ReadRepoFile("src", "Ui", "linux", file);
            string line = desktop.Split('\n').First(l => l.StartsWith("MimeType=", StringComparison.Ordinal));

            foreach (string mime in line["MimeType=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                Assert.True(mimeXml.Contains($"type=\"{mime}\"", StringComparison.Ordinal),
                    $"{file} claims {mime}, which circuitrf-mime.xml does not define.");
        }
    }
}
