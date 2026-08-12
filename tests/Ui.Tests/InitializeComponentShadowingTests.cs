using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests;

// ── The x:Name fields are assigned by the GENERATED InitializeComponent, not by the loader ────────
//
// Avalonia's name generator emits, per view, an `internal` field per `x:Name` plus
// `public void InitializeComponent(bool loadXaml = true)` whose body is
// `AvaloniaXamlLoader.Load(this)` FOLLOWED BY one `Find<T>("Name")` assignment per field.
//
// A code-behind that writes its own `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);`
// still compiles — the two are different signatures — and C# overload resolution prefers the
// parameterless one over the one with an optional argument, so the hand-written method WINS. The XAML
// loads and renders correctly; every named field stays null. The failure surfaces much later, as a
// NullReferenceException the first time the view touches one of its own controls. That is exactly
// what crashed Tools ▸ wBond (`WBondEditorView.OnDataContextChanged` → `LayoutCanvasCtrl` null), and
// the same shadowing was latent in nine other views plus two dialogs that called the loader straight
// from their constructor.
//
// This scan is the gate: no view may declare `InitializeComponent`, and no view may call
// `AvaloniaXamlLoader.Load(this)` at all. An `Application` subclass legitimately does (it has no name
// scope and no generated method), so `*App.axaml.cs` is the one exemption.

public class InitializeComponentShadowingTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    /// <summary>
    /// Source with comments removed. This codebase has been caught by exactly this before: a scan for
    /// an ABSENCE fails on the code's own note explaining that the thing is gone.
    /// </summary>
    private static string CodeOnly(string path)
    {
        string src = File.ReadAllText(path);
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", "");
        return src;
    }

    /// <summary>Every view code-behind in src/Ui, excluding the three Application subclasses.</summary>
    private static IReadOnlyList<string> ViewCodeBehindFiles()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ui");
        var files = Directory
            .EnumerateFiles(root, "*.axaml.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => !Path.GetFileName(p).EndsWith("App.axaml.cs", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // Non-vacuity: this scan is worthless if the enumeration silently found nothing.
        Assert.True(files.Count > 50, $"Expected the whole view tree; found only {files.Count} files.");
        return files;
    }

    [Fact]
    public void NoViewDeclaresItsOwnInitializeComponent()
    {
        var offenders = ViewCodeBehindFiles()
            .Where(p => Regex.IsMatch(CodeOnly(p),
                        @"\bvoid\s+InitializeComponent\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These views shadow the generated InitializeComponent, so their x:Name fields are never " +
            "assigned and any use of one throws NullReferenceException: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoViewCallsTheXamlLoaderDirectly()
    {
        var offenders = ViewCodeBehindFiles()
            .Where(p => CodeOnly(p).Contains("AvaloniaXamlLoader.Load(this)", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These views load the XAML without going through InitializeComponent(), so their x:Name " +
            "fields are never assigned: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The control the crash actually landed on. Pinned by name as well as by the sweep above, so a
    /// future reader can trace the report to a test rather than to a general rule.
    /// </summary>
    [Fact]
    public void WBondEditorView_UsesTheGeneratedInitializeComponent()
    {
        string src = CodeOnly(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs"));

        Assert.Contains("InitializeComponent();", src, StringComparison.Ordinal);
        Assert.DoesNotContain("AvaloniaXamlLoader", src, StringComparison.Ordinal);
    }
}
