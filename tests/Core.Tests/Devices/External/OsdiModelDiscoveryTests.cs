using System.Collections.Generic;
using System.IO;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Finding a user's COMPILED Verilog-A models and working out what each implements.
///
/// <para>Skips rather than fails when the worker or the sample model is not built — both are native,
/// and this repository must stay green on a machine with no C compiler.</para>
/// </summary>
public sealed class OsdiModelDiscoveryTests
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    [FixtureFact(WorkerRel, HowTo)]
    public void FindsAModelUnderARoot_AndReadsTheModuleFromTheArtefact()
    {
        string worker = FixturePaths.Require(WorkerRel);
        string model  = FixturePaths.Require(ModelRel);

        var problems = new List<string>();
        var found    = OsdiModelDiscovery.Find([Path.GetDirectoryName(model)!], worker, problems);

        var hit = Assert.Single(found);
        Assert.Equal(Path.GetFullPath(model), hit.FilePath);
        Assert.NotEmpty(hit.TypeIds);
        Assert.Empty(problems);

        // THE RULE THIS EXISTS FOR — asserted where it can actually be asserted, which is not here.
        // The module is read from inside the artefact and need not match the file's own name; on a
        // real build mdla_nqs.osdi declares MDLANQS_VA, so a name-derived mapping fails to resolve
        // a model sitting right there, and fails silently. This repository's own sample happens to
        // name its module after its file, so a check here would pass either way — and a test that
        // cannot fail is worse than no test. What IS pinned above is that the module came from the
        // describe call at all; the divergent case is covered by the real-kit measurement recorded
        // in src/Ui/CLAUDE.md.
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void MatchesAModelCardsTypeCaseInsensitively()
    {
        string worker = FixturePaths.Require(WorkerRel);
        string model  = FixturePaths.Require(ModelRel);

        var found = OsdiModelDiscovery.Find([Path.GetDirectoryName(model)!], worker);
        string declared = found[0].TypeIds[0];

        // A `.model` card writes the module in whatever case the kit's author used; the artefact
        // declares its own. Measured: the card says `mdla_va`, the artefact says
        // `MDLA_VA`. The dialect is case-insensitive, so this is correctness, not tolerance.
        Assert.NotNull(OsdiModelDiscovery.ImplementorOf(found, declared.ToLowerInvariant()));
        Assert.NotNull(OsdiModelDiscovery.ImplementorOf(found, declared.ToUpperInvariant()));
        Assert.Null(OsdiModelDiscovery.ImplementorOf(found, "a_module_nobody_compiled"));
    }

    /// <summary>
    /// A card's parameter names are respelled the way the ARTEFACT declares them, and a name it
    /// declares nothing like is left exactly as written.
    ///
    /// <para>Both halves matter. Measured against a real compiled model: <c>level</c> is refused —
    /// <i>'level' is not a parameter of this device type</i> — where <c>LEVEL</c> is accepted, because
    /// the dialect writing the card is case-insensitive and the worker matches with <c>strcmp</c>. But
    /// respelling only what the module declares a match for is what keeps this a translation rather
    /// than a spell-checker: a genuine typo still goes through untouched and is refused by name.</para>
    /// </summary>
    [Fact]
    public void AlignParameterCase_RespellsWhatTheModuleDeclares_AndNothingElse()
    {
        string[] declared = ["LEVEL", "TOX", "g0"];

        Assert.Equal("LEVEL", OsdiModelDiscovery.AlignParameterCase(declared, "level"));
        Assert.Equal("LEVEL", OsdiModelDiscovery.AlignParameterCase(declared, "LEVEL"));
        Assert.Equal("g0",    OsdiModelDiscovery.AlignParameterCase(declared, "G0"));

        Assert.Equal("vth0",  OsdiModelDiscovery.AlignParameterCase(declared, "vth0"));
        Assert.Equal("vth0",  OsdiModelDiscovery.AlignParameterCase([], "vth0"));
    }

    /// <summary>
    /// The implementor carries the artefact's own spelling of the module AND its parameter names —
    /// which is the whole reason it is a record rather than a path: the routing needs all three, and
    /// two of them exist nowhere but inside the file.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void TheImplementorCarriesTheFile_TheModule_AndItsParameterSpellings()
    {
        string model = FixturePaths.Require(ModelRel);
        var found = OsdiModelDiscovery.Find([Path.GetDirectoryName(model)!], FixturePaths.Require(WorkerRel));

        string declared = found[0].TypeIds[0];
        var hit = OsdiModelDiscovery.ImplementorOf(found, declared.ToLowerInvariant());

        Assert.NotNull(hit);
        Assert.Equal(Path.GetFullPath(model), hit!.FilePath);
        Assert.Equal(declared, hit.TypeId);         // the artefact's spelling, not the caller's
        Assert.NotEmpty(hit.Parameters);
    }

    [Fact]
    public void NoWorker_ReportsWhyAndFindsNothing_RatherThanThrowing()
    {
        var problems = new List<string>();

        var found = OsdiModelDiscovery.Find(
            [Path.GetTempPath()], Path.Combine(Path.GetTempPath(), "no-such-osdi-worker"), problems);

        Assert.Empty(found);
        Assert.NotEmpty(problems);
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void AnUnreadableArtefact_CostsItselfAndIsReported_NeverTheOthers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-osdi-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "broken.osdi"), "not a compiled model");

            var problems = new List<string>();
            var found    = OsdiModelDiscovery.Find([dir], FixturePaths.Require(WorkerRel), problems);

            Assert.Empty(found);
            Assert.NotEmpty(problems);      // named, never silently absent
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void AMissingRoot_IsNotAnError()
        => Assert.Empty(OsdiModelDiscovery.Find(
               [Path.Combine(Path.GetTempPath(), "definitely-not-here")],
               FixturePaths.Require(WorkerRel)));
}
