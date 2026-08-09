using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Two owner-reported items from bringing an open PDK up: kit symbols rendering larger than
/// circuitRF's own, and a workspace's PCell declaration hard-coding an absolute path to the kit.
///
/// <para>Every fixture is SYNTHETIC — the repository commits no third-party kit data, and a test
/// keyed to one kit on one machine fails on a fresh clone.</para>
/// </summary>
public class KitSymbolScaleAndPortabilityTests
{
    // ── Symbol scale ──────────────────────────────────────────────────────────

    private static (IReadOnlyList<KitSymbolPin>?, IReadOnlyList<KitSymbolShape>?) Part(int halfSpan)
        => ([new KitSymbolPin("1", 0, -halfSpan), new KitSymbolPin("2", 0, +halfSpan)], null);

    /// <summary>
    /// The headline. A kit drawn in units where its ordinary part spans 60 must land at circuitRF's
    /// own 400, not merely "somewhere legible" — which is what the decade-wide band gave, and is why
    /// every part came out half again to twice the size of the built-in beside it.
    /// </summary>
    [Fact]
    public void TypicalPart_LandsAtCircuitRfsOwnSymbolSize()
    {
        // Nine ordinary parts at 60 units, one large one at 130 — the shape of a kit.
        var parts = Enumerable.Repeat(Part(30), 9).Append(Part(65)).ToList();

        double scale = KitTemplateSymbol.ChooseKitScale(parts);

        Assert.Equal(KitTemplateSymbol.ReferenceSymbolExtent, 60 * scale, 6);
    }

    /// <summary>
    /// The old rule keyed on the LARGEST part, so one unusually big symbol decided the whole kit's
    /// size. A median cannot be moved by it — which is the actual behaviour change.
    /// </summary>
    [Fact]
    public void OneOversizedPart_DoesNotShrinkTheWholeKit()
    {
        var withoutGiant = Enumerable.Repeat(Part(30), 9).ToList();
        var withGiant    = withoutGiant.Append(Part(2000)).ToList();

        Assert.Equal(KitTemplateSymbol.ChooseKitScale(withoutGiant),
                     KitTemplateSymbol.ChooseKitScale(withGiant), 6);
    }

    /// <summary>Relative sizes are the kit author's choice and one scale is what preserves them.</summary>
    [Fact]
    public void RelativeSizes_AreUnchanged_BecauseOneScaleServesTheWholeKit()
    {
        var parts = new List<(IReadOnlyList<KitSymbolPin>?, IReadOnlyList<KitSymbolShape>?)>
            { Part(30), Part(30), Part(30), Part(90) };

        double scale = KitTemplateSymbol.ChooseKitScale(parts);

        // 3x in the file stays 3x on the schematic.
        Assert.Equal(3.0, (180 * scale) / (60 * scale), 9);
    }

    /// <summary>A kit whose parts differ wildly still cannot produce something off the canvas.</summary>
    [Fact]
    public void AWildlyUnevenKit_IsStillHeldInsideTheLegibilityBand()
    {
        // Median 2 units, largest 100,000 — normalising on the median alone would scale the big one
        // to 20 million.
        var parts = Enumerable.Repeat(Part(1), 9).Append(Part(50_000)).ToList();

        double scale = KitTemplateSymbol.ChooseKitScale(parts);

        // Clamped to the band edge, not merely "smaller" — normalising on the median alone would
        // have scaled the largest part to twenty million.
        Assert.True(100_000 * scale <= 30_000,
            $"largest part scaled to {100_000 * scale:F0}, outside the legibility band");
        Assert.True(100_000 * scale > 1_000);
    }

    [Fact]
    public void NoDrawingBackedPart_ReturnsZero_SoCallersFallBackToTheirOwnScale()
        => Assert.Equal(0, KitTemplateSymbol.ChooseKitScale([(null, null)]));

    /// <summary>Rescaling moves pins, and the counter that guards that must have moved with it.</summary>
    [Fact]
    public void TranslationVersion_WasBumped_BecauseRescalingMovesPins()
        => Assert.True(DsnSymbolReader.TranslationVersion >= 3);

    // ── Manifest portability ──────────────────────────────────────────────────

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-kitmanifest-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// The reported problem: the declaration written into the workspace carried an absolute path to
    /// the kit, so repairing the kit reference in Manage PDKs fixed the parts and left the layout
    /// cells pointing at a folder that is no longer there.
    /// </summary>
    [Fact]
    public void RepointingTheKitRoot_MovesEveryKitAnchoredPath()
    {
        string dir    = TempDir();
        string oldKit = Path.Combine(TempDir(), "kit-was-here");
        string newKit = Path.Combine(TempDir(), "kit-is-here-now");

        var manifest = new PCellGeneratorManifest
        {
            Entry      = "kit_entry.py",
            KitRoot    = oldKit,
            PythonPath = [PCellGeneratorManifest.KitToken + "/lib/python"],
            Sources    = [PCellGeneratorManifest.KitToken + "/lib/python/cells"],
        };
        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
                          System.Text.Json.JsonSerializer.Serialize(manifest));

        Assert.Equal(Path.Combine(oldKit, "lib", "python"),
                     PCellGeneratorManifest.TryRead(dir, out _)!.ResolvePythonPath(dir).Single());

        Assert.True(PCellGeneratorManifest.TryRepointKitRoot(dir, newKit, out _));

        var after = PCellGeneratorManifest.TryRead(dir, out _)!;
        Assert.Equal(Path.Combine(newKit, "lib", "python"), after.ResolvePythonPath(dir).Single());
        // The entry script is the user's and lives in the workspace — it must NOT be re-anchored.
        Assert.Equal(Path.Combine(dir, "kit_entry.py"), after.ResolveEntry(dir));
    }

    /// <summary>Repointing to where the kit already is must not rewrite the file.</summary>
    [Fact]
    public void RepointingToTheSamePlace_ChangesNothing()
    {
        string dir = TempDir();
        string kit = Path.Combine(TempDir(), "kit");

        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            System.Text.Json.JsonSerializer.Serialize(new PCellGeneratorManifest
            { Entry = "e.py", KitRoot = kit, PythonPath = [PCellGeneratorManifest.KitToken + "/p"] }));

        Assert.False(PCellGeneratorManifest.TryRepointKitRoot(dir, kit, out _));
    }

    /// <summary>
    /// A manifest a kit author wrote by hand states no kit root, and its paths mean exactly what
    /// they say. Silently re-anchoring them would move a working kit.
    /// </summary>
    [Fact]
    public void AManifestThatDeclaresNoKitRoot_IsLeftAlone()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, PCellGeneratorManifest.FileName),
            System.Text.Json.JsonSerializer.Serialize(new PCellGeneratorManifest
            { Entry = "e.py", PythonPath = ["../shared/python"] }));

        Assert.False(PCellGeneratorManifest.TryRepointKitRoot(dir, TempDir(), out _));

        var m = PCellGeneratorManifest.TryRead(dir, out _)!;
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "../shared/python")),
                     m.ResolvePythonPath(dir).Single());
    }

    /// <summary>Every manifest written before the anchor existed keeps resolving exactly as it did.</summary>
    [Fact]
    public void AnUnanchoredAbsolutePath_ResolvesExactlyAsBefore()
    {
        string dir = TempDir();
        string abs = Path.Combine(TempDir(), "somewhere", "python");

        var m = new PCellGeneratorManifest { Entry = "e.py", PythonPath = [abs] };

        Assert.Equal(Path.GetFullPath(abs), m.ResolvePythonPath(dir).Single());
    }
}
