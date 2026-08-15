// ================================================================
//  HarmonicaR7aMenuTests.cs — brief-harmonicarf-r7a §2.5
//
//  §2.4's own bug: a MenuItem that carries BOTH a Click handler and an ItemsSource (children) never
//  raises Click — pointing at or clicking it only ever opens the submenu. That was the marker menu's
//  "VSWR: <val>" row and all three "Γ = …"/"Z = …" rows, each carrying a lone "Set…" child. The fix
//  flattened all four; this pins the structural rule so the defect cannot come back at either site.
// ================================================================

using System;
using System.IO;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR7aMenuTests
{
    private static string ViewSource() => ReadSource(
        "src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Removes <c>//</c>-to-end-of-line and <c>/* … */</c> spans — the same simple,
    /// string-literal-blind stripper <c>EmFrameworkFreeTests.StripComments</c> already uses. A
    /// source-scan test that reads commented-out code has bitten this repo before (see that file's
    /// own remark); being blind to string literals only makes this stricter, never more permissive.</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    private static string MethodBody(string src, string startMarker, string endMarker)
    {
        int m = src.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(m >= 0, $"could not find '{startMarker}'");
        int mEnd = src.IndexOf(endMarker, m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0, $"could not find '{endMarker}' after '{startMarker}'");
        return src[m..mEnd];
    }

    [Fact]
    public void BuildMarkerMenu_NoItemEverSetsBothClickAndItemsSource()
    {
        string body = StripComments(MethodBody(ViewSource(),
            "private void BuildMarkerMenu(", "\n    private async System.Threading.Tasks.Task ShowMarkerSetVswrDialogAsync"));

        // R8B §7.1 added ONE legitimate ItemsSource here: the "VSWR: <val>" value row, whose ONLY
        // child is "Set…" — a submenu, exactly like Contour Plane/Harmonic/Efficiency Metric in
        // BuildSmithTitleMenu. R7A §2.4's actual rule survives: THAT SAME row must carry no Click of
        // its own (a MenuItem with children never raises Click).
        int itemsSourceAt = body.IndexOf("ItemsSource", StringComparison.Ordinal);
        Assert.True(itemsSourceAt >= 0, "expected the VSWR value row's ItemsSource to be present");

        int blockStart = body.LastIndexOf("new MenuItem", itemsSourceAt, StringComparison.Ordinal);
        int blockEnd = body.IndexOf("});", itemsSourceAt, StringComparison.Ordinal);
        Assert.True(blockStart >= 0 && blockEnd > blockStart);
        string vswrValueRow = body[blockStart..blockEnd];
        Assert.DoesNotContain(".Click +=", vswrValueRow, StringComparison.Ordinal);

        // And there is exactly one such ItemsSource-carrying item in this method — not more.
        Assert.Equal(itemsSourceAt, body.IndexOf("ItemsSource", StringComparison.Ordinal));
        Assert.DoesNotContain("ItemsSource", body[(itemsSourceAt + "ItemsSource".Length)..], StringComparison.Ordinal);

        // Sanity: the method still wires real interaction, so an empty/gutted method can't pass this
        // vacuously. R8B §5 routed every leaf item through Item(...)/Toggle(...), which wire their
        // own Click internally — neither literal ".Click +=" appears directly in this method's body
        // any more, so the sanity check is "it still calls the shared builders" instead.
        Assert.Contains("Item(", body, StringComparison.Ordinal);
        Assert.Contains("Toggle(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormatRow_IsFlattened_NoItemsSource_ClicksDirectlyToTheSetDialog()
    {
        string body = StripComments(MethodBody(ViewSource(),
            "private MenuItem BuildFormatRow(", "\n    /// <summary>R-h9r2-7's \"Set…\""));

        Assert.DoesNotContain("ItemsSource", body, StringComparison.Ordinal);
        Assert.Contains("ShowMarkerSetDialogAsync(h, marker, format)", body, StringComparison.Ordinal);
    }

    // ── §2.3 — the Fluent MenuItem Icon/checkmark trap: Autoscale and Locked never carry ToggleType ──

    [Fact]
    public void AutoscaleAndLocked_NeverCarryToggleType_TheIconAloneCarriesState()
    {
        string body = StripComments(MethodBody(ViewSource(),
            "private static void AddAutoscaleLockedItems(", "\n    // ── §4 (R2A)"));

        // R9A §10 — Locked now shares Toggle's own checkbox glyph pair (CheckboxOutline/
        // CheckboxBlankOutline), the same pair "Show Grid Points" uses, rather than a Lock/
        // LockOpenVariant pair — the owner wants Locked to read as the checkbox toggle it is.
        // Toggle itself never sets ToggleType (see Toggle's own doc comment), so this still holds.
        Assert.DoesNotContain("ToggleType", body, StringComparison.Ordinal);
        Assert.Contains("Toggle(\"Autoscale\", autoscaleOn, onAutoscaleClick)", body, StringComparison.Ordinal);
        Assert.Contains("Toggle(\"Locked\", !autoscaleOn, onLockedClick)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MaterialIconKind.ArrowExpandAll", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MaterialIconKind.Lock", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MaterialIconKind.LockOpenVariant", body, StringComparison.Ordinal);
    }

    // ── §2.2 — every leaf action item routes through the ONE Item(...) helper ──────────────────────

    [Fact]
    public void CopyAndFormatRowsAndMarkerActions_AllRouteThroughTheSharedItemHelper()
    {
        string src = ViewSource();

        Assert.Contains("private static MenuItem Item(string header, MaterialIconKind? icon, Action onClick,",
            src, StringComparison.Ordinal);

        // Spot-check a representative item from each of §2.2's call sites — Copy, the three format
        // rows (R8B §6: icon: null, not a substitute glyph), Set… (VSWR), Add Grid Points, Add Grid
        // Points to VSWR (R9A §6), Remove, DCIV Sweeps…, Axis Limits….
        foreach (string needle in new[]
        {
            "Item(\"Copy\", MaterialIconKind.ContentCopy,",
            "Item(header, icon: null,",
            "Item(\"Set…\", MaterialIconKind.Cog,",
            "Item(\"Add Grid Points\", MaterialIconKind.PlusCircleOutline,",
            "Item(\"Add Grid Points to VSWR\", MaterialIconKind.PlusCircleMultipleOutline,",
            "Item($\"Remove {marker.Name}\", MaterialIconKind.Delete,",
            "Item(\"DCIV Sweeps…\", MaterialIconKind.Cog,",
            "Item(\"Axis Limits…\", MaterialIconKind.Cog,",
        })
            Assert.Contains(needle, src, StringComparison.Ordinal);
    }
}
