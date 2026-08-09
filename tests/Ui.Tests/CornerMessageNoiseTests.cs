using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// What applying a corner is allowed to SAY.
///
/// <para><b>The failure this pins.</b> Applying a corner reads the kit's shared model library through
/// the corner file, so every honest observation the SPICE reader makes about that library arrives at
/// the caller — a model the library itself defines twice, an <c>.ends</c> whose trailing name does not
/// match. Those were reported as problems with the corner. None of them is about the design, the axis
/// or the section; none is actionable by the person who pressed Run; and they arrive once per axis per
/// run because every axis includes the same library. Measured: <b>28 messages on every
/// simulation of one transistor</b>, ahead of the two lines that meant anything.</para>
///
/// <para>The rule these gate is <b>not</b> "say less" — it is that a message must be about the design
/// being run. So each pair below asserts silence on agreement AND the message on the real thing,
/// because a quieter build that has stopped reporting a genuine contradiction is the worse outcome.</para>
/// </summary>
public sealed class CornerMessageNoiseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cmn-" + Guid.NewGuid().ToString("N")[..8]);

    public CornerMessageNoiseTests() => Directory.CreateDirectory(Path.Combine(_root, "models"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Models => Path.Combine(_root, "models");

    /// <summary>
    /// A shared model library that gives the reader something honest to say — the same model card
    /// twice, which is exactly what a kit's library does when a corner file includes it.
    /// </summary>
    private void WriteSharedLibrary() => File.WriteAllText(Path.Combine(Models, "shared.lib"), """
        .model dmod d is=1e-14
        .model dmod d is=1e-14
        .subckt plate a b
        C1 a b c={carea}
        .ends plate
        """);

    private string WriteAxis(string name, params (string Section, string Body)[] sections)
    {
        string path = Path.Combine(Models, name);
        File.WriteAllText(path, string.Join("\n", sections.Select(s => $"""
            .LIB {s.Section}
            {s.Body}
            .include shared.lib
            .ENDL {s.Section}
            """)));
        return path;
    }

    private static WorkspaceCornerAxis Axis(string display, string file, params string[] options)
        => new("SampleKit", $"models/{Path.GetFileName(file)}", display, options, file, display);

    private static (IReadOnlyList<Variable> Bound, List<string> Problems) Apply(
        IReadOnlyList<WorkspaceCornerAxis> axes, Dictionary<string, string>? selections = null)
    {
        var problems = new List<string>();
        var bound = WorkspaceCorners.BindingsFor(axes, selections ?? [], problems);
        return (bound, problems);
    }

    // ── the reader's notes ────────────────────────────────────────────────────

    [Fact]
    public void ACornerThatBindsSomething_SaysNothingAboutTheKitsOwnFiles()
    {
        WriteSharedLibrary();
        string file = WriteAxis("capCorners.lib", ("cap_typ", ".param carea = 1.5E-15"));

        var (bound, problems) = Apply([Axis("capCorners", file, "cap_typ")]);

        Assert.Contains(bound, v => v.Name == "carea");

        // The library really does give the reader something to report — a fixture that produced no
        // notes would pass this whether or not the rule were implemented.
        Assert.NotEmpty(CircuitRF.Core.Netlist.Spice.SpiceNetlistReader
                            .ReadFile(Path.Combine(Models, "shared.lib")).Notes);
        Assert.Empty(problems);
    }

    /// <summary>
    /// …and when NOTHING was bound, the reader's account is the only explanation available, so it is
    /// exactly then that it surfaces. Quietening the ordinary case must not quieten this one.
    /// </summary>
    [Fact]
    public void ACornerThatBindsNothing_SaysSoAndCarriesTheReadersReasons()
    {
        WriteSharedLibrary();
        string file = WriteAxis("capCorners.lib", ("cap_typ", "* this section declares no parameter"));

        var (bound, problems) = Apply([Axis("capCorners", file, "cap_typ")]);

        Assert.Empty(bound);
        Assert.Contains(problems, p => p.Contains("bound nothing", StringComparison.Ordinal));
        Assert.True(problems.Count > 1, "the reader's own reasons must come with it");
    }

    // ── two axes, one name ────────────────────────────────────────────────────

    [Fact]
    public void TwoAxesBindingOneNameToTheSameValue_IsAgreementAndIsSilent()
    {
        WriteSharedLibrary();
        string a = WriteAxis("cornerA.lib", ("a_typ", ".param SWSOA = 0\n.param acon = 1"));
        string b = WriteAxis("cornerB.lib", ("b_typ", ".param SWSOA = 0\n.param bcon = 2"));

        var (bound, problems) = Apply([Axis("cornerA", a, "a_typ"), Axis("cornerB", b, "b_typ")]);

        Assert.Empty(problems);
        Assert.Single(bound, v => v.Name == "SWSOA");
    }

    [Fact]
    public void TwoAxesBindingOneNameToDIFFERENTValues_IsStillReported()
    {
        WriteSharedLibrary();
        string a = WriteAxis("cornerA.lib", ("a_typ", ".param SWSOA = 0"));
        string b = WriteAxis("cornerB.lib", ("b_typ", ".param SWSOA = 1"));

        var (_, problems) = Apply([Axis("cornerA", a, "a_typ"), Axis("cornerB", b, "b_typ")]);

        Assert.Contains(problems, p => p.Contains("SWSOA", StringComparison.Ordinal)
                                    && p.Contains("more than one corner axis", StringComparison.Ordinal));
    }

    // ── the design and the corner ─────────────────────────────────────────────

    private static IReadOnlyList<string> ExtractWith(params Variable[] cornerVars)
    {
        var model = new SchematicEditModel { SchematicDirectory = Path.GetTempPath() };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "V1", Symbol = SymbolKind.Var, X = 0, Y = 0,
            Parameters   = { new EditableParameter { Name = "SWSOA", Expression = "0" } },
        });

        return NetExtractor.Extract(model, "tb", cornerVariables: cornerVars).Conflicts;
    }

    [Fact]
    public void ADesignConstantEqualToTheCornersIsSilent_AndADifferentOneIsReported()
    {
        Assert.DoesNotContain(ExtractWith(new Variable("SWSOA", "0")),
                              c => c.Contains("SWSOA", StringComparison.Ordinal));

        Assert.Contains(ExtractWith(new Variable("SWSOA", "1")),
                        c => c.Contains("SWSOA", StringComparison.Ordinal)
                          && c.Contains("design's own definition", StringComparison.Ordinal));
    }
}
