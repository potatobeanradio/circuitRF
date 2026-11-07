using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  Materials editor redesign (2026-09-29): every property on one form, the temperature tables editable, and the 3D
//  view's Materials dialog hosting the same editor over the technology's lists — edits applied to their files on OK.
//  And the generic library: its thermal-era materials had no electrical values, so they read as stating nothing.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class MaterialsEditorRedesignTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-matredesign-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    /// <summary>The dialog edits copies: an edit to a library material, and a duplicate renamed, reach ONLY the list they
    /// were made in, on OK; an existing material's rename (which would leave every file naming it dangling) is refused.</summary>
    [Fact]
    public void TheDialog_EditsCopiesOfEachList_AndHandsBackOnlyTheListsItChanged()
    {
        var library = new List<TechMaterial>
        {
            new() { Name = "Gold", Sigma20 = 4.1e7, ThermalK = 318 },
            new() { Name = "Gallium nitride", Epsr = 9.5, ThermalK = 165.3 },
        };
        var picker = new MaterialPickerViewModel(
            [new MaterialSourceSeed("t.ctech (the technology's own)", null, [], null),
             new MaterialSourceSeed("lib.cmat", "/x/lib.cmat", library, null)],
            "t.ctech", startNew: false, current: "Gallium nitride", objectCount: 1, refusal: null);
        var table = picker.Table;
        Assert.True(table.CanDelete);                              // owner request, 2026-10-04: the dialog deletes too
        Assert.Equal("lib.cmat", table.TargetSource.Label);        // M9: the first library

        var gan = table.SelectedRow!;
        gan.ThermalKText = "150";
        gan.NameText = "GaN";                                      // an existing material: refused, name kept
        Assert.Equal("Gallium nitride", gan.Name);
        Assert.NotNull(table.Refusal);

        table.Duplicate();
        var copy = table.SelectedRow!;
        Assert.Equal("Gallium nitride copy", copy.Name);
        copy.NameText = "GaN epi";                                 // a new one is named here
        Assert.Equal("GaN epi", copy.Name);

        Assert.Equal(165.3, library[1].ThermalK);                  // the caller's list is untouched
        Assert.Null(picker.Accept());
        Assert.Equal("GaN epi", picker.ChosenName);
        var (seed, materials) = Assert.Single(picker.ChangedLists);
        Assert.Equal("/x/lib.cmat", seed.LibraryPath);
        Assert.Equal(150, materials.Single(m => m.Name == "Gallium nitride").ThermalK);
        Assert.Equal(150, materials.Single(m => m.Name == "GaN epi").ThermalK);
    }

    /// <summary>Add Tissue Materials lists the tissue library at once, to assign like any other list's; the button goes away, and
    /// OK hands the library back for the workspace to name (it changed nothing in it, so no list is committed). An earlier edit
    /// still undoes. A refusal is said in the dialog and adds nothing.</summary>
    [Fact]
    public void AddTissueMaterials_ListsTheTissueLibrary_ForOkToName()
    {
        var own = new List<TechMaterial> { new() { Name = "Gold", Sigma20 = 3e7 } };
        var tissues = MaterialLibraryPersistence.Deserialize(MaterialLibraries.ShippedRawJson(MaterialLibraries.TissueFileName));
        var seed = new MaterialSourceSeed(MaterialLibraries.TissueFileName, "/x/" + MaterialLibraries.TissueFileName, tissues, null);
        string? refusal = "'/x/tissue-materials.cmat' already exists";
        var picker = new MaterialPickerViewModel([new MaterialSourceSeed("t.ctech (the technology's own)", null, own, null)],
                                                 "t.ctech", startNew: false, current: "Gold", objectCount: 1, refusal: null)
        {
            TissueLibrary = () => refusal is null ? (seed, null) : (null, refusal),
        };
        picker.Table.SelectedRow!.EpsrText = "1";
        Assert.True(picker.OffersTissueMaterials);

        picker.AddTissueMaterialsCommand.Execute(null);
        Assert.Equal(refusal, picker.Refusal);
        Assert.DoesNotContain(picker.Table.Rows, r => r.Name == "Muscle");

        refusal = null;
        picker.AddTissueMaterialsCommand.Execute(null);
        Assert.False(picker.OffersTissueMaterials);
        Assert.Same(seed, picker.AddedTissueLibrary);
        Assert.Equal(1 + tissues.Count, picker.Table.Rows.Count);
        picker.UndoCommand.Execute(null);                                            // the Epsr edit, from before the list came
        Assert.Equal(1 + tissues.Count, picker.Table.Rows.Count);

        picker.Table.Select("Muscle");
        Assert.Null(picker.Accept());
        Assert.Equal("Muscle", picker.ChosenName);
        Assert.Empty(picker.ChangedLists);
    }

    /// <summary>The Built-in toggle lists the shipped materials the technology does not already name, each marked; editing one
    /// copies it — with the edit — into the target list as that list's own, and assigning one copies it as it ships.</summary>
    [Fact]
    public void BuiltInMaterials_AreListedByTheToggle_AndCopiedIntoTheTechnologyWhenEditedOrAssigned()
    {
        var own = new List<TechMaterial> { new() { Name = "Gold", Sigma20 = 3e7 } };
        var picker = new MaterialPickerViewModel([new MaterialSourceSeed("t.ctech (the technology's own)", null, own, null)],
                                                 "t.ctech", startNew: false, current: "Gold", objectCount: 1, refusal: null);
        var table = picker.Table;
        Assert.True(table.OffersBuiltIns);
        Assert.False(table.ShowBuiltIns);
        Assert.DoesNotContain(table.Rows, r => r.IsBuiltIn);

        table.ShowBuiltIns = true;
        var generic = MaterialLibraries.LoadGeneric();
        Assert.Equal(1 + generic.Count - 1, table.Rows.Count);                       // the technology's Gold is not listed twice
        Assert.Equal(3e7, table.Rows.Single(r => r.Name == "Gold").Material.Sigma20);
        Assert.All(table.Rows.Where(r => r.Name != "Gold"), r => Assert.True(r.IsBuiltIn && r.ShowsBuiltInMark && r.IsEditable && !r.IsNameEditable));

        table.Select("PTFE");
        table.SelectedRow!.EpsrText = "2.1";                                          // an edit adopts the material
        var ptfe = table.SelectedRow!;
        Assert.False(ptfe.IsBuiltIn);
        Assert.Equal(2.1, ptfe.Material.Epsr);
        Assert.Single(table.Rows, r => r.Name == "PTFE");

        table.Select("Soda-lime glass (window)");
        Assert.True(table.SelectedRow!.IsBuiltIn);
        Assert.Null(picker.Accept());                                                 // assigning adopts it as it ships
        Assert.Equal("Soda-lime glass (window)", picker.ChosenName);
        var (_, materials) = Assert.Single(picker.ChangedLists);
        Assert.Equal(["Gold", "PTFE", "Soda-lime glass (window)"], materials.Select(m => m.Name));
        Assert.Equal(6.31, materials[2].Epsr);
        Assert.Single(own);                                                           // the caller's list is untouched until OK
    }

    /// <summary>A k(T) table is edited point by point, each gesture one undo entry, kept sorted; a repeated temperature is
    /// refused and an empty table is written as no table.</summary>
    [Fact]
    public void ATemperatureTable_IsEditedPointByPoint_SortedAndOneUndoEntryEach()
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "lib.cmat");
        MaterialLibraryPersistence.SaveToFile(path, [new TechMaterial { Name = "SiC", Epsr = 9.7, ThermalK = 383 }]);
        var vm = new MaterialsEditorViewModel(path, MaterialLibraryPersistence.LoadFromFile(path));
        var k = vm.Table.Rows[0].KTable;

        k.AddPointCommand.Execute(null);                            // 20 °C at the constant
        k.AddPointCommand.Execute(null);                            // 45 °C at the last value
        k.Points[1].ValueText = "331";
        k.Points[1].TempText = "20";                                // a second point at 20 °C: refused
        Assert.NotNull(vm.Table.Refusal);
        k.Points[1].TempText = "-10";                               // re-sorted to the front
        Assert.Equal([(-10.0, 331.0), (20.0, 383.0)], vm.Working[0].ThermalKVsTemp!.Select(p => (p.TempC, p.Value)));

        vm.UndoCommand.Execute(null);
        Assert.Equal([(20.0, 383.0), (45.0, 331.0)], vm.Working[0].ThermalKVsTemp!.Select(p => (p.TempC, p.Value)));
        k.ClearCommand.Execute(null);
        Assert.Null(vm.Working[0].ThermalKVsTemp);
    }

    /// <summary>Every generic material states what it is — a conductor or a dielectric — so none reads as "states nothing"
    /// in the editor, and GaN and SiC state their permittivity. The silicon grades state both a conductivity and a permittivity
    /// on purpose, since a substrate's conductivity is its loss, so an object made of one says which it is by its Role.</summary>
    [Fact]
    public void EveryGenericMaterial_StatesItsRole()
    {
        var lib = MaterialLibraries.LoadGeneric();
        Assert.All(lib, m => Assert.Contains(C3dMaterialRole.Implied(m), m.Name.StartsWith("Silicon, ", StringComparison.Ordinal)
            ? new[] { C3dImpliedRole.Ambiguous }
            : new[] { C3dImpliedRole.Conductor, C3dImpliedRole.Dielectric }));
        Assert.Equal(9.5, lib.Single(m => m.Name == "Gallium nitride").Epsr);
        Assert.Equal(9.7, lib.Single(m => m.Name == "Silicon carbide (4H, semi-insulating)").Epsr);
    }

    /// <summary>A .cmat's delete — the button or a row's context menu — is warned about rather than refused when the material is
    /// in use: the host is asked with the uses, a "no" deletes nothing, a "yes" deletes it as one undo entry.</summary>
    [Fact]
    public async Task ACmatDeleteOfAMaterialInUse_AsksTheHost_AndGoesAheadOnlyWhenConfirmed()
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "lib.cmat");
        MaterialLibraryPersistence.SaveToFile(path, [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }, new TechMaterial { Name = "SiC", Epsr = 9.7 }]);
        var vm = new MaterialsEditorViewModel(path, MaterialLibraryPersistence.LoadFromFile(path));
        vm.Table.UsedBy = name => name == "Gold" ? ["body 'Lid' of t.ctech"] : [];
        IReadOnlyList<string>? asked = null;
        bool answer = false;
        vm.Table.ConfirmDelete = (_, uses) => { asked = uses; return Task.FromResult(answer); };

        var gold = vm.Table.Rows.Single(r => r.Name == "Gold");
        Assert.True(gold.OffersDelete && gold.CanBeDeleted);
        await gold.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(["body 'Lid' of t.ctech"], asked);
        Assert.Contains(vm.Working, m => m.Name == "Gold");               // "no": nothing deleted

        answer = true;
        await gold.DeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Working, m => m.Name == "Gold");
        Assert.Null(vm.Table.Refusal);
        vm.UndoCommand.Execute(null);
        Assert.Contains(vm.Working, m => m.Name == "Gold");
    }

    /// <summary>The 3D view's Materials dialog deletes from its copy: a material made in the dialog at once, one that already
    /// existed only after the host's warning — a "no" keeps it — the delete reaches the file's list only through OK, and the dialog's
    /// own undo takes it back.</summary>
    [Fact]
    public async Task TheDialog_DeletesANewMaterialAtOnce_AndAnExistingOneOnlyAfterTheHostsWarning()
    {
        var library = new List<TechMaterial> { new() { Name = "Gold", Sigma20 = 4.1e7 }, new() { Name = "SiC", Epsr = 9.7 } };
        var picker = new MaterialPickerViewModel([new MaterialSourceSeed("lib.cmat", "/x/lib.cmat", library, null)],
                                                 "t.ctech", startNew: false, current: "Gold", objectCount: 1, refusal: null);
        var asked = new List<(string Name, string? Library)>();
        bool answer = false;
        picker.ConfirmDeleteExisting = (name, lib) => { asked.Add((name, lib)); return Task.FromResult(answer); };
        var table = picker.Table;

        table.Add();
        await table.SelectedRow!.DeleteCommand.ExecuteAsync(null);         // made here: no warning
        Assert.Empty(asked);
        Assert.DoesNotContain(table.Rows, r => r.Name == "Material1");

        var gold = table.Rows.Single(r => r.Name == "Gold");
        await gold.DeleteCommand.ExecuteAsync(null);                       // existing: warned, "no"
        Assert.Equal([("Gold", (string?)"/x/lib.cmat")], asked);
        Assert.Contains(table.Rows, r => r.Name == "Gold");

        answer = true;
        await gold.DeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(table.Rows, r => r.Name == "Gold");
        Assert.Equal(2, library.Count);                                    // the caller's list waits for OK
        var (_, materials) = Assert.Single(picker.ChangedLists);
        Assert.Equal(["SiC"], materials.Select(m => m.Name));

        picker.UndoCommand.Execute(null);                                  // the dialog's own history brings it back
        Assert.Contains(table.Rows, r => r.Name == "Gold");
        Assert.Empty(picker.ChangedLists);
        picker.RedoCommand.Execute(null);
        Assert.DoesNotContain(table.Rows, r => r.Name == "Gold");
    }

    /// <summary>The list's swatch is a colour the material has: its ordinary-view colour when stated, else the realistic view's
    /// base colour — not one stand-in per role, which drew every unstated metal gold.</summary>
    [Fact]
    public void TheListSwatch_IsTheOrdinaryViewColour_ElseTheRealisticBaseColour()
    {
        var list = new List<TechMaterial>
        {
            new() { Name = "Silver", Sigma20 = 6.3e7, Appearance = new TechAppearance { BaseColor = "#FCFAF5", Metallic = 1 } },
            new() { Name = "Copper", Sigma20 = 5.8e7, Color = "#B87333", Appearance = new TechAppearance { BaseColor = "#FAD1C2" } },
        };
        var table = new MaterialsTableViewModel(() => list, (mutate, _) => mutate(), "this library");
        Assert.Equal(Avalonia.Media.Color.FromRgb(0xFC, 0xFA, 0xF5), table.Rows.Single(r => r.Name == "Silver").ListSwatchColor);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0xB8, 0x73, 0x33), table.Rows.Single(r => r.Name == "Copper").ListSwatchColor);
    }
}
