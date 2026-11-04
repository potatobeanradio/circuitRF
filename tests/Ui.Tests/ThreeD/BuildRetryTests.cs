using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>
/// brief-dfr34-5 R-dfr34-5-1 — the stalled bar's Retry must not queue behind the build it cancelled. A fake elaboration
/// that blocks stands in for the one seen in the field; a second build has to produce the scene within 2 s, and the
/// blocked build's late result is never adopted.
/// </summary>
[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class BuildRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr34-5-retry-" + Guid.NewGuid().ToString("N")[..8]);
    private C3dEditorViewModel? _vm;

    public void Dispose()
    {
        _vm?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private C3dEditorViewModel Open()
    {
        Directory.CreateDirectory(_root);
        TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"),
            new Technology { Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }] });
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(_root, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(100_000, 100_000, 10_000) }],
        });
        _vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new PatchRecordingBackend(),
                                     () => Path.Combine(_root, ".cws"), a => a());
        _vm.Viewer.Resized(400, 300);
        _vm.Start();
        Assert.True(SpinWait.SpinUntil(() => _vm.AdoptedGeneration == _vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)));
        return _vm;
    }

    /// <summary>Starts a build that runs <paramref name="block"/> inside the elaboration lock, then presses Retry, and
    /// returns the two generations and every generation adopted after the press.</summary>
    private static (long Blocked, long Retried, List<long> Adopted) BlockThenRetry(C3dEditorViewModel vm, Action<CancellationToken> block)
    {
        using var entered = new ManualResetEventSlim();
        bool first = true;
        vm.ElaborationStarting = ct =>
        {
            if (!first) return;
            first = false;
            entered.Set();
            block(ct);
        };
        vm.Viewer.Regenerate();
        long blocked = vm.Viewer.Source.Requested;
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the blocked build never started");

        var adopted = new List<long>();
        vm.Viewer.SceneAdopted += () => { lock (adopted) adopted.Add(vm.Viewer.Scene.Generation); };
        vm.Viewer.RetryFirstSceneCommand.Execute(null);
        long retried = vm.Viewer.Source.Requested;
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == retried, TimeSpan.FromSeconds(2)),
                    "Retry's build produced no scene within 2 s");
        return (blocked, retried, adopted);
    }

    [Fact]
    public void Retry_IsNotHeldBehindAnElaborationItCancelled()
    {
        var vm = Open();
        var (_, retried, _) = BlockThenRetry(vm, ct => { ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)); ct.ThrowIfCancellationRequested(); });
        Assert.True(vm.Viewer.Scene.Objects.Length > 0);
        Assert.Equal(retried, vm.Viewer.Scene.Generation);
    }

    [Fact]
    public void ABlockedBuildThatFinishesLate_IsNeverShown()
    {
        var vm = Open();
        var (blocked, retried, adopted) = BlockThenRetry(vm, _ => Thread.Sleep(500));   // ignores the token
        Thread.Sleep(200);
        lock (adopted)
        {
            Assert.DoesNotContain(blocked, adopted);
            Assert.Contains(retried, adopted);
        }
        Assert.Equal(retried, vm.Viewer.Scene.Generation);
    }
}
