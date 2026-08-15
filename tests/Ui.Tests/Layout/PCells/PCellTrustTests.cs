using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// B6: a kit's generator scripts are third-party code, and third-party code runs only with explicit
/// consent.
///
/// <para><b>The consent is enforced at the point that would launch the interpreter, not at the
/// prompt.</b> A prompt that never appeared — headless, a dialog that failed, a workspace switched
/// away mid-question — leaves the decision Unknown, and Unknown does not run. These tests drive the
/// enforcement point directly for exactly that reason.</para>
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class PCellTrustTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _reports = [];
    private readonly List<PCellWorkerResolver> _resolvers = [];

    public PCellTrustTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcelltrust-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        PCellRegistry.ClearResolvers();
    }

    public void Dispose()
    {
        PCellRegistry.ClearResolvers();
        foreach (var r in _resolvers) r.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── The store ─────────────────────────────────────────────────────────────

    [Fact]
    public void AKitNobodyHasBeenAskedAbout_IsUnknown_NotAllowed()
        => Assert.Equal(PCellTrustDecision.Unknown, new PCellTrustStore().Decide(_root));

    [Theory]
    [InlineData(true,  PCellTrustDecision.Allowed)]
    [InlineData(false, PCellTrustDecision.Denied)]
    public void ARecordedDecision_IsRemembered(bool allowed, PCellTrustDecision expected)
    {
        var store = new PCellTrustStore();
        store.Record(_root, allowed);
        Assert.Equal(expected, store.Decide(_root));
    }

    /// <summary>The same directory named two ways must be one entry, or "allow" would not cover the
    /// spelling the next lookup happens to use.</summary>
    [Fact]
    public void TheSameDirectoryNamedTwoWays_IsOneEntry()
    {
        var store = new PCellTrustStore();
        store.Record(_root, allowed: true);

        Assert.Equal(PCellTrustDecision.Allowed, store.Decide(_root + Path.DirectorySeparatorChar));
        Assert.Equal(PCellTrustDecision.Allowed, store.Decide(Path.Combine(_root, "kit", "..")));
        Assert.Equal(1, store.Count);
    }

    /// <summary><b>A refusal is recorded too.</b> Without that, every open re-asks about the same kit,
    /// and a prompt that nags is one people learn to dismiss unread.</summary>
    [Fact]
    public void ARefusal_IsWritten_JustLikeAPermission()
    {
        IReadOnlyDictionary<string, bool>? written = null;
        var store = new PCellTrustStore(persist: d => written = new Dictionary<string, bool>(d));

        store.Record(_root, allowed: false);

        Assert.NotNull(written);
        Assert.False(Assert.Single(written!).Value);
    }

    [Fact]
    public void ASeededStore_AnswersTheSameAsOneJustWritten()
    {
        var seed = new Dictionary<string, bool> { [_root + Path.DirectorySeparatorChar] = true };
        Assert.Equal(PCellTrustDecision.Allowed, new PCellTrustStore(seed).Decide(_root));
    }

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary><b>The headline.</b> A kit nobody has agreed to does not run — no process is started,
    /// and its generators are not offered. Its cells then draw as the existing Not Found placeholder,
    /// which is the "degrade, never deny" rule every other missing-thing already follows.</summary>
    [PythonFact]
    public void AnUnknownKit_IsNeverStarted_AndOffersNothing()
    {
        WriteKit("kit");
        var store = new PCellTrustStore();

        int before = ProcessPCellWorkerTransport.StartCount;
        var resolver = NewResolver(store);

        Assert.Null(resolver.Resolve("TESTCELL"));
        Assert.Empty(resolver.KnownGeneratorIds);
        Assert.Equal(0, ProcessPCellWorkerTransport.StartCount - before);
        Assert.Contains(_reports, r => r.Contains("has not been allowed to run yet"));
    }

    /// <summary>A refusal must say how to reverse itself, or a mis-click permanently breaks a kit's
    /// artwork with nothing visible to undo it.</summary>
    [PythonFact]
    public void ARefusedKit_IsNeverStarted_AndTheReasonNamesTheWayBack()
    {
        string kit = WriteKit("kit");
        var store = new PCellTrustStore();
        store.Record(kit, allowed: false);

        int before = ProcessPCellWorkerTransport.StartCount;
        var resolver = NewResolver(store);

        Assert.Null(resolver.Resolve("TESTCELL"));
        Assert.Equal(0, ProcessPCellWorkerTransport.StartCount - before);
        Assert.Contains(_reports, r => r.Contains("not allowed to run") && r.Contains("Settings"));
    }

    [PythonFact]
    public void AnAllowedKit_Runs()
    {
        string kit = WriteKit("kit");
        var store = new PCellTrustStore();
        store.Record(kit, allowed: true);

        Assert.NotNull(NewResolver(store).Resolve("TESTCELL"));
    }

    /// <summary>One refused kit must not take the others down with it — the same
    /// one-broken-kit-is-not-every-kit rule the start path already follows.</summary>
    [PythonFact]
    public void RefusingOneKit_LeavesAnotherKitsCellsWorking()
    {
        string refused = WriteKit("refused", generatorId: "REFUSEDCELL");
        WriteKit("allowed", generatorId: "ALLOWEDCELL");

        var store = new PCellTrustStore();
        store.Record(refused, allowed: false);
        store.Record(Path.Combine(_root, "allowed"), allowed: true);

        var resolver = NewResolver(store);
        Assert.Null(resolver.Resolve("REFUSEDCELL"));
        Assert.NotNull(resolver.Resolve("ALLOWEDCELL"));
    }

    /// <summary>
    /// The real flow after the user answers Allow: the resolver has already concluded that the kit
    /// could not run, and that conclusion is cached. <see cref="PCellWorkerResolver.StopProviders"/>
    /// is what makes the answer take effect without reopening the workspace.
    /// </summary>
    [PythonFact]
    public void GrantingPermission_MakesTheCellsAvailable_WithoutReopeningTheWorkspace()
    {
        string kit = WriteKit("kit");
        var store = new PCellTrustStore();
        var resolver = NewResolver(store);

        Assert.Null(resolver.Resolve("TESTCELL"));   // asked, refused-by-default, and cached as such

        store.Record(kit, allowed: true);
        resolver.StopProviders();
        PCellRegistry.InvalidateResolved();

        Assert.NotNull(resolver.Resolve("TESTCELL"));
    }

    /// <summary>
    /// <b>Stopping must actually END the interpreters.</b> Clearing the lookup alone would start a
    /// second process per kit on the next lookup and leave the first running with nothing to talk to
    /// — a leak the user can neither see nor clean up.
    /// </summary>
    [PythonFact]
    public void StopProviders_EndsTheProcess_RatherThanLeavingASecondOneRunning()
    {
        string kit = WriteKit("kit");
        var store = new PCellTrustStore();
        store.Record(kit, allowed: true);

        var resolver = NewResolver(store);
        int before = ProcessPCellWorkerTransport.StartCount;

        var first = resolver.Resolve("TESTCELL");
        Assert.NotNull(first);
        Assert.Equal(1, ProcessPCellWorkerTransport.StartCount - before);

        resolver.StopProviders();
        Assert.NotNull(resolver.Resolve("TESTCELL"));
        Assert.Equal(2, ProcessPCellWorkerTransport.StartCount - before);

        // The delegate held from before the stop now talks to a process that is gone; it fails
        // loudly rather than silently answering from a dead pipe. Every caller of a generator already
        // guards this (render and snap both degrade rather than taking the frame down).
        Assert.ThrowsAny<Exception>(() =>
            first!(new Dictionary<string, PCellValue>(), null, PCellLayerSelection.Default));
    }

    /// <summary>The documented default: a caller with no policy — a headless one, with nobody to ask
    /// and nowhere to record an answer — gets the pre-B6 behaviour.</summary>
    [PythonFact]
    public void NoPolicyAtAll_RunsEverything()
    {
        WriteKit("kit");
        Assert.NotNull(NewResolver(store: null).Resolve("TESTCELL"));
    }

    /// <summary>The kit list the prompt names comes from the manifest scan alone: reading JSON, with
    /// nothing started. Asking is not running.</summary>
    [Fact]
    public void ListingTheKitsToAskAbout_StartsNothing()
    {
        WriteKit("kit");
        int before = ProcessPCellWorkerTransport.StartCount;

        var kits = NewResolver(new PCellTrustStore()).Kits;

        Assert.Equal("kit", Assert.Single(kits).Name);
        Assert.Equal(Path.Combine(_root, "kit", "main.py"), kits[0].EntryScript);
        Assert.Equal(0, ProcessPCellWorkerTransport.StartCount - before);
    }

    // ── A kit that travelled inside an archived workspace ────────────────────

    /// <summary>
    /// Owner question (2026-08-15): "How are Kit artwork permissions handled when a user unarchives a
    /// workspace that includes a kit that uses python to generate artwork?"
    ///
    /// <para>The answer only works if the kit is FOUND. The manifest scan is deliberately shallow —
    /// the root and one level down — and an archive puts an included kit at <c>kits/&lt;kit&gt;/</c>,
    /// which is two. Unscanned means never asked about, and never asked about means every cell it
    /// draws stays a placeholder with nothing said anywhere. That is the failure this pins.</para>
    /// </summary>
    [Fact]
    public void AKitBundledInsideAnUnarchivedWorkspace_IsStillFound_SoConsentIsStillAsked()
    {
        string bundled = WriteKit("kits/vendorkit");

        var kits = NewResolver(new PCellTrustStore()).Kits;

        Assert.Equal(bundled, Assert.Single(kits).Directory);
    }

    [Fact]
    public void ABundledKitNobodyHasAnsweredFor_IsUnknown_AndRunsNothing()
    {
        string bundled = WriteKit("kits/vendorkit");
        var trust = new PCellTrustStore();

        int before = ProcessPCellWorkerTransport.StartCount;
        var resolver = NewResolver(trust);

        // Unknown, so the recipient IS asked — and until they answer, nothing starts.
        Assert.Equal(PCellTrustDecision.Unknown, trust.Decide(bundled));
        Assert.Null(resolver.Resolve("TESTCELL"));
        Assert.Equal(0, ProcessPCellWorkerTransport.StartCount - before);
    }

    [Fact]
    public void TheSendersPermission_DoesNotTravel_BecauseTheKitLandsAtADifferentPath()
    {
        // The decision is keyed by the kit's absolute directory. The sender allowed the kit where it
        // sat on THEIR machine; on the recipient's it is a different thing on disk, so the question
        // is put again — which is the honest answer, and the whole reason the key is a path.
        string bundled = WriteKit("kits/vendorkit");

        var trust = new PCellTrustStore(
            [new KeyValuePair<string, bool>("/some/other/machine/vendorkit", true)]);

        Assert.Equal(PCellTrustDecision.Unknown, trust.Decide(bundled));
    }

    [PythonFact]
    public void UnarchivingOntoTheSamePathTheUserAlreadyAllowed_DoesNotAskAgain()
    {
        // "…unless they have already given permission for that kit."
        string bundled = WriteKit("kits/vendorkit");

        var trust = new PCellTrustStore([new KeyValuePair<string, bool>(bundled, true)]);

        Assert.Equal(PCellTrustDecision.Allowed, trust.Decide(bundled));
        Assert.NotNull(NewResolver(trust).Resolve("TESTCELL"));
    }

    [Fact]
    public void TheArchivesKitsFolderIsAContainerOfKits_NotAKit()
    {
        // A manifest sitting in `kits/` itself would be a kit called "kits"; the folder is scanned
        // THROUGH, and its own children are what get asked about.
        WriteKit("kits/one", generatorId: "ONECELL");
        WriteKit("kits/two", generatorId: "TWOCELL");

        var kits = NewResolver(new PCellTrustStore()).Kits;

        Assert.Equal(2, kits.Count);
        Assert.DoesNotContain(kits, k => Path.GetFileName(k.Directory) == "kits");
    }

    // ── Where the decision is kept ────────────────────────────────────────────

    /// <summary>
    /// <b>The decision must never be serialized into the workspace.</b> A file that travels with the
    /// artifact can be written by whoever sends you the artifact — a workspace arriving with its own
    /// scripts pre-marked trusted would run them on open with no prompt, which defeats the whole
    /// mechanism. It lives in this installation's preferences instead.
    /// </summary>
    [Fact]
    public void TheWorkspaceFile_CarriesNoTrustDecision()
    {
        Assert.DoesNotContain(typeof(CwsFile).GetProperties(),
            p => p.Name.Contains("Trust", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(typeof(CircuitRF.Ui.Theming.AppPreferences).GetProperties(),
            p => p.Name == "PCellTrust");
    }

    // ── Source scans: the parts that cannot be constructed headlessly ─────────

    /// <summary>
    /// The gate defaults to permissive for headless callers, so a future construction site that forgets
    /// to pass a policy would silently disable consent. This pins that the application's own — and
    /// only — construction site passes one.
    /// </summary>
    [Fact]
    public void TheApplication_AlwaysPassesATrustPolicy()
    {
        string vm = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("trust: trust.Decide", vm);

        // And no other production code constructs a resolver at all (qualified or not).
        var construction = new System.Text.RegularExpressions.Regex(
            @"new\s+(?:[\w.]+\.)?PCellWorkerResolver\s*\(");
        var offenders = Directory
            .EnumerateFiles(RepoPath("src/Ui"), "*.cs", SearchOption.AllDirectories)
            .Where(f => construction.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["WorkspaceViewModel.cs"], offenders);
    }

    /// <summary>A dismissed prompt must record NOTHING — never a refusal, and certainly never
    /// permission. The question simply stands, and is put again next time.</summary>
    [Fact]
    public void ADismissedPrompt_RecordsNothing()
    {
        string vm = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        int dismissed = vm.IndexOf("if (allowed is null) return;", StringComparison.Ordinal);
        int records   = vm.IndexOf("trust.Record(kit.Directory", StringComparison.Ordinal);

        Assert.True(dismissed > 0, "The consent handler no longer has a dismissed-without-answering branch.");
        Assert.True(records > dismissed, "Consent is recorded before the dismissed case is filtered out.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PCellWorkerResolver NewResolver(PCellTrustStore? store)
    {
        var resolver = new PCellWorkerResolver(_root,
            (_, _) => new PythonInterpreter(PythonRunner.Interpreter ?? "python3", [], "test", "supplied by the test"),
            _reports.Add,
            store is null ? null : store.Decide);
        _resolvers.Add(resolver);
        return resolver;
    }

    /// <param name="name">Folder under the workspace root. May name a subfolder ("kits/vendor") —
    /// which is exactly where an unarchived workspace puts a kit that travelled inside it.</param>
    private string WriteKit(string name, string generatorId = "TESTCELL")
    {
        string dir = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);

        string package = PythonRunner.PackageRoot;
        File.WriteAllText(Path.Combine(dir, "main.py"), $"""
            import sys
            sys.path.insert(0, r'{package}')
            from circuitrf_pcell import Layer, Parameter, Rect, Result, generator, run

            @generator("{generatorId}", [Parameter.length("W")])
            def cell(params, tech):
                return Result(shapes=[Rect(tech.signal_layer or Layer(1, 0), 0, 0, 1000, 1000)], pins=[])

            run()
            """);

        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            $$"""{ "schemaVersion": 1, "entry": "main.py", "pythonPath": [{{System.Text.Json.JsonSerializer.Serialize(package)}}] }""");
        return dir;
    }

    private static string RepoPath(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root walking up from this test file.");
        return Path.Combine(dir!, relativePath);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
        => File.ReadAllText(RepoPath(relativePath, here));
}
