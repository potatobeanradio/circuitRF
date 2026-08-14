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

        Assert.DoesNotContain("\"File\"", rest, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"Edit\"", rest, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"Help\"", rest, System.StringComparison.Ordinal);
        Assert.Contains("[BuildMarkers(vm), BuildDisplay(vm), BuildGrid(vm)]", src, System.StringComparison.Ordinal);
    }
}
