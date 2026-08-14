// ================================================================
//  HarmonicaAppMenuInjectorTests.cs — brief-harmonicarf-r3a §2.1, gates §3.4/§3.5
//
//  DO NOT call HarmonicaAppMenuInjector.BuildTopLevelItems (or its private Item(...) helper) from a
//  headless test. This was tried and DOES deadlock the whole test process — confirmed from an actual
//  `--blame-hang` minidump's managed stack, not inferred:
//
//      NativeMenuItem.set_Command
//        -> NativeMenuItem.CanExecuteChanged()
//        -> Dispatcher.Invoke(Action)          // SYNCHRONOUS
//        -> DispatcherOperation.Wait()          // blocks forever — no dispatcher pump exists here
//
//  Setting Command on a NativeMenuItem synchronously posts to Avalonia's UI-thread dispatcher and
//  waits for it. The real app always calls this from a UI-thread callback (AttachedToVisualTree,
//  SetDockedFocus, …), where a pump is running, so production is unaffected. This xunit host never
//  runs one at all, so the wait never returns. It does not throw and is not flaky in the sense of
//  "sometimes fails" — it is a deterministic property of NativeMenuItem.Command, and only fails to
//  bite when this thread happens to be the one Avalonia's Dispatcher lazily bound to (which is why an
//  isolated single-class run looked fine while a full `dotnet test` run hung solid).
//
//  Constructing a plain NativeMenuItem/NativeMenu and mutating Header/ToggleType/IsChecked/IsEnabled/
//  Click/Menu/Items IS safe headlessly (no TopLevel, no platform needed) — only Command is not. So
//  Inject/Withdraw (plain Items.Add/Remove, never touching Command) are exercised for real below,
//  against hand-built, Command-free stand-in items. BuildTopLevelItems' own shape is pinned by source
//  scan instead.
// ================================================================

using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public class HarmonicaAppMenuInjectorTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    private static string InjectorSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Harmonica", "HarmonicaAppMenuInjector.cs"));

    // ── §3.4 — Inject/Withdraw round-trip cleanly, twice, against the REAL methods ──────────────────

    [Fact]
    public void InjectThenWithdraw_RoundTripsCleanly_TwiceInARow()
    {
        var appMenu = new NativeMenu();
        var circuitRfFile = new NativeMenuItem("File");
        var circuitRfEdit = new NativeMenuItem("Edit");
        appMenu.Items.Add(circuitRfFile);
        appMenu.Items.Add(circuitRfEdit);

        var original = appMenu.Items.ToList();

        for (int round = 1; round <= 2; round++)
        {
            // Stand-ins for harmonicaRF's own Markers/Display/Grid — no Command set (see file header).
            var items = new[]
            {
                new NativeMenuItem("Markers"),
                new NativeMenuItem("Display"),
                new NativeMenuItem("Grid"),
            };
            HarmonicaAppMenuInjector.Inject(appMenu, items);

            // Exactly circuitRF's original items plus harmonicaRF's, in that order — never a
            // duplicate, even on the second round.
            Assert.Equal(original.Concat(items).ToList(), appMenu.Items.ToList());

            HarmonicaAppMenuInjector.Withdraw(appMenu, items);

            // Back to exactly the original list, BY REFERENCE.
            Assert.True(original.SequenceEqual(appMenu.Items, ReferenceEqualityComparer.Instance),
                $"round {round}: withdrawal must leave circuitRF's own items exactly as they were.");
        }
    }

    [Fact]
    public void Inject_NeverRemovesOrReordersCircuitRfsOwnItems()
    {
        var appMenu = new NativeMenu();
        var circuitRfFile = new NativeMenuItem("File");
        appMenu.Items.Add(circuitRfFile);

        HarmonicaAppMenuInjector.Inject(appMenu, [new NativeMenuItem("Markers")]);

        Assert.Same(circuitRfFile, appMenu.Items[0]);
    }

    [Fact]
    public void Withdraw_RemovesExactlyTheGivenItems_ByReference_NeverByHeaderMatch()
    {
        var appMenu = new NativeMenu();
        var lookalike = new NativeMenuItem("Markers");   // same header, a DIFFERENT instance
        appMenu.Items.Add(lookalike);

        var real = new NativeMenuItem("Markers");
        HarmonicaAppMenuInjector.Inject(appMenu, [real]);
        HarmonicaAppMenuInjector.Withdraw(appMenu, [real]);

        // The look-alike, added by someone else, must survive — Withdraw removes by reference only.
        Assert.Same(lookalike, Assert.Single(appMenu.Items));
    }

    // ── §1.2 — the owner-reported "Markers shows, Display/Grid do not" symptom ─────────────────────
    //
    // Diagnosis: a normal Inject/Withdraw/re-Inject cycle against hand-built stand-in items (below,
    // and InjectThenWithdraw_RoundTripsCleanly_TwiceInARow above) does NOT reproduce a throw — the
    // exact mechanism did not reproduce headlessly. What DOES reproduce is the underlying hazard: the
    // OLD Inject was a bare foreach with no rollback, so if ANY item in a call already carried a
    // Parent (from anywhere — a stale reference, a second concurrent injector, a bug elsewhere), the
    // items before it in the list would already have landed in appMenu.Items while the rest silently
    // never did. That is exactly "Markers present, Display and Grid absent" — item order preserved,
    // partial success. Inject is now atomic (see its own doc comment) — this proves it.

    [Fact]
    public void Inject_NeverLeavesAPartialSet_WhenALaterItemAlreadyHasAParent()
    {
        var appMenu = new NativeMenu();

        // Simulates the exact hazard: "Display" is not fresh — it already belongs to some other menu
        // when Inject is asked to add it as harmonicaRF's second top-level item.
        var elsewhere = new NativeMenu();
        var poisoned = new NativeMenuItem("Display");
        elsewhere.Items.Add(poisoned);

        var items = new[] { new NativeMenuItem("Markers"), poisoned, new NativeMenuItem("Grid") };

        Assert.Throws<InvalidOperationException>(() => HarmonicaAppMenuInjector.Inject(appMenu, items));

        // The OLD code would have left "Markers" behind here — the exact owner-reported symptom.
        // Atomic Inject rolls it back: the whole set lands, or none of it.
        Assert.Empty(appMenu.Items);
    }

    [Fact]
    public void InjectWithdrawReinject_SurvivesSeveralRounds_IncludingABandToggleAndAViewModelSwap()
    {
        var appMenu = new NativeMenu();
        var circuitRfFile = new NativeMenuItem("File");
        appMenu.Items.Add(circuitRfFile);
        var baseline = appMenu.Items.ToList();

        // Round 1 — ordinary dock-and-focus.
        var round1 = new[] { new NativeMenuItem("harmonicaRF"), new NativeMenuItem("Markers"),
                              new NativeMenuItem("Display"), new NativeMenuItem("Grid") };
        HarmonicaAppMenuInjector.Inject(appMenu, round1);
        Assert.Equal(baseline.Concat(round1).ToList(), appMenu.Items.ToList());

        // Round 2 — simulates RefreshInjectedItemsIfAny after a band toggle: withdraw the exact set,
        // then inject a FRESH set built from the (now-changed) view model. Never the same instances —
        // HarmonicaAppMenuInjector.BuildTopLevelItems always returns brand-new items.
        HarmonicaAppMenuInjector.Withdraw(appMenu, round1);
        var round2 = new[] { new NativeMenuItem("harmonicaRF"), new NativeMenuItem("Markers"),
                              new NativeMenuItem("Display"), new NativeMenuItem("Grid") };
        HarmonicaAppMenuInjector.Inject(appMenu, round2);
        Assert.Equal(baseline.Concat(round2).ToList(), appMenu.Items.ToList());

        // Round 3 — simulates a view-model swap (a different document becomes the docked-and-focused
        // holder): withdraw round 2's items, inject a third fresh set.
        HarmonicaAppMenuInjector.Withdraw(appMenu, round2);
        var round3 = new[] { new NativeMenuItem("harmonicaRF"), new NativeMenuItem("Markers"),
                              new NativeMenuItem("Display"), new NativeMenuItem("Grid") };
        HarmonicaAppMenuInjector.Inject(appMenu, round3);
        Assert.Equal(baseline.Concat(round3).ToList(), appMenu.Items.ToList());

        HarmonicaAppMenuInjector.Withdraw(appMenu, round3);
        Assert.True(baseline.SequenceEqual(appMenu.Items, ReferenceEqualityComparer.Instance));
    }

    // ── §3.5 / §2.1 — BuildTopLevelItems' own shape, pinned by SOURCE SCAN (see file header for why
    // it is never called directly here) ──────────────────────────────────────────────────────────

    [Fact]
    public void Item_AlwaysConstructsAFreshInstance_NeverReturnsACachedOrExternalOne()
    {
        string src = InjectorSource();

        // The one place a NativeMenuItem carrying a Command comes from — a plain `new(header) {...}`
        // object-creation expression, never a field, parameter, or cached reference. NativeMenu's own
        // list validator throws InvalidOperationException for an item that already has a Parent — a
        // reused (already-parented) instance would trip it immediately.
        Assert.Contains(
            "private static NativeMenuItem Item(string header, System.Windows.Input.ICommand? command, object? parameter = null)",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "=> new(header) { Command = command, CommandParameter = parameter };",
            src, System.StringComparison.Ordinal);

        // Stateless: no field (this repo's convention: a leading underscore) could hand back a
        // previously-built item across calls — every NativeMenuItem/NativeMenu handed out is
        // constructed fresh, inline, in the method that returns it.
        Assert.DoesNotContain("NativeMenuItem? _", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMenu? _", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMenuItem _", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("NativeMenu _", src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTopLevelItems_NeverIncludesFileEditOrHelp_TheyAlreadyDuplicateCircuitRfsOwnBar()
    {
        string src = InjectorSource();

        int start = src.IndexOf(
            "public static IReadOnlyList<NativeMenuItem> BuildTopLevelItems", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected to find BuildTopLevelItems.");
        string rest = src[start..];

        // brief-harmonicarf-r6a §1.3 — the injected set gained a fourth top-level item, "harmonicaRF"
        // (BuildHarmonicaRf), holding what used to live ONLY in the torn-off File/Edit menus. It is
        // named "harmonicaRF", never literally "File"/"Edit" — those two headers still duplicate
        // circuitRF's own bar and stay out of the injected set.
        Assert.DoesNotContain("\"File\"", rest, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"Edit\"", rest, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"Help\"", rest, System.StringComparison.Ordinal);
        Assert.Contains(
            "[BuildHarmonicaRf(vm), BuildMarkers(vm), BuildDisplay(vm), BuildGrid(vm)]",
            src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHarmonicaRf_NeverIncludesUndoOrRedo_CircuitRfsOwnEditAlreadyOwnsTheGesture()
    {
        string src = InjectorSource();

        int start = src.IndexOf("private static NativeMenuItem BuildHarmonicaRf", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected to find BuildHarmonicaRf.");
        int end = src.IndexOf("\n    private static NativeMenuItem BuildMarkers", start, System.StringComparison.Ordinal);
        Assert.True(end > start);
        string body = src[start..end];

        Assert.DoesNotContain("\"Undo\"", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"Redo\"", body, System.StringComparison.Ordinal);
        Assert.Contains("\"Settings…\"", body, System.StringComparison.Ordinal);
        Assert.Contains("\"Close\"", body, System.StringComparison.Ordinal);
    }
}
