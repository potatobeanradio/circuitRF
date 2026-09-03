using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A kit's LAYOUT artwork: finding the parametric-cell library it ships, declaring it, and working
/// out which of its cells is a given schematic part's layout view.
///
/// <para>The fixtures are synthetic — this repository commits no third-party kit data — but every rule
/// they exercise was settled by running it against a real one, which is what the shapes below
/// reproduce in miniature: a wrapper package whose cells live in a subpackage, and a kit that names
/// its schematic part and its layout cell differently while both name the same model.</para>
/// </summary>
// KitLayoutGenerators is process-wide and this class publishes into it; serialized with everything
// else that reads it, for the reason PdkToolsDirectoryCollection already records.
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitLayoutArtworkTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-kitpcell-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A kit holding a wrapper package whose real cells are one level down.</summary>
    private static string BuildKit(string root, int cellModules = 3, bool markerInSubpackage = true)
    {
        string wrapper = Path.Combine(root, "libs", "python", "kit_cells");
        string devices = Path.Combine(wrapper, "devices");
        Directory.CreateDirectory(devices);

        File.WriteAllText(Path.Combine(wrapper, "__init__.py"), "");
        File.WriteAllText(Path.Combine(devices, "__init__.py"), "");
        // The wrapper carries one helper written against the API, exactly as a kit's does —
        // which is why "the first package that qualifies" is the wrong rule.
        File.WriteAllText(Path.Combine(wrapper, "tech_helper.py"), "from cni.dlo import *\n");

        for (int i = 0; i < cellModules; i++)
            File.WriteAllText(
                Path.Combine(devices, $"device{i}_code.py"),
                markerInSubpackage ? "from cni.dlo import *\nclass d(DloGen): pass\n" : "x = 1\n");

        return root;
    }

    // ── discovery ─────────────────────────────────────────────────────────────

    [Fact]
    public void Find_PrefersTheSubpackageHoldingTheCells_NotTheWrapperAroundIt()
    {
        string kit = BuildKit(TempDir());

        var pkg = KitPCellLibrary.Find(kit, out var also);

        Assert.NotNull(pkg);
        Assert.Equal("kit_cells.devices", pkg!.PackageName);
        Assert.Equal(3, pkg.CellModuleCount);
        Assert.Equal(Path.Combine(kit, "libs", "python"), pkg.PythonPathRoot);
        // The wrapper is the same library one level up, not an alternative to offer.
        Assert.Empty(also);
    }

    [Fact]
    public void Find_KitWithNoCellLibrary_ReturnsNull_RatherThanGuessing()
    {
        string kit = BuildKit(TempDir(), markerInSubpackage: false);
        // Remove the wrapper's own helper too, so nothing anywhere names the API.
        File.Delete(Path.Combine(kit, "libs", "python", "kit_cells", "tech_helper.py"));

        Assert.Null(KitPCellLibrary.Find(kit));
    }

    [Fact]
    public void Find_IgnoresAPackageDirectoryWithNoInitFile()
    {
        string kit = TempDir();
        string loose = Path.Combine(kit, "scripts");
        Directory.CreateDirectory(loose);
        File.WriteAllText(Path.Combine(loose, "thing.py"), "from cni.dlo import *\n");

        Assert.Null(KitPCellLibrary.Find(kit));
    }

    // ── declaration ───────────────────────────────────────────────────────────

    [Fact]
    public void EnsureDeclared_WritesAManifestCircuitRfCanReadBack()
    {
        string kit = BuildKit(TempDir());
        string ws  = TempDir();
        var pkg = KitPCellLibrary.Find(kit)!;

        string? dir = KitPCellLibrary.EnsureDeclared(ws, "some-kit", pkg, out string? problem, out bool created);

        Assert.Null(problem);
        Assert.True(created);
        Assert.NotNull(dir);

        var manifest = PCellGeneratorManifest.TryRead(dir!, out string? readProblem);
        Assert.Null(readProblem);
        Assert.NotNull(manifest);
        Assert.True(File.Exists(manifest!.ResolveEntry(dir!)));
        Assert.Contains(manifest.ResolvePythonPath(dir!), Directory.Exists);
        Assert.Contains(pkg.PackageName, File.ReadAllText(manifest.ResolveEntry(dir!)));
    }

    [Fact]
    public void EnsureDeclared_NeverOverwritesWhatIsAlreadyThere()
    {
        string kit = BuildKit(TempDir());
        string ws  = TempDir();
        var pkg = KitPCellLibrary.Find(kit)!;

        string dir = KitPCellLibrary.EnsureDeclared(ws, "some-kit", pkg, out _, out _)!;
        string manifestPath = Path.Combine(dir, PCellGeneratorManifest.FileName);
        string edited = File.ReadAllText(manifestPath).Replace("kit_entry.py", "my_entry.py");
        File.WriteAllText(manifestPath, edited);

        string? again = KitPCellLibrary.EnsureDeclared(ws, "some-kit", pkg, out _, out bool created);

        Assert.Equal(dir, again);
        Assert.False(created);
        Assert.Equal(edited, File.ReadAllText(manifestPath));
    }

    [Fact]
    public void EnsureDeclared_WithNoWorkspace_SaysSoRatherThanThrowing()
    {
        var pkg = new KitPCellPackage("/nowhere", "a.b", 1);

        string? dir = KitPCellLibrary.EnsureDeclared("", "k", pkg, out string? problem, out bool created);

        Assert.Null(dir);
        Assert.False(created);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    // ── which cell is which part ──────────────────────────────────────────────

    private static PaletteItem Part(string kit, string id, string model = "") =>
        new(SymbolKind.Generic, 0, id, ComponentCategory.Other, [id], false, null,
            new PdkPartRef(kit, id, null, PdkKitRegistry.RefFor(kit, id), "", model));

    [Fact]
    public void Compose_MatchesAPartToACellWhenBothDeclareTheSameModel()
    {
        var parts = new[] { Part("k", "cap_thing", model: "cap_thing_model") };
        var kits   = new Dictionary<string, string> { ["thing"] = "k" };
        var models = new Dictionary<string, string> { ["thing"] = "cap_thing_model" };

        var composed = KitPaletteMerge.Compose(parts, kits, models);

        var tile = Assert.Single(composed);
        Assert.Equal("cap_thing", tile.Pdk!.PartId);
        Assert.Equal("thing", tile.PCellGeneratorId);
    }

    [Fact]
    public void Compose_TwoCellsClaimingOneModel_MatchNeither_RatherThanPickOne()
    {
        var parts = new[] { Part("k", "fet", model: "fet_model") };
        var kits   = new Dictionary<string, string> { ["plain"] = "k", ["rf"] = "k" };
        var models = new Dictionary<string, string> { ["plain"] = "fet_model", ["rf"] = "fet_model" };

        var composed = KitPaletteMerge.Compose(parts, kits, models);

        Assert.Null(composed.Single(i => i.Pdk!.PartId == "fet").PCellGeneratorId);
        // Both cells are still offered — layout-only, but real and placeable.
        Assert.Equal(2, composed.Count(i => i.Pdk!.PartId is "plain" or "rf"));
    }

    [Fact]
    public void Compose_WithNoModelsSupplied_BehavesExactlyAsItDidBefore()
    {
        var parts = new[] { Part("k", "cap_thing", model: "cap_thing_model") };
        var kits  = new Dictionary<string, string> { ["thing"] = "k" };

        var composed = KitPaletteMerge.Compose(parts, kits);

        Assert.Null(composed.Single(i => i.Pdk!.PartId == "cap_thing").PCellGeneratorId);
    }

    [Fact]
    public void Compose_PartIdMatchStillWins_EvenWhenModelsAreAvailable()
    {
        var parts = new[] { Part("k", "rpoly", model: "res_poly") };
        var kits   = new Dictionary<string, string> { ["rpoly"] = "k" };
        var models = new Dictionary<string, string> { ["rpoly"] = "something_else" };

        var composed = KitPaletteMerge.Compose(parts, kits, models);

        Assert.Equal("rpoly", Assert.Single(composed).PCellGeneratorId);
    }

    // ── when one model is claimed by several of each ──────────────────────────

    private static PaletteItem PartWith(string kit, string id, string model, params string[] parameters) =>
        new(SymbolKind.Generic, 0, id, ComponentCategory.Other, [id], false, null,
            new PdkPartRef(kit, id, null, PdkKitRegistry.RefFor(kit, id), "", model, parameters));

    /// <summary>
    /// The shape a kit takes when it offers one device twice — an RF form and a plain one — and
    /// names the same model from both layout cells. Two parts, two cells, one model: the model step
    /// must refuse (it requires exactly one on each side), and this is what settles it.
    ///
    /// <para><b>Coverage alone only settles half of it, which is the point of propagating.</b> The RF
    /// part carries <c>rfmode</c> and only the RF cell accepts it — forced. The plain part is accepted
    /// by BOTH cells, so it is settled only once the RF cell has been taken. Measured:
    /// all four of its MOS devices are exactly this, and all eight parts pair correctly.</para>
    /// </summary>
    [Fact]
    public void Compose_TwoPartsAndTwoCellsOnOneModel_ArePairedByWhatEachAccepts()
    {
        var parts = new[]
        {
            PartWith("k", "dev_rf", "one_model", "ModelLibrary", "w", "l", "ng", "m", "rfmode"),
            PartWith("k", "dev",    "one_model", "ModelLibrary", "w", "l", "ng", "m"),
        };
        var kits   = new Dictionary<string, string> { ["cellRf"] = "k", ["cellPlain"] = "k" };
        var models = new Dictionary<string, string> { ["cellRf"] = "one_model", ["cellPlain"] = "one_model" };
        var declared = new Dictionary<string, IReadOnlyList<string>>
        {
            ["cellRf"]    = ["model", "w", "l", "ng", "m", "rfmode", "guard_ring"],
            ["cellPlain"] = ["model", "w", "l", "ng", "m", "ws"],
        };

        var composed = KitPaletteMerge.Compose(parts, kits, models, declared);

        Assert.Equal("cellRf",    composed.Single(i => i.Pdk!.PartId == "dev_rf").PCellGeneratorId);
        Assert.Equal("cellPlain", composed.Single(i => i.Pdk!.PartId == "dev").PCellGeneratorId);

        // Both cells are now a part's artwork, so neither is left over as a tile of its own.
        Assert.DoesNotContain(composed, i => i.Pdk!.PartId is "cellRf" or "cellPlain");
    }

    /// <summary>
    /// Nothing here reads a NAME. Pairing "rf" against "rf" would be inventing a convention the kit
    /// never stated, and a kit that spells it the other way round would silently get its artwork
    /// swapped — which draws perfectly and is wrong. The names are crossed here on purpose.
    /// </summary>
    [Fact]
    public void Compose_PairsOnWhatIsAccepted_NotOnWhatThingsAreCalled()
    {
        var parts = new[]
        {
            PartWith("k", "alpha", "one_model", "w", "extra"),
            PartWith("k", "beta",  "one_model", "w"),
        };
        var kits   = new Dictionary<string, string> { ["beta_cell"] = "k", ["alpha_cell"] = "k" };
        var models = new Dictionary<string, string> { ["beta_cell"] = "one_model", ["alpha_cell"] = "one_model" };
        var declared = new Dictionary<string, IReadOnlyList<string>>
        {
            ["beta_cell"]  = ["w", "extra"],   // the cell NAMED beta accepts what ALPHA has
            ["alpha_cell"] = ["w"],
        };

        var composed = KitPaletteMerge.Compose(parts, kits, models, declared);

        Assert.Equal("beta_cell",  composed.Single(i => i.Pdk!.PartId == "alpha").PCellGeneratorId);
        Assert.Equal("alpha_cell", composed.Single(i => i.Pdk!.PartId == "beta").PCellGeneratorId);
    }

    /// <summary>
    /// Two cells that accept exactly the same things say nothing about which part is which, so the
    /// group is left for the palette rather than guessed at. Measured: its two Schottky
    /// cells declare identical parameter sets — and no part claims that model at all, so both stay
    /// layout-only either way.
    /// </summary>
    [Fact]
    public void Compose_CellsThatAcceptTheSameThings_SettleNothing()
    {
        var parts = new[]
        {
            PartWith("k", "one", "one_model", "w"),
            PartWith("k", "two", "one_model", "w"),
        };
        var kits   = new Dictionary<string, string> { ["a"] = "k", ["b"] = "k" };
        var models = new Dictionary<string, string> { ["a"] = "one_model", ["b"] = "one_model" };
        var declared = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["w", "l"],
            ["b"] = ["w", "l"],
        };

        var composed = KitPaletteMerge.Compose(parts, kits, models, declared);

        Assert.Null(composed.Single(i => i.Pdk!.PartId == "one").PCellGeneratorId);
        Assert.Null(composed.Single(i => i.Pdk!.PartId == "two").PCellGeneratorId);
        // Still offered on their own, which is the existing recovery the refusal message points at.
        Assert.Equal(2, composed.Count(i => i.Pdk!.PartId is "a" or "b"));
    }

    /// <summary>
    /// circuitRF's own <c>ModelLibrary</c> row is excluded from the comparison — a kit has never heard
    /// of it, so requiring a cell to declare it would rule out every cell for every part and this step
    /// would settle nothing, anywhere.
    /// </summary>
    [Fact]
    public void Compose_CircuitRfsOwnRowIsNotHeldAgainstTheKit()
    {
        var parts = new[]
        {
            PartWith("k", "dev_rf", "one_model", "ModelLibrary", "w", "rfmode"),
            PartWith("k", "dev",    "one_model", "ModelLibrary", "w"),
        };
        var kits   = new Dictionary<string, string> { ["cellRf"] = "k", ["cellPlain"] = "k" };
        var models = new Dictionary<string, string> { ["cellRf"] = "one_model", ["cellPlain"] = "one_model" };
        var declared = new Dictionary<string, IReadOnlyList<string>>
        {
            ["cellRf"]    = ["w", "rfmode"],
            ["cellPlain"] = ["w"],
        };

        var composed = KitPaletteMerge.Compose(parts, kits, models, declared);

        Assert.Equal("cellRf",    composed.Single(i => i.Pdk!.PartId == "dev_rf").PCellGeneratorId);
        Assert.Equal("cellPlain", composed.Single(i => i.Pdk!.PartId == "dev").PCellGeneratorId);
    }

    /// <summary>
    /// Purely additive: with no parameter declarations supplied, every earlier step behaves exactly as
    /// it did, and the ambiguous group is refused exactly as it was.
    /// </summary>
    [Fact]
    public void Compose_WithNoInterfacesSupplied_IsUnchanged()
    {
        var parts = new[]
        {
            PartWith("k", "dev_rf", "one_model", "w", "rfmode"),
            PartWith("k", "dev",    "one_model", "w"),
        };
        var kits   = new Dictionary<string, string> { ["cellRf"] = "k", ["cellPlain"] = "k" };
        var models = new Dictionary<string, string> { ["cellRf"] = "one_model", ["cellPlain"] = "one_model" };

        var composed = KitPaletteMerge.Compose(parts, kits, models);

        Assert.Null(composed.Single(i => i.Pdk!.PartId == "dev_rf").PCellGeneratorId);
        Assert.Null(composed.Single(i => i.Pdk!.PartId == "dev").PCellGeneratorId);
    }

    /// <summary>
    /// The id rules still win. A part whose id IS the generator id is matched by step one, and the
    /// interfaces are never consulted for it — this step exists for what the earlier ones refuse, and
    /// must not get a second opinion on what they already settled.
    /// </summary>
    [Fact]
    public void Compose_AnIdMatchIsNotSecondGuessedByTheInterfaces()
    {
        var parts = new[]
        {
            PartWith("k", "cellRf", "one_model", "w"),          // id-matches cellRf
            PartWith("k", "dev",    "one_model", "w", "rfmode"),
        };
        var kits   = new Dictionary<string, string> { ["cellRf"] = "k", ["cellPlain"] = "k" };
        var models = new Dictionary<string, string> { ["cellRf"] = "one_model", ["cellPlain"] = "one_model" };
        var declared = new Dictionary<string, IReadOnlyList<string>>
        {
            ["cellRf"]    = ["w", "rfmode"],   // would have taken "dev" on interfaces alone
            ["cellPlain"] = ["w"],
        };

        var composed = KitPaletteMerge.Compose(parts, kits, models, declared);

        Assert.Equal("cellRf", composed.Single(i => i.Pdk!.PartId == "cellRf").PCellGeneratorId);
    }

    // ── the published answer ──────────────────────────────────────────────────

    [Fact]
    public void KitLayoutGenerators_PublishesWhatTheePaletteSettled_AndForgetsOnClear()
    {
        var parts = new[] { Part("k", "cap_thing", model: "cap_thing_model") };
        var kits   = new Dictionary<string, string> { ["thing"] = "k" };
        var models = new Dictionary<string, string> { ["thing"] = "cap_thing_model" };

        KitLayoutGenerators.Publish(null, KitPaletteMerge.Compose(parts, kits, models));
        Assert.Equal("thing", KitLayoutGenerators.For(null, "k", "cap_thing"));
        Assert.Null(KitLayoutGenerators.For(null, "k", "not_a_part"));

        KitLayoutGenerators.ResetAllForTests();
        Assert.Null(KitLayoutGenerators.For(null, "k", "cap_thing"));
    }
}
