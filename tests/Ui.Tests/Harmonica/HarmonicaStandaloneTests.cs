// ================================================================
//  HarmonicaStandaloneTests.cs  —  M2 and M3's gates, brief-harmonicarf-h8
//
//  R-h8-5  TWO Mains in one assembly, with StartupObject set EXPLICITLY for BOTH configurations.
//  R-h8-6  the standalone Application's style/resource set is a SUPERSET of what harmonicaRF's own
//          views reach — gated BY CONSTRUCTION (one shared file, both Applications) rather than by
//          grepping for one StyleInclude, which would pass the day a second dependency landed.
//  R-h8-7  what the standalone must NOT do, and the two ProcessExit cleanups it must still do.
//  R-h8-8  the shell is a plain Window, and H7's menu already attaches its NativeMenu to it.
//  R-h8-9  harmonicaRF is a DIFFERENT application: its own bundle id, name and icon.
//  R-h8-10 .charm gets a UTI and a document type on BOTH binaries.
//
//  These are source and manifest scans. An Application, a Window and an .app bundle have no
//  headlessly assertable output — this codebase's own long-standing fallback (see
//  LayoutContextMenuStackingTests) — so what is pinned is the STRUCTURE that makes the behaviour
//  hold, and the interactive half is reported as unverified rather than implied.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaStandaloneTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.True(dir.Length > 0, "could not locate the repository root");
        return dir;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>
    /// C# source with comment LINES removed. Every "must not appear" assertion below runs through this,
    /// because the files under test deliberately NAME what they must not do — <c>HarmonicaApp</c>'s own
    /// doc comment lists <c>WorkspaceWindow</c>, <c>ApplyLaunchSettings</c> and <c>.crfw</c> as the things
    /// it does not stand up. A raw text scan would fail on the documentation of the very rule it checks.
    /// </summary>
    private static string CodeOnly(string source) => string.Join(
        '\n',
        source.Split('\n').Where(l =>
        {
            string t = l.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal)
                && !t.StartsWith("*",  StringComparison.Ordinal)
                && !t.StartsWith("/*", StringComparison.Ordinal);
        }));

    /// <summary>XML/AXAML/plist text with <c>&lt;!-- --&gt;</c> comments removed, for the same reason.</summary>
    private static string XmlCodeOnly(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    // ══ R-h8-5 — two entry points, both named explicitly ═════════════════════

    [Fact]
    public void BothConfigurations_NameTheirStartupObjectExplicitly()
    {
        string csproj = Read("src/Ui/CircuitRF.Ui.csproj");

        Assert.Contains("<StartupObject Condition=\"'$(CrfApp)' == 'circuitrf'\">CircuitRF.Ui.Program</StartupObject>",
                        csproj, StringComparison.Ordinal);
        Assert.Contains("<StartupObject Condition=\"'$(CrfApp)' == 'harmonica'\">CircuitRF.Ui.ProgramHarmonica</StartupObject>",
                        csproj, StringComparison.Ordinal);

        // The DEFAULT case is named too. Relying on "there is only one Main today" is what breaks the
        // moment the second one lands — which is now.
        Assert.Contains("<CrfApp Condition=\"'$(CrfApp)' == ''\">circuitrf</CrfApp>",
                        csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherEntryPoint_SuppressesTheMultipleEntryPointWarning()
    {
        string csproj = XmlCodeOnly(Read("src/Ui/CircuitRF.Ui.csproj"));

        // TreatWarningsAsErrors stays on, and the multiple-entry-point warning is not silenced
        // anywhere — the StartupObject is what resolves it, not a suppression.
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<NoWarn", csproj, StringComparison.Ordinal);

        foreach (string f in new[] { "src/Ui/Program.cs", "src/Ui/ProgramHarmonica.cs" })
            Assert.DoesNotContain("#pragma warning disable", Read(f), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStandalone_IsTheSameAssembly_NotARenamedOne()
    {
        // §3.1: "the standalone binary does not weaken [the firewall]: it is src/Ui with a different
        // Main". Renaming the assembly also loses RfCore's InternalsVisibleTo("CircuitRF.Ui") and the
        // Data Display half stops compiling — found by building it.
        string csproj = XmlCodeOnly(Read("src/Ui/CircuitRF.Ui.csproj"));
        Assert.DoesNotContain("<AssemblyName", csproj, StringComparison.Ordinal);
    }

    // ══ R-h8-6 — the style set is a superset BY CONSTRUCTION ═════════════════

    [Fact]
    public void BothApplications_IncludeTheSameStyleAndResourceFiles()
    {
        string app  = Read("src/Ui/App.axaml");
        string harm = Read("src/Ui/HarmonicaApp.axaml");

        foreach (string shared in new[] { "Styles/CircuitRfStyles.axaml", "Styles/CircuitRfResources.axaml" })
        {
            Assert.Contains(shared, app,  StringComparison.Ordinal);
            Assert.Contains(shared, harm, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The superset claim, made structural: NEITHER Application may declare an application-scope
    /// style or resource of its own. With every one in the shared files, "harmonicaRF carries
    /// everything circuitRF does" is true by definition rather than by comparison — and a second
    /// dependency added to one cannot fall out of the other, which is the exact failure R-h8-6 names.
    /// </summary>
    [Fact]
    public void NeitherApplication_DeclaresAnApplicationScopeStyleOrResourceOfItsOwn()
    {
        foreach (string file in new[] { "src/Ui/App.axaml", "src/Ui/HarmonicaApp.axaml" })
        {
            var doc = XDocument.Parse(Read(file));

            var styles = doc.Descendants().First(e => e.Name.LocalName == "Application.Styles");
            var styleChildren = styles.Elements().Select(e => e.Name.LocalName).ToArray();
            Assert.Equal(["StyleInclude"], styleChildren);

            var resources = doc.Descendants().First(e => e.Name.LocalName == "Application.Resources");
            var declaredKeys = resources.Descendants()
                .Where(e => e.Name.LocalName != "ResourceDictionary"
                         && e.Name.LocalName != "ResourceDictionary.MergedDictionaries"
                         && e.Name.LocalName != "ResourceInclude")
                .ToArray();
            Assert.Empty(declaredKeys);

            output.WriteLine($"{file}: styles={string.Join(",", styleChildren)}, " +
                             $"own resource keys={declaredKeys.Length}");
        }
    }

    [Fact]
    public void TheSharedStyleFile_CarriesTheIncludeThatFailsSilently()
    {
        // §7.9.4's own gotcha: ColorView's template comes from
        // Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml — note .xaml, not .axaml — and
        // omitting it renders an empty box with NO error. Because both Applications include this one
        // file, the standalone colour editor is populated for the same reason circuitRF's is.
        string styles = Read("src/Ui/Styles/CircuitRfStyles.axaml");
        Assert.Contains("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml",
                        styles, StringComparison.Ordinal);
        Assert.Contains("<FluentTheme", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedResourceFile_CarriesEveryBrushHarmonicasOwnViewsBindTo()
    {
        string resources = Read("src/Ui/Styles/CircuitRfResources.axaml");

        // Every application-scope resource key harmonicaRF's own AXAML actually names, collected from
        // the views themselves rather than from a hand-written list — so a NEW binding added to a
        // harmonicaRF view and not declared anywhere fails this test rather than rendering blank.
        string[] harmonicaViews =
        [
            "src/Ui/Views/Harmonica/HarmonicaView.axaml",
            "src/Ui/Views/Harmonica/HarmonicaMenuView.axaml",
            "src/Ui/Views/Harmonica/ReadoutStripView.axaml",
            "src/Ui/Views/Harmonica/HarmonicaShellWindow.axaml",
            "src/Ui/Views/Dialogs/HarmonicaPreferencesDialog.axaml",
            "src/Ui/Views/Dialogs/HarmonicaTracePickerDialog.axaml",
            "src/Ui/Views/Dialogs/HarmonicaSetDutDialog.axaml",
        ];

        var referenced = harmonicaViews
            .SelectMany(v => System.Text.RegularExpressions.Regex
                .Matches(Read(v), @"DynamicResource\s+(Crf[A-Za-z0-9]+)")
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referenced);   // a vacuous pass would be a test of nothing
        foreach (string key in referenced)
        {
            Assert.Contains($"x:Key=\"{key}\"", resources, StringComparison.Ordinal);
            output.WriteLine($"harmonicaRF binds {key} — declared in the shared dictionary");
        }
    }

    // ══ R-h8-7 — what it must not do, and what it must still do ══════════════

    [Fact]
    public void TheStandaloneApplication_StandsUpNoWorkspaceAndNoLaunchAction()
    {
        string app = CodeOnly(Read("src/Ui/HarmonicaApp.axaml.cs"));

        foreach (string forbidden in new[]
                 {
                     "new WorkspaceWindow", "new WorkspaceViewModel",
                     "ApplyLaunchSettings", "ProcessTechnologyRecognizers", ".crfw",
                 })
            Assert.DoesNotContain(forbidden, app, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStandaloneApplication_KeepsBothProcessExitCleanups()
    {
        string app = Read("src/Ui/HarmonicaApp.axaml.cs");

        // The first is NOT optional: a leaked device worker on macOS holds a VM slot indefinitely and
        // the next run dies with a broken pipe and no worker output. harmonicaRF can hold an external
        // DUT, so it can leak one.
        Assert.Contains("ProcessExit += (_, _) => ExternalDeviceRegistry.ResetResolved()",
                        app, StringComparison.Ordinal);
        Assert.Contains("PCellRegistry.ClearResolvers()", app, StringComparison.Ordinal);

        // …and the theme, or every .ccolor the user has resolves to nothing.
        Assert.Contains("ThemeResolver.SetBuiltInProvider", app, StringComparison.Ordinal);
        Assert.Contains("ActiveThemeName", app, StringComparison.Ordinal);
    }

    // ══ R-h8-8 — the shell is a plain Window, and the menu finds it ══════════

    [Fact]
    public void TheShell_IsAPlainWindowHostingOneView_WithNoDock()
    {
        var doc = XDocument.Parse(Read("src/Ui/Views/Harmonica/HarmonicaShellWindow.axaml"));
        Assert.Equal("Window", doc.Root!.Name.LocalName);

        var children = doc.Root.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToArray();
        var child = Assert.Single(children);
        Assert.Equal("HarmonicaView", child.Name.LocalName);

        Assert.DoesNotContain("Dock", CodeOnly(Read("src/Ui/Views/Harmonica/HarmonicaShellWindow.axaml.cs")),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// R-h8-8 asks for this POSITIVELY, and the reason is that the check is a type-NAME comparison:
    /// the menu view deliberately takes no dependency on the workspace shell (which does not exist in
    /// the standalone build), so a rename of <c>WorkspaceWindow</c> would silently stop the macOS menu
    /// bar appearing in every torn-off and standalone window with nothing failing to compile.
    /// </summary>
    [Fact]
    public void TheMenuView_AttachesItsNativeMenu_ToAnyWindowThatIsNotTheWorkspaceShell()
    {
        string code = Read("src/Ui/Views/Harmonica/HarmonicaMenuView.axaml.cs");

        Assert.Contains("WorkspaceWindowTypeName = \"WorkspaceWindow\"", code, StringComparison.Ordinal);
        Assert.Contains("bool isWorkspaceWindow = window.GetType().Name == WorkspaceWindowTypeName;",
            code, StringComparison.Ordinal);
        // R3A §2.1's own RecomputeAttachment: a torn-off document window or the standalone shell
        // (!isWorkspaceWindow) always owns the window outright — the same case this test's own name
        // describes, reached by falling through the isWorkspaceWindow branch into
        // AttachToWindowOutright rather than a three-way desiredTarget ternary.
        Assert.Contains("AttachToWindowOutright(window);", code, StringComparison.Ordinal);
        Assert.Contains("NativeMenu.SetMenu(desiredTarget, _ownMenu);", code, StringComparison.Ordinal);

        // The name it compares against must still be a real type, or the comparison is against a
        // string nothing answers to and every window would get the menu bar — including the shell.
        Assert.NotNull(typeof(CircuitRF.Ui.Views.WorkspaceWindow));
        Assert.Equal("WorkspaceWindow", typeof(CircuitRF.Ui.Views.WorkspaceWindow).Name);
    }

    // ══ R-h8-9 / R-h8-10 — bundle identity and the .charm association ════════

    private static XElement PlistDict(string relative)
    {
        // Two things a plist carries that a strict XML reader will not take: the DOCTYPE names
        // Apple's external DTD (which XDocument.Parse refuses to fetch), and these files' own
        // comments contain "--" (the "--entitlements" note), which XML forbids inside a comment.
        // Neither matters here — only the element tree does.
        string text = System.Text.RegularExpressions.Regex.Replace(
            XmlCodeOnly(Read(relative)), "<!DOCTYPE.*?>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var doc = XDocument.Parse(text);
        return doc.Root!.Elements().First(e => e.Name.LocalName == "dict");
    }

    private static string? PlistString(XElement dict, string key)
    {
        var nodes = dict.Elements().ToList();
        for (int i = 0; i < nodes.Count - 1; i++)
            if (nodes[i].Name.LocalName == "key" && nodes[i].Value == key)
                return nodes[i + 1].Value;
        return null;
    }

    [Fact]
    public void TheTwoApps_HaveDifferentBundleIdentifiersNamesAndIcons()
    {
        var crf  = PlistDict("src/Ui/Assets/macOS/Info.plist");
        var harm = PlistDict("src/Ui/Assets/macOS/Harmonica-Info.plist");

        string?[] pairs =
        [
            PlistString(crf,  "CFBundleIdentifier"), PlistString(harm, "CFBundleIdentifier"),
            PlistString(crf,  "CFBundleName"),       PlistString(harm, "CFBundleName"),
            PlistString(crf,  "CFBundleIconFile"),   PlistString(harm, "CFBundleIconFile"),
        ];

        for (int i = 0; i < pairs.Length; i += 2)
        {
            Assert.False(string.IsNullOrWhiteSpace(pairs[i]));
            Assert.False(string.IsNullOrWhiteSpace(pairs[i + 1]));
            Assert.NotEqual(pairs[i], pairs[i + 1]);
        }

        output.WriteLine($"circuitRF  {PlistString(crf,  "CFBundleIdentifier")} / {PlistString(crf,  "CFBundleIconFile")}");
        output.WriteLine($"harmonicaRF {PlistString(harm, "CFBundleIdentifier")} / {PlistString(harm, "CFBundleIconFile")}");
    }

    [Fact]
    public void TheBundleScript_AgreesWithThePlistItSigns_AndSaysSoInThreePlaces()
    {
        string script = Read("src/Ui/bundleForHarmonicaMacOS.sh");
        string id = PlistString(PlistDict("src/Ui/Assets/macOS/Harmonica-Info.plist"), "CFBundleIdentifier")!;

        // The three-place trap R-h8-9 names: the plist, the script's BUNDLE_ID and the codesign
        // invocation. The script checks the first two against each other at bundle time; this checks
        // that the constant it checks against is the right one to begin with.
        Assert.Contains($"BUNDLE_ID=\"{id}\"", script, StringComparison.Ordinal);
        Assert.Contains("-p:CrfApp=harmonica", script, StringComparison.Ordinal);
        Assert.Contains("Harmonica-Info.plist", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CharmIsDeclaredOnBothBinaries_ExportedByOneAndImportedByTheOther()
    {
        string crf  = XmlCodeOnly(Read("src/Ui/Assets/macOS/Info.plist"));
        string harm = XmlCodeOnly(Read("src/Ui/Assets/macOS/Harmonica-Info.plist"));

        // One definition, two claimants: circuitRF EXPORTS the type, harmonicaRF IMPORTS it. Two
        // applications both exporting one UTI is what Launch Services cannot arbitrate.
        Assert.Contains("UTExportedTypeDeclarations", crf,  StringComparison.Ordinal);
        Assert.Contains("com.circuitrf.charm",        crf,  StringComparison.Ordinal);
        Assert.Contains("UTImportedTypeDeclarations", harm, StringComparison.Ordinal);
        Assert.Contains("com.circuitrf.charm",        harm, StringComparison.Ordinal);

        // Both OPEN one, which is the half that matters to a user.
        foreach (string plist in new[] { crf, harm })
        {
            Assert.Contains("CFBundleDocumentTypes", plist, StringComparison.Ordinal);
            Assert.Contains("<string>charm</string>", plist, StringComparison.Ordinal);
        }

        // …and harmonicaRF does NOT claim .crfw: there is no workspace in that build, so offering it
        // in "Open With" for every workspace on the machine would be a lie.
        Assert.DoesNotContain("crfw", harm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoApps_ShipDifferentIconArtwork()
    {
        string root = RepoRoot();
        string crf  = Path.Combine(root, "src/Ui/Assets/artwork/circuitRF-app-icon.svg");
        string harm = Path.Combine(root, "src/Ui/Assets/artwork/harmonicaRF-app-icon.svg");

        Assert.True(File.Exists(crf),  "circuitRF's own icon artwork is missing");
        Assert.True(File.Exists(harm), "harmonicaRF's own icon artwork is missing");
        Assert.NotEqual(File.ReadAllText(crf), File.ReadAllText(harm));
    }
}
