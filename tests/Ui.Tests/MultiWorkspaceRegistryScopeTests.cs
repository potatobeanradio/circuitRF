using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// MW1 §3 / R-mw1-4 — the four process-global registries are scoped to a workspace.
///
/// <para><b>This is the test that would have caught the whole problem.</b> Before MW1, opening a
/// workspace called <c>PdkKitRegistry.Clear()</c>, <c>KitLayoutGenerators.Clear()</c>,
/// <c>PCellRegistry.ClearResolvers()</c> and <c>ExternalDeviceRegistry.ResetResolved()</c> on state
/// shared by the whole process — so a second workspace window silently unmounted the first one's
/// kits, its layout generators and its device workers. Nothing reported it: the first symptom was
/// that window's kit parts drawing as pin-less placeholders, and its runs failing to find a provider.
/// Watched red against the pre-MW1 code before the scoping landed.</para>
///
/// <para>Fixtures name no vendor and no part — a kit name and a part id are strings that arrived at
/// run time (R-pdk-1).</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class MultiWorkspaceRegistryScopeTests : IDisposable
{
    // Two workspace roots, distinct and absolute. They need not exist: every registry keys by the
    // normalised path string, and nothing here touches the filesystem.
    private static readonly string RootA = Path.Combine(Path.GetTempPath(), "crf-mw1-a");
    private static readonly string RootB = Path.Combine(Path.GetTempPath(), "crf-mw1-b");

    public MultiWorkspaceRegistryScopeTests() => Reset();
    public void Dispose()                     => Reset();

    private static void Reset()
    {
        PdkKitRegistry.ResetAllForTests();
        KitLayoutGenerators.SetRefresher(null, null);
        KitLayoutGenerators.ResetAllForTests();
        PCellRegistry.ClearResolvers();
        ExternalDeviceRegistry.Clear();
    }

    private static PdkKitPart Part(string id, int pins = 2)
    {
        var sym = new Symbol(
            primitives: [],
            pins:       [.. Enumerable.Range(0, pins).Select(i => new SymbolPin(0, i * 100, i, $"p{i}"))],
            portCount:  pins);
        return new PdkKitPart(id, sym, new CcellFile { NumPorts = pins }, IconPath: null);
    }

    private static PaletteItem PaletteEntry(string kit, string partId, string generator) =>
        new(Kind:             SymbolKind.Generic,
            PortCount:        0,
            DisplayName:      partId,
            Category:         ComponentCategory.Other,
            SearchTerms:      [partId],
            IsCommon:         false,
            ExtraCategories:  null,
            Pdk:              new PdkPartRef(kit, partId),
            PCellGeneratorId: generator);

    // ── PdkKitRegistry ────────────────────────────────────────────────────────

    [Fact]
    public void OpeningASecondWorkspace_DoesNotUnmountTheFirstsKit()
    {
        PdkKitRegistry.SetKit(RootA, "KitA", [Part("PART_A")]);

        // Workspace B opens: it withdraws its OWN scope (empty) and mounts its own kit.
        PdkKitRegistry.ClearWorkspace(RootB);
        PdkKitRegistry.SetKit(RootB, "KitB", [Part("PART_B")]);

        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitA", "PART_A"), RootA));
        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitB", "PART_B"), RootB));
    }

    /// <summary>Closing B leaves A exactly as it was — the other half of R-mw1-4.</summary>
    [Fact]
    public void ClosingTheSecondWindow_LeavesTheFirstsKitMounted()
    {
        PdkKitRegistry.SetKit(RootA, "KitA", [Part("PART_A")]);
        PdkKitRegistry.SetKit(RootB, "KitB", [Part("PART_B")]);

        PdkKitRegistry.ClearWorkspace(RootB);

        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitA", "PART_A"), RootA));
        Assert.Null(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitB", "PART_B"), RootB));
    }

    /// <summary>
    /// The reference form is name-keyed and written into user files, so two workspaces referencing
    /// kits of the same name is ordinary — and each must get its own part, not the other's.
    /// </summary>
    [Fact]
    public void TwoWorkspacesMayMountKitsOfTheSameName_AndDoNotCollide()
    {
        PdkKitRegistry.SetKit(RootA, "SharedName", [Part("PART_A", pins: 2)]);
        PdkKitRegistry.SetKit(RootB, "SharedName", [Part("PART_B", pins: 3)]);

        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SharedName", "PART_A"), RootA));
        Assert.Null   (PdkKitRegistry.Find(PdkKitRegistry.RefFor("SharedName", "PART_A"), RootB));
        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("SharedName", "PART_B"), RootB));
        Assert.Null   (PdkKitRegistry.Find(PdkKitRegistry.RefFor("SharedName", "PART_B"), RootA));
    }

    /// <summary>A workspace that mounted nothing answers nothing, rather than the other one's kit.</summary>
    [Fact]
    public void AWorkspaceThatMountedNoKit_ResolvesNothing()
    {
        PdkKitRegistry.SetKit(RootA, "KitA", [Part("PART_A")]);
        Assert.Null(PdkKitRegistry.Find(PdkKitRegistry.RefFor("KitA", "PART_A"), RootB));
        Assert.False(PdkKitRegistry.HasKit(RootB, "KitA"));
    }

    // ── KitLayoutGenerators ───────────────────────────────────────────────────

    [Fact]
    public void OneWorkspacePublishingItsGenerators_DoesNotReplaceAnothers()
    {
        KitLayoutGenerators.Publish(RootA, [PaletteEntry("KitA", "PART_A", "gen_a")]);
        KitLayoutGenerators.Publish(RootB, [PaletteEntry("KitB", "PART_B", "gen_b")]);

        Assert.Equal("gen_a", KitLayoutGenerators.For(RootA, "KitA", "PART_A"));
        Assert.Equal("gen_b", KitLayoutGenerators.For(RootB, "KitB", "PART_B"));
        Assert.Null(KitLayoutGenerators.For(RootA, "KitB", "PART_B"));

        Assert.Equal(PdkKitRegistry.RefFor("KitA", "PART_A"), KitLayoutGenerators.PartRefFor(RootA, "gen_a"));
        Assert.Null(KitLayoutGenerators.PartRefFor(RootA, "gen_b"));

        KitLayoutGenerators.ClearWorkspace(RootB);
        Assert.Equal("gen_a", KitLayoutGenerators.For(RootA, "KitA", "PART_A"));
        Assert.Null(KitLayoutGenerators.For(RootB, "KitB", "PART_B"));
    }

    /// <summary>A refresher belongs to the workspace that installed it, and is not asked for another's
    /// lookup — it would start THAT workspace's interpreters to answer a question about this one.</summary>
    [Fact]
    public void ARefresherIsAskedOnlyForItsOwnWorkspace()
    {
        int askedForA = 0;
        KitLayoutGenerators.SetRefresher(RootA, () => { askedForA++; return false; });

        KitLayoutGenerators.For(RootB, "KitB", "PART_B");
        Assert.Equal(0, askedForA);

        KitLayoutGenerators.For(RootA, "KitA", "PART_A");
        Assert.Equal(1, askedForA);
    }

    // ── PCellRegistry resolvers ───────────────────────────────────────────────

    private sealed class StubPCellResolver(string id) : IPCellGeneratorResolver
    {
        public PCellGenerator? Resolve(string generatorId)
            => string.Equals(generatorId, id, StringComparison.OrdinalIgnoreCase)
                ? (_, _, _) => throw new NotSupportedException("never generated in this test")
                : null;

        public System.Collections.Generic.IReadOnlyCollection<string> KnownGeneratorIds => [id];
        public string  Describe()                     => id;
        public string? ContentKeyFor(string genId)    => null;
    }

    [Fact]
    public void WithdrawingOneWorkspacesPCellResolver_LeavesTheOthers()
    {
        var a = new StubPCellResolver("gen_a");
        var b = new StubPCellResolver("gen_b");
        PCellRegistry.AddResolver(a);
        PCellRegistry.AddResolver(b);

        PCellRegistry.RemoveResolver(b);

        Assert.True (PCellRegistry.TryGet("gen_a", out _));
        Assert.False(PCellRegistry.TryGet("gen_b", out _));
    }

    // ── ExternalDeviceRegistry resolvers ──────────────────────────────────────

    private sealed class StubProvider(string name) : IExternalDeviceProvider, IDisposable
    {
        public string Name { get; } = name;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;

        public System.Collections.Generic.IReadOnlyList<ExternalDeviceDescriptor> Describe() => [];
        public IExternalDeviceInstance Create(
            string typeId, System.Collections.Generic.IReadOnlyDictionary<string, string> parameters)
            => throw new NotSupportedException("never instantiated in this test");
    }

    private sealed class StubProviderResolver(StubProvider provider) : IExternalProviderResolver
    {
        public string Describe => provider.Name;
        public IExternalDeviceProvider? Resolve(string name)
            => string.Equals(name, provider.Name, StringComparison.OrdinalIgnoreCase) ? provider : null;
    }

    [Fact]
    public void WithdrawingOneWorkspacesDeviceResolver_LeavesTheOthersWorkersRunning()
    {
        var providerA = new StubProvider("WorkerA");
        var providerB = new StubProvider("WorkerB");
        var a = new StubProviderResolver(providerA);
        var b = new StubProviderResolver(providerB);
        ExternalDeviceRegistry.AddResolver(a);
        ExternalDeviceRegistry.AddResolver(b);

        // Resolve both, so each registry entry has a producing resolver recorded against it.
        Assert.NotNull(ExternalDeviceRegistry.Find("WorkerA"));
        Assert.NotNull(ExternalDeviceRegistry.Find("WorkerB"));

        ExternalDeviceRegistry.RemoveResolver(b);

        Assert.NotNull(ExternalDeviceRegistry.Find("WorkerA"));
        Assert.False(providerA.Disposed);
        Assert.True(providerB.Disposed);        // its worker was ended with its own workspace
        Assert.Null(ExternalDeviceRegistry.Find("WorkerB"));
    }

    /// <summary>
    /// A process-wide policy change ends the WORKERS and keeps the resolvers — otherwise every open
    /// workspace loses device resolution entirely until it is reopened, silently, in whichever window
    /// is not in front.
    /// </summary>
    [Fact]
    public void EndingResolvedProviders_KeepsTheResolversSoTheNextLookupProducesAFreshOne()
    {
        var first = new StubProvider("Worker");
        ExternalDeviceRegistry.AddResolver(new StubProviderResolver(first));
        Assert.NotNull(ExternalDeviceRegistry.Find("Worker"));

        ExternalDeviceRegistry.EndResolvedProviders();

        Assert.True(first.Disposed);
        Assert.NotNull(ExternalDeviceRegistry.Find("Worker"));   // the resolver is still there
    }

    // ── The walk-up that answers "on whose behalf" (R-mw1-5) ──────────────────

    [Fact]
    public void ADocumentResolvesItsKitAgainstItsOwnParentWorkspace()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "crf-mw1-" + Guid.NewGuid().ToString("N"));
        string wsA = Path.Combine(tmp, "A");
        string wsB = Path.Combine(tmp, "B");
        string cellInA = Path.Combine(wsA, "cells", "Amp", "schematic");
        try
        {
            Directory.CreateDirectory(cellInA);
            Directory.CreateDirectory(wsB);
            File.WriteAllText(Path.Combine(wsA, ".cws"), "{}");
            File.WriteAllText(Path.Combine(wsB, ".cws"), "{}");
            WorkspaceRootFinder.InvalidateCache();

            PdkKitRegistry.SetKit(wsA, "KitA", [Part("PART_A")]);

            string kitRef = PdkKitRegistry.RefFor("KitA", "PART_A");

            // A document inside workspace A resolves it; one inside B does not — and neither had to
            // be told which workspace it was in.
            Assert.Equal(CellSymbolState.Resolved,
                         CellSymbolResolver.Resolve(kitRef, cellInA).State);
            Assert.Equal(CellSymbolState.NotFound,
                         CellSymbolResolver.Resolve(kitRef, wsB).State);
        }
        finally
        {
            WorkspaceRootFinder.InvalidateCache();
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
