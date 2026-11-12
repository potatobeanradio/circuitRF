using System.Diagnostics;
using CircuitRF.Cli;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-2 R-hzn2-8 — <c>check</c>, <c>explain</c> and <c>find</c> read a <c>.hzn</c> through the seams they already have. The
/// verb is run as a PROCESS and compared with the in-process call it forwards to, as every verb gate is: a translation
/// that lived in the verb would agree today and drift tomorrow.
/// </summary>
public sealed class HznCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn2-" + Guid.NewGuid().ToString("N")[..10]);

    public HznCliTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    private static string Line(Diagnostic d) => d.Severity switch
    {
        DiagnosticSeverity.Error => "error: " + d.Render(),
        DiagnosticSeverity.Warning => "warning: " + d.Render(),
        _ => "note: " + d.Render(),
    };

    [Fact]
    public void Check_ExitsZeroOnADesignThatTranslates_AndSaysWhatTheInProcessCallSays()
    {
        var run = RunCli("check", Fixture("mapping.hzn"), "--design", "Mapping");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        var expected = HznCli.Check(Fixture("mapping.hzn"), "Mapping");
        Assert.DoesNotContain(expected, d => d.Severity == DiagnosticSeverity.Error);
        foreach (var d in expected) Assert.Contains(Line(d), run.StdErr);
        Assert.Contains("1 document(s) checked: 0 error(s)", run.StdOut);
    }

    [Fact]
    public void Check_ExitsOneOnARefusal_WithTheSameItemsTheTranslationReturns()
    {
        var run = RunCli("check", Fixture("unsupported.hzn"));
        Assert.Equal(1, run.ExitCode);
        var expected = Assert.Single(HznCli.Check(Fixture("unsupported.hzn"), null));
        Assert.Equal("hzn.translate.refused", expected.Id);
        Assert.Contains(Line(expected).Split('\n')[0], run.StdErr);
        Assert.Contains("CreateTorus", run.StdErr);
        Assert.Contains("Incident Plane Wave", run.StdErr);
        Assert.Contains("mystery_ceramic", run.StdErr);
    }

    [Fact]
    public void ADesignNameThatIsNotInTheProject_IsRefusedNamingTheDesigns()
    {
        var run = RunCli("check", Fixture("mapping.hzn"), "--design", "Elsewhere");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Mapping", run.StdErr);
    }

    [Fact]
    public void AHzn_IsRecognisedByItsContent_NotItsExtension()
    {
        Assert.Equal(DocumentKind.Hertzon, DocumentKinds.Classify(Fixture("mapping.hzn")));
        string other = Path.Combine(_root, "not-hertzon.hzn");
        File.WriteAllText(other, "{\"format\": \"something-else\", \"version\": 2}");
        Assert.Equal(DocumentKind.Foreign, DocumentKinds.Classify(other));
    }

    [Fact]
    public void Explain_ReportsEachDesignAndWhatItBecame()
    {
        var run = RunCli("explain", Fixture("mapping.hzn"));
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        string all = run.StdOut + run.StdErr;
        Assert.Contains("Mapping", all);
        Assert.Contains("translated to a 3D view held in memory", all);

        var refused = RunCli("explain", Fixture("unsupported.hzn"));
        Assert.Equal(1, refused.ExitCode);
    }

    [Fact]
    public void Find_ListsAProjectOutsideAnyWorkspace_WithItsDesigns()
    {
        File.Copy(Fixture("two-designs.hzn"), Path.Combine(_root, "two-designs.hzn"));
        var run = RunCli("find", _root);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.Contains("Hertzon project", run.StdOut);
        var expected = HznCli.Describe(Path.Combine(_root, "two-designs.hzn"));
        Assert.NotEmpty(expected.Designs);
        foreach (var d in expected.Designs) Assert.Contains($"{d.Name} [{d.Kind}]", run.StdOut);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HznCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}
