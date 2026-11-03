using System;
using System.Collections.Generic;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Brief vendorlib-15 R-vl15-2: a compiled model that refuses to run ends as ONE refusal carrying the model's own
/// sentence — through circuitRF's real provider, over a real worker process. The device is the fake library's
/// <c>crf_refuse</c>, whose <c>mode</c> model parameter picks how it refuses; the sentences are fixed strings in
/// <c>tools/fake-osdi-model/fake_osdi.c</c>.
/// </summary>
[Collection(DeviceWorkerToolsDirectoryCollection.Name)]
public sealed class OsdiRefusingModelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    public const string SetupSentence = "TSTFET100 licence check failed: this build expired on 2020-01-01.";
    public const string EvalSentence  = "TSTFET100 stopped: this build is locked to another machine.";
    public const string ExitSentence  = "TSTFET100 cannot start: no licence file was found beside the model.";

    [FixtureTheory(WorkerRel, HowTo)]
    [InlineData(1, SetupSentence)]   // setup reports errors
    [InlineData(2, EvalSentence)]    // $fatal on the first evaluation
    [InlineData(3, ExitSentence)]    // the process exits during setup
    public void ARefusingModelIsOneRefusalCarryingItsOwnSentence(int mode, string sentence)
    {
        using var p = DeviceWorkerProvider.Launch("osdi",
            FixturePaths.Require(WorkerRel), [FixturePaths.Require(ModelRel)]);

        var ex = Assert.ThrowsAny<ExternalDeviceException>(() =>
        {
            using var inst = p.Create("crf_refuse", new Dictionary<string, string> { ["mode"] = mode.ToString() });
            inst.Evaluate([1.0, 0.0]);
        });

        output.WriteLine(ex.Message);
        // The model's own sentence LEADS — before the worker's "model setup reported errors" and before a failed
        // point's list of possible causes, which say nothing the sentence does not.
        Assert.StartsWith("The model refused to run: " + sentence, ex.Message);
        if (mode == 1)
            Assert.True(ex.Message.IndexOf(sentence, StringComparison.Ordinal)
                        < ex.Message.IndexOf("model setup reported errors", StringComparison.Ordinal), ex.Message);
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void ModeZeroIsAPlainConductance()
    {
        using var p = DeviceWorkerProvider.Launch("osdi",
            FixturePaths.Require(WorkerRel), [FixturePaths.Require(ModelRel)]);
        using var inst = p.Create("crf_refuse", new Dictionary<string, string> { ["g0"] = "0.002", ["mode"] = "0" });
        var r = inst.Evaluate([1.5, 0.5]);
        Assert.Equal(0.002, r.Current[0], 12);
        Assert.Equal(-0.002, r.Current[1], 12);
    }
}
