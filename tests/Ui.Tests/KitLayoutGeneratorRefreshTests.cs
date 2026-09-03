using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Which layout cell a placed kit part draws must not depend on a background reading having landed.
///
/// <para><b>Owner report: after importing a kit and placing its components, every one of them was
/// reported as having no layout artwork — including parts whose cells the kit plainly ships.</b> The
/// map is filled by <c>WorkspaceViewModel.RefreshPCellPaletteItems</c>, which has to START a kit's
/// interpreter and so runs off the UI thread. It was reachable from exactly one place — the workspace
/// PATH changing. A kit imported into an already-open workspace declares its cell library during that
/// import, and nothing re-read it afterwards, so the map stayed empty for the rest of the session and
/// only closing and reopening the workspace fixed it.</para>
///
/// <para><b>In the PDK collection because <see cref="KitLayoutGenerators"/> is process-wide, and this
/// class installs a HOOK on it</b> — a lookup in a class running alongside would otherwise reach a
/// refresher this one set, and publish over what that class had just published.</para>
///
/// <para>Two things close that. The publishing paths now all refresh (held by
/// <see cref="KitLayoutGeneratorRefreshWiringTests"/>), and a lookup that misses may ask once — which
/// is what makes the answer independent of timing rather than merely likelier to be right.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitLayoutGeneratorRefreshTests : IDisposable
{
    public KitLayoutGeneratorRefreshTests() => Reset();

    public void Dispose() => Reset();

    /// <summary>The registry is process-wide; leaving a refresher installed would reach other tests.</summary>
    private static void Reset()
    {
        KitLayoutGenerators.SetRefresher(null, null);
        KitLayoutGenerators.ResetAllForTests();
    }

    private static PaletteItem Part(string kit, string partId, string? generator) =>
        new(Kind:             SymbolKind.Generic,
            PortCount:        0,
            DisplayName:      partId,
            Category:         ComponentCategory.Other,
            SearchTerms:      [partId],
            IsCommon:         false,
            ExtraCategories:  null,
            Pdk:              new PdkPartRef(kit, partId),
            PCellGeneratorId: generator);

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A lookup against a map nothing has filled yet gets the answer, rather than the "this kit has
    /// no layout cell for that part" that reads identically to the kit genuinely having none.
    /// </summary>
    [Fact]
    public void ALookupBeforeTheReadingHasLandedStillGetsTheAnswer()
    {
        Assert.Null(KitLayoutGenerators.For(null, "a-kit", "a_part"));   // nothing published yet

        int asked = 0;
        KitLayoutGenerators.SetRefresher(null, () =>
        {
            asked++;
            KitLayoutGenerators.Publish(null, [Part("a-kit", "a_part", "a_cell")]);
            return true;
        });

        Assert.Equal("a_cell", KitLayoutGenerators.For(null, "a-kit", "a_part"));
        Assert.Equal(1, asked);

        // …and once it is published, the hook is not reached again.
        Assert.Equal("a_cell", KitLayoutGenerators.For(null, "a-kit", "a_part"));
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A part the kit genuinely has no cell for is still an ordinary miss. The hook is asked once and
    /// the answer stands — a lookup must not turn into a loop over a kit that will never name it.
    /// </summary>
    [Fact]
    public void APartWithNoLayoutCellIsStillAMissAndTheHookIsAskedOnce()
    {
        int asked = 0;
        KitLayoutGenerators.SetRefresher(null, () =>
        {
            asked++;
            KitLayoutGenerators.Publish(null, [Part("a-kit", "a_part", "a_cell")]);
            return true;
        });

        Assert.Null(KitLayoutGenerators.For(null, "a-kit", "model_only_part"));
        Assert.Equal(1, asked);
    }

    /// <summary>A hook that published nothing changes nothing, and one that throws is a miss — a
    /// reading that fails must never become the caller's exception.</summary>
    [Fact]
    public void AHookThatPublishesNothingOrThrowsIsJustAMiss()
    {
        KitLayoutGenerators.SetRefresher(null, () => false);
        Assert.Null(KitLayoutGenerators.For(null, "a-kit", "a_part"));

        KitLayoutGenerators.SetRefresher(null, () => throw new InvalidOperationException("no interpreter"));
        Assert.Null(KitLayoutGenerators.For(null, "a-kit", "a_part"));
    }

    /// <summary>
    /// The hook publishes, and publishing is itself something that could ask for a refresh. Asking
    /// from inside one would recurse without bound; the second lookup below is what would do it.
    /// </summary>
    [Fact]
    public void TheHookIsNotReEnteredFromInsideItself()
    {
        int depth = 0, maxDepth = 0;
        KitLayoutGenerators.SetRefresher(null, () =>
        {
            maxDepth = Math.Max(maxDepth, ++depth);
            try
            {
                KitLayoutGenerators.For(null, "a-kit", "another_part");   // would re-enter
                KitLayoutGenerators.Publish(null, [Part("a-kit", "a_part", "a_cell")]);
                return true;
            }
            finally { depth--; }
        });

        Assert.Equal("a_cell", KitLayoutGenerators.For(null, "a-kit", "a_part"));
        Assert.Equal(1, maxDepth);
    }

    /// <summary>Clearing the registry drops the mapping. It deliberately does NOT drop the hook — the
    /// workspace that installed one is still open, and the very next lookup is the one that needs it.</summary>
    [Fact]
    public void ClearDropsTheMappingAndKeepsTheHook()
    {
        int asked = 0;
        KitLayoutGenerators.SetRefresher(null, () => { asked++; return false; });

        KitLayoutGenerators.Publish(null, [Part("a-kit", "a_part", "a_cell")]);
        Assert.Equal("a_cell", KitLayoutGenerators.For(null, "a-kit", "a_part"));
        Assert.Equal(0, asked);

        KitLayoutGenerators.ClearWorkspace(null);
        Assert.Null(KitLayoutGenerators.For(null, "a-kit", "a_part"));
        Assert.Equal(1, asked);
    }
}

/// <summary>
/// Every path that can change what a kit's generator resolver would answer has to re-read it. Checked
/// against the SOURCE because <c>WorkspaceViewModel</c> cannot be constructed in a test — the same
/// arrangement this suite already uses for other view-model invariants.
/// </summary>
public sealed class KitLayoutGeneratorRefreshWiringTests
{
    private static string Source()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("WorkspaceViewModel.cs was not found above the test output.");
    }

    /// <summary>The body of one method, by brace matching from its signature.</summary>
    private static string BodyOf(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is no longer in WorkspaceViewModel — update this test.");

        int open = source.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }

    /// <summary>
    /// Reloading a kit's generator scripts re-decides what they offer, so which cell each part places
    /// has to be re-decided with it. This is the path a fresh import takes — the kit's cell library is
    /// declared during the import, and the resolver scanned the workspace before that existed.
    /// </summary>
    [Fact]
    public void ReloadingGeneratorsRefreshesWhichCellEachPartPlaces()
        => Assert.Contains("RefreshPCellPaletteItems()",
                           BodyOf(Source(), "private void ReloadPCellGenerators()"), StringComparison.Ordinal);

    /// <summary>Granting a kit permission to run makes its cells listable where a moment ago they were
    /// not — so the reading taken while it was refused has to be taken again.</summary>
    [Fact]
    public void GrantingPermissionRefreshesWhichCellEachPartPlaces()
        => Assert.Contains("RefreshPCellPaletteItems()",
                           BodyOf(Source(), "private void RequestPCellConsent("), StringComparison.Ordinal);

    /// <summary>The synchronous fallback is installed for the open workspace, and dropped with it — a
    /// hook left behind would answer for a workspace that is no longer open.</summary>
    [Fact]
    public void TheSynchronousFallbackIsInstalledAndDroppedWithTheWorkspace()
    {
        string body = BodyOf(Source(), "private void ResetPCellGenerators(");
        Assert.Contains("KitLayoutGenerators.SetRefresher(workspaceRootDir, RefreshPCellGeneratorsNow)", body, StringComparison.Ordinal);
        Assert.Contains("KitLayoutGenerators.SetRefresher(_mountedKitRoot, null)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starting a worker is announced. The event is raised in <c>src/Core</c> and means nothing until
    /// something subscribes; the subscription is one line in a constructor, which is exactly the kind
    /// of line a later edit drops without noticing — and the only symptom is the silence this exists
    /// to end. See <c>DeviceWorkerStartNotificationTests</c> for the event's own behaviour.
    /// </summary>
    [Fact]
    public void TheWorkerStartAnnouncementIsSubscribedTo()
    {
        string source = Source();
        Assert.Contains("ProcessDeviceWorkerTransport.Starting += OnDeviceWorkerStarting",
                        source, StringComparison.Ordinal);
        Assert.Contains("Messages.Info(text)",
                        BodyOf(source, "private void OnDeviceWorkerStarting("), StringComparison.Ordinal);
    }
}
