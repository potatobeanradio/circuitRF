using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-3 §6 tests 4-5 — EMerge's speed sentence and its thread environment. Nothing here needs EMerge: the
//  probe is canned and the record is written by hand. Tests 1-3 (the installer's optional steps) are
//  SolverInstallTests' gate 13, beside the fake recipes they share.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class EmergeAcceleratorTests
{
    private static readonly EmergeProbe Baseline = new("3.0.0b2", "3.13.16", "darwin", "arm64", 10, "4.15.2", [], "SolverSuperLU");

    /// <summary>Test 4 — the Settings row, the install summary and a run's Note are one function's words: each caller
    /// goes through <see cref="EmergeAccelerator.Sentence(SolverInstallation?)"/>, nothing else words the advice, and
    /// a run's Note on the baseline IS that sentence, once per session.</summary>
    [Fact]
    public void TheSettingsSentenceAndTheRunNoteAreOneFunctionsWords()
    {
        string root = RepoRoot();
        string Code(string rel) => Regex.Replace(Regex.Replace(File.ReadAllText(Path.Combine(root, rel)), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Assert.Contains("EmergeAccelerator.Sentence(found)", Code("src/Design/Em3d/SolverDiscovery.cs"));
        Assert.Contains("EmergeAccelerator.Sentence(found)", Code("src/Design/Em3d/Install/SolverInstaller.cs"));
        Assert.Contains("Sentence(found) is { } sentence", Code("src/Design/Em3d/Solvers/EMerge/EmergeAccelerator.cs"));
        var wording = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                               .Where(f => Code(Path.GetRelativePath(root, f)).Contains("will add {faster}", StringComparison.Ordinal))
                               .Select(f => Path.GetFileName(f)).ToList();
        Assert.Equal(["EmergeAccelerator.cs"], wording);

        // What it says for circuitRF's own install with the GPU solver still to build: the tier, then the recipe's
        // prerequisite and its command.
        var recipe = SolverRecipes.All.Single(r => r.Id == "emerge-3.0.0-macos-arm64");
        var record = new InstallRecord
        {
            Recipe = recipe.Id, Optional = [new(recipe.OptionalGroups[0].When.Name, OptionalStepsStatus.Missing, "unmet")],
        };
        Assert.StartsWith("Baseline sparse solver, one core. GPU solving is available after installing a Rust toolchain (",
                          EmergeAccelerator.Sentence(Baseline, record, recipe, new SolverInstaller { Distribution = [] }));

        // A run on the baseline adds the sentence as a Note, once per session.
        var found = new SolverInstallation(SolverTool.Emerge, "/x/bin/python", "3.0.0b2", null, Baseline.Banner,
                                           SolverHowFound.Settings, "set here", "3.0.0b2", Emerge: Baseline);
        EmergeAccelerator.ResetSessionForTests();
        List<string> notes = [], warnings = [];
        EmergeAccelerator.BeforeRun(found, notes, warnings);
        EmergeAccelerator.BeforeRun(found, notes, warnings);
        Assert.Equal([EmergeAccelerator.Sentence(found)!], notes);
        Assert.Empty(warnings);
    }

    /// <summary>Test 5 — R-emi3-5: the Cores preference, clamped to the logical count, or every physical core when unset
    /// or unusable; one count in every variable a solver reads.</summary>
    [Fact]
    public void TheThreadEnvironmentIsDerivedFromTheCoresPreference()
    {
        Assert.Equal(10, EmergeThreads.Count(null, 10, 10));
        Assert.Equal(4, EmergeThreads.Count(4, 10, 10));
        Assert.Equal(12, EmergeThreads.Count(64, 8, 12));
        Assert.Equal(8, EmergeThreads.Count(0, 8, 16));
        var env = EmergeThreads.Environment(4, 10, 10);
        Assert.Equal(EmergeThreads.Variables, env.Keys);
        Assert.All(env.Values, v => Assert.Equal("4", v));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
