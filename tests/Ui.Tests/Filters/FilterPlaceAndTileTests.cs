using System.Globalization;
using Avalonia.Input;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// Brief FD-P: the Filter tile (bandstop glyph, pickers, greyed rows, the Designer's settings kept on
/// the block), placing the designed filter (Place filter, Place component, Replace this Filter) and
/// Synthesise… on a Filter.
/// </summary>
public class FilterPlaceAndTileTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static EditableComponent NewFilter(string name = "FLT1", double x = 0, double y = 0)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Filter, X = x, Y = y };
        comp.Parameters.AddRange(FilterPlacement.DefaultParameters());
        return comp;
    }

    /// <summary>A Filter as it was saved before FD-P and before EllipticType: the first eleven parameters.</summary>
    private static EditableComponent OldFilter()
    {
        var comp = NewFilter();
        int keep = comp.Parameters.FindIndex(p => p.Name == "EllipticType");
        comp.Parameters.RemoveRange(keep, comp.Parameters.Count - keep);
        return comp;
    }

    private static async Task<LcFilterDesignerViewModel> Designed(
        FilterResponse response, NetworkForm form, Action<LcFilterDesignerViewModel>? edit = null)
    {
        var vm = new LcFilterDesignerViewModel { Response = response, Form = form };
        edit?.Invoke(vm);
        await vm.DesignAsync(0);
        Assert.True(vm.Elements.Count > 0, vm.Status);
        return vm;
    }

    private static SchematicViewModel EmptySheet(out SchematicEditModel model)
    {
        model = new SchematicEditModel { GridSize = 100, GridSnap = true };
        return new SchematicViewModel(model);
    }

    // ── §3.1 / §3.2: the tile ──────────────────────────────────────────────

    [Fact]
    public void TheFormPickerOffersBandstop()
    {
        var model = new SchematicEditModel();
        var comp = NewFilter();
        model.Components.Add(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp);
        var form = editor.Rows.Single(r => r.Name == "Form");
        Assert.True(form.IsChoiceParam);
        Assert.Equal(["Lowpass", "Highpass", "Bandpass", "Bandstop"], form.ChoiceOptions);
    }

    /// <summary>
    /// §3.1 case 2: the glyph is redrawn when Form changes through the Properties panel's own
    /// command — the middle wave struck for a bandstop, the outer two clear.
    /// </summary>
    [Fact]
    public void SettingFormToBandstopRedrawsTheGlyph()
    {
        var vm = EmptySheet(out var model);
        var comp = NewFilter();
        model.Components.Add(comp);
        model.NotifyChanged();

        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        updated.Single(p => p.Name == "Form").Expression = "Bandstop";
        vm.Execute(new SetParametersCommand(model, comp, updated));

        var drawn = vm.RenderModel!.Components.Single(c => c.Id == comp.Id).InstanceSymbol;
        Assert.NotNull(drawn);
        Assert.Same(BuiltInSymbols.PrimitivesForFilter(NetworkForm.Bandstop), drawn);
        Assert.NotSame(BuiltInSymbols.PrimitivesForFilter(NetworkForm.Bandpass), drawn);

        // Three waves; one slash, on the middle wave (it is the only line within the middle band).
        var lines = drawn!.Primitives.OfType<LinePrimitive>()
            .Where(l => Math.Abs(l.X1 - l.X2) < 60 && Math.Abs(l.Y1 - l.Y2) > 1).ToList();
        var slash = Assert.Single(lines);
        Assert.InRange((slash.Y1 + slash.Y2) / 2, -27.5, 27.5);
    }

    [Fact]
    public void TheDesignerPickersOfferTheirEnumsMembers()
    {
        Assert.Equal(["A", "B", "C", "Auto"], ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "EllipticType"));
        foreach (string t in ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "EllipticType")!)
            Assert.True(EllipticTypes.TryParse(t, out _), t);
        Assert.Equal(Enum.GetNames<FilterOrderMode>(), ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "OrderMode"));
        Assert.Equal(Enum.GetNames<FilterFirstElement>(), ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "First"));
        Assert.Equal(Enum.GetNames<FilterSeriesKind>(), ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "Values"));
    }

    [Theory]
    [InlineData("Butterworth", "Lowpass", "Ripple", true)]
    [InlineData("InvChebyshev", "Lowpass", "Ripple", true)]
    [InlineData("Chebyshev", "Lowpass", "Ripple", false)]
    [InlineData("Chebyshev", "Lowpass", "Astop", true)]
    [InlineData("Elliptic", "Lowpass", "Astop", false)]
    [InlineData("Chebyshev", "Lowpass", "EllipticType", true)]
    [InlineData("Elliptic", "Lowpass", "EllipticType", false)]
    [InlineData("Chebyshev", "Bandstop", "Fc", true)]
    [InlineData("Chebyshev", "Bandstop", "F1", false)]
    [InlineData("Chebyshev", "Highpass", "F2", true)]
    [InlineData("Chebyshev", "Highpass", "Fc", false)]
    [InlineData("Chebyshev", "Bandpass", "IL", false)]
    public void ARowTheResponseOrFormDoesNotReadIsGreyed(string response, string form, string row, bool greyed)
    {
        var model = new SchematicEditModel();
        var comp = NewFilter();
        comp.Parameters.Single(p => p.Name == "Response").Expression = response;
        comp.Parameters.Single(p => p.Name == "Form").Expression = form;
        model.Components.Add(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp);
        var r = editor.Rows.Single(x => x.Name == row);
        Assert.Equal(!greyed, r.ExpressionEnabled);
        if (greyed) Assert.False(string.IsNullOrEmpty(r.DisabledReason));
    }

    private static ParameterEditorViewModel EditorOn(EditableComponent comp, out SchematicViewModel vm)
    {
        var model = new SchematicEditModel();
        model.Components.Add(comp);
        vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);
        return editor;
    }

    private static async Task<EditableComponent> DesignedFilter()
    {
        var designer = await Designed(FilterResponse.Chebyshev, NetworkForm.Bandpass);
        var comp = NewFilter();
        comp.Parameters.Clear();
        comp.Parameters.AddRange(designer.AppliedParameters(FilterPlacement.DefaultParameters())!);
        return comp;
    }

    [Fact]
    public void APlainFilterShowsNoDesignerRows_InTheDesignersOrder()
    {
        var editor = EditorOn(NewFilter(), out _);
        Assert.False(FilterPlacement.IsDesigned(NewFilter()));
        Assert.DoesNotContain(editor.Rows, r => ComponentTypeRegistry.IsLcFilterDesignerParameter(SymbolKind.Filter, r.Name));
        Assert.Equal(["Zin", "Zout", "Order", "Response", "Form", "Ripple", "Astop", "EllipticType", "Fc", "F1", "F2", "IL"],
                     editor.Rows.Select(r => r.Name));
        Assert.All(editor.Rows.Where(r => r.Name is "Zin" or "Order" or "Form"), r => Assert.True(r.ExpressionEnabled));
    }

    /// <summary>
    /// A Filter carrying a Designer design is locked: every row but IL is greyed and read-only, a
    /// picker included, so the only way to change it is Synthesise….
    /// </summary>
    [Fact]
    public async Task ADesignedFilterLocksEveryRowButIL()
    {
        var comp = await DesignedFilter();
        Assert.True(FilterPlacement.IsDesigned(comp));
        var editor = EditorOn(comp, out var vm);
        Assert.Contains(editor.Rows, r => r.Name == "Ap");
        foreach (var row in editor.Rows)
        {
            if (row.Name == "IL") { Assert.True(row.ExpressionEnabled); continue; }
            Assert.False(row.ExpressionEnabled, row.Name);
            Assert.True(row.ExpressionReadOnly, row.Name);
            Assert.StartsWith("This Filter was designed", row.DisabledReason);
        }

        // The picker and the text box write nothing.
        int before = vm.UndoRedo.Entries.Count();
        var form = editor.Rows.Single(r => r.Name == "Form");
        form.SelectedChoice = "Lowpass";
        var order = editor.Rows.Single(r => r.Name == "Order");
        order.StagedExpression = "9";
        order.CommitExpression();
        Assert.Equal(before, vm.UndoRedo.Entries.Count());
        Assert.Equal("Bandpass", comp.Parameters.Single(p => p.Name == "Form").Expression);

        // The view greys the picker too, not only the text box.
        string xaml = File.ReadAllText(Source("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml"));
        Assert.Matches(@"ItemsSource=""\{Binding ChoiceOptions\}""\s+SelectedItem=""\{Binding SelectedChoice\}""\s+IsVisible=""\{Binding IsChoiceParam\}""\s+IsEnabled=""\{Binding ExpressionEnabled\}""", xaml);
    }

    [Theory]
    [InlineData(FilterResponse.Butterworth, false)]
    [InlineData(FilterResponse.Chebyshev, true)]
    [InlineData(FilterResponse.InvChebyshev, false)]
    [InlineData(FilterResponse.Elliptic, true)]
    [InlineData(FilterResponse.Bessel, false)]
    public void TheDesignersRippleBoxIsEnabledOnlyWhereTheResponseReadsIt(FilterResponse response, bool enabled)
    {
        var vm = new LcFilterDesignerViewModel { Response = FilterResponse.Gaussian };
        vm.Response = response;
        Assert.Equal(enabled, vm.RippleRelevant);
    }

    [Fact]
    public void TheDesignersActionsAreOnItsToolbar_AndTheMaskLabelToggleIsGone()
    {
        string xaml = File.ReadAllText(Source("src", "Ui", "Views", "Filters", "LcFilterDesignerWindow.axaml"));
        int bar = xaml.IndexOf("<Border DockPanel.Dock=\"Top\"", StringComparison.Ordinal);
        Assert.True(bar >= 0);
        string toolbar = xaml[bar..xaml.IndexOf("</Border>", xaml.IndexOf("SaveNetlistButton", bar, StringComparison.Ordinal), StringComparison.Ordinal)];
        // Each frequency box carries its own prefix picker; the one shared picker is gone.
        foreach (string unit in (string[])["UnitA", "UnitB", "UnitC", "UnitD", "UnitFmin", "UnitFmax"])
            Assert.Contains($"SelectedItem=\"{{Binding {unit}}}\"", xaml);
        Assert.DoesNotContain("SelectedItem=\"{Binding Unit}\"", xaml);
        foreach (string name in (string[])["DesignButton", "ApplyButton", "PlaceFilterButton", "PlaceComponentButton", "ReplaceButton", "SaveNetlistButton"])
        {
            Assert.Contains($"Name=\"{name}\"", toolbar);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(xaml, $"Name=\"{name}\"").Count);
        }
        Assert.DoesNotContain("Content=\"Place filter\"", xaml);
        Assert.DoesNotContain("ShowMaskLabels", xaml);
        Assert.DoesNotContain("FormatText", xaml);
        // One plain glyph per action: composites of part glyphs were illegible at 16 px.
        Assert.DoesNotContain("PaletteGlyphControl", toolbar);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(toolbar, "<mi:MaterialIcon ").Count);
        // A prefix picker shares its box's value column; a third side-panel column squeezed the
        // shared label column out of line with every other card.
        Assert.DoesNotContain("SharedSizeGroup=\"FltUnit\"", xaml);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(xaml, "Classes=\"withunit\"").Count);
    }

    // ── §3.3: the Designer's settings on the block, invisible downstream ──

    [Fact]
    public void TheDesignerSettingsNeverReachTheNetlist()
    {
        string Line(EditableComponent filter)
        {
            var model = new SchematicEditModel();
            model.Components.Add(filter);
            var inst = Assert.Single(NetExtractor.Extract(model).TestBench.Instances);
            return string.Join(" ", inst.Overrides.Select(o => $"{o.Name}={o.Expression}"));
        }

        var bare = OldFilter();
        bare.Parameters.Add(new EditableParameter { Name = "EllipticType", Expression = "A" });
        var full = NewFilter();
        Assert.Equal(Line(bare), Line(full));
        Assert.DoesNotContain("Fs=", Line(full));
        foreach (var p in full.Parameters.Where(p => ComponentTypeRegistry.IsLcFilterDesignerParameter(SymbolKind.Filter, p.Name)))
            Assert.DoesNotContain(p.Name + "=", Line(full).Split(' ').Select(t => t[..(t.IndexOf('=') + 1)]));
    }

    /// <summary>A Filter saved before FD-P loads, simulates and saves back byte for byte.</summary>
    [Fact]
    public void AnOldFilterLoadsSimulatesAndRoundTripsUnchanged()
    {
        var model = new SchematicEditModel { GridSize = 100 };
        model.Components.Add(OldFilter());
        string saved = SchematicPersistence.Serialize(model, "old");

        var (loaded, _, _) = SchematicPersistence.Deserialize(saved);
        Assert.Equal(saved, SchematicPersistence.Serialize(loaded, "old"));
        Assert.Equal(11, loaded.Components.Single().Parameters.Count);

        var extracted = NetExtractor.Extract(loaded);
        var netlist = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
        Assert.IsType<FilterModel>(Assert.Single(netlist.Components).Model);
    }

    /// <summary>Designer → Apply → reopen gives back all four edges, Ap and the settings exactly.</summary>
    [Theory]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Lowpass)]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Highpass)]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Bandpass)]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Bandstop)]
    [InlineData(FilterResponse.InvChebyshev, NetworkForm.Bandpass)]
    [InlineData(FilterResponse.Elliptic, NetworkForm.Bandstop)]
    public async Task ApplyThenReopenGivesBackTheWholeSpecification(FilterResponse response, NetworkForm form)
    {
        var first = await Designed(response, form, v =>
        {
            if (form is NetworkForm.Lowpass) { v.FaText = "1"; v.FbText = "1.7"; }
            if (form is NetworkForm.Highpass) { v.FaText = "0.6"; v.FbText = "1"; }
            if (response == FilterResponse.InvChebyshev) { v.FaText = "0.3"; v.FdText = "3.5"; v.AsText = "30"; }
            v.ApText = "0.7";              // not the Ripple: Ap must come back from Ap, not be derived
            v.RippleText = "0.2";
            v.First = FilterFirstElement.Series;
            v.Series = FilterSeriesKind.E24;
        });
        first.Unit = "MHz";
        await first.DesignAsync(0);
        var block = NewFilter();
        block.Parameters.Clear();
        block.Parameters.AddRange(first.AppliedParameters(FilterPlacement.DefaultParameters())!);

        var second = new LcFilterDesignerViewModel();
        second.SeedFrom(block);
        await second.DesignAsync(0);

        Assert.Equal("MHz", second.Unit);
        Assert.Equal((first.FaText, first.FbText), (second.FaText, second.FbText));
        if (form is NetworkForm.Bandpass or NetworkForm.Bandstop)
            Assert.Equal((first.FcText, first.FdText), (second.FcText, second.FdText));
        Assert.Equal(first.ApText, second.ApText);
        Assert.Equal(first.RippleText, second.RippleText);
        Assert.Equal(first.OrderMode, second.OrderMode);
        Assert.Equal(first.OrderText, second.OrderText);
        Assert.Equal(FilterFirstElement.Series, second.First);
        Assert.Equal(FilterSeriesKind.E24, second.Series);
        Assert.Equal(first.Elements.Select(e => e.Used), second.Elements.Select(e => e.Used));
    }

    /// <summary>
    /// Each edge box has its own prefix: changing one rescales only that box, the specification
    /// does not move, and Apply then reopen gives every box back in its own prefix.
    /// </summary>
    [Fact]
    public async Task EachEdgeKeepsItsOwnPrefix_ThroughApplyAndReopen()
    {
        var first = await Designed(FilterResponse.Chebyshev, NetworkForm.Bandpass);
        var before = first.FilterAssignments()!.ToDictionary(a => a.Name);
        string fb = first.FbText, fc = first.FcText;

        first.UnitA = "MHz";
        first.UnitD = "kHz";
        Assert.Equal(double.Parse(before["Fs1"].Expression, CultureInfo.InvariantCulture) * 1e3,
                     double.Parse(first.FaText, CultureInfo.InvariantCulture), 6);
        Assert.Equal((fb, fc), (first.FbText, first.FcText));   // the other boxes did not move
        await first.DesignAsync(0);

        var applied = first.FilterAssignments()!.ToDictionary(a => a.Name);
        Assert.Equal(("MHz", "GHz", "GHz", "kHz"),
                     (applied["Fs1"].Unit, applied["Fp1"].Unit, applied["Fp2"].Unit, applied["Fs2"].Unit));
        Assert.Equal(("GHz", "GHz"), (applied["F1"].Unit, applied["F2"].Unit));
        foreach (string n in (string[])["F1", "F2", "Order"])
            Assert.Equal(before[n].Expression, applied[n].Expression);

        var block = NewFilter();
        block.Parameters.Clear();
        block.Parameters.AddRange(first.AppliedParameters(FilterPlacement.DefaultParameters())!);
        var second = new LcFilterDesignerViewModel();
        second.SeedFrom(block);
        Assert.Equal(("MHz", "GHz", "GHz", "kHz"), (second.UnitA, second.UnitB, second.UnitC, second.UnitD));
        Assert.Equal((first.FaText, first.FbText, first.FcText, first.FdText),
                     (second.FaText, second.FbText, second.FcText, second.FdText));
    }

    [Fact]
    public void ThePartsSettingReadsBackWhatIsWritten()
    {
        var parsed = LcFilterDesignerViewModel.ParseParts("L1=Lib: IndA|IndA-10N; C2=Lib: CapB|CapB-1p0").ToList();
        Assert.Equal([("L1", "Lib: IndA", "IndA-10N"), ("C2", "Lib: CapB", "CapB-1p0")], parsed);
        Assert.Empty(LcFilterDesignerViewModel.ParseParts("garbage; =x|y; L1=|p"));
    }

    // ── §1: placing ─────────────────────────────────────────────────────────

    [Fact]
    public void AnArmedFragmentPlacesOnAClickAsOneUndoStep()
    {
        var vm = EmptySheet(out var model);
        model.Components.Add(new EditableComponent { InstanceName = "L1", Symbol = SymbolKind.Inductor, X = -3000, Y = 0 });
        model.NotifyChanged();
        string before = SchematicPersistence.Serialize(model, "s");
        int undoBefore = vm.UndoRedo.Entries.Count();

        var fragment = new SchematicEditModel { GridSize = 100 };
        fragment.Components.Add(new EditableComponent { InstanceName = "L1", Symbol = SymbolKind.Inductor, X = 0, Y = 0 });
        fragment.Components.Add(new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.Capacitor, X = 300, Y = 0 });
        vm.PointerWorldProvider = () => (1000, 1000);
        Assert.True(vm.ArmFragmentPlacement(fragment));
        Assert.True(vm.IsPasteArmed);
        Assert.Single(model.Components);

        vm.OnPointerPressed(1000, 1000, KeyModifiers.None);
        Assert.False(vm.IsPasteArmed);
        Assert.Equal(3, model.Components.Count);
        Assert.Equal(3, model.Components.Select(c => c.InstanceName).Distinct().Count());
        Assert.Equal(undoBefore + 1, vm.UndoRedo.Entries.Count());

        vm.UndoRedo.Undo();
        Assert.Equal(before, SchematicPersistence.Serialize(model, "s"));
    }

    [Fact]
    public void WithoutAPointerTheFragmentWaitsAtTheViewCentre()
    {
        var vm = EmptySheet(out _);
        vm.ViewportProvider = () => new Ui.Schematic.SchematicPasteGeometry.ViewRect(0, 0, 4000, 2000);
        bool raised = false;
        vm.PlacementActivationRequested += () => raised = true;
        var fragment = new SchematicEditModel { GridSize = 100 };
        fragment.Components.Add(new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.Capacitor });
        Assert.True(vm.ArmFragmentPlacement(fragment));
        Assert.True(raised);
        Assert.Equal((2000.0, 1000.0), (vm.Overlay.DuplicateGhosts![0].X, vm.Overlay.DuplicateGhosts[0].Y));
    }

    /// <summary>The placed ladder IS the ladder: the topology gate of the copy, ends on IN and OUT.</summary>
    [Theory]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Bandpass)]
    [InlineData(FilterResponse.Elliptic, NetworkForm.Lowpass)]
    [InlineData(FilterResponse.Butterworth, NetworkForm.Bandstop)]
    [InlineData(FilterResponse.Elliptic, NetworkForm.Bandpass)]
    public async Task PlaceFilterPutsTheLadderOnTheSheetBetweenINAndOUT(FilterResponse response, NetworkForm form)
    {
        var designer = await Designed(response, form, v =>
        {
            if (form == NetworkForm.Lowpass) { v.FaText = "1"; v.FbText = "1.6"; }
        });
        var sheet = EmptySheet(out var model);
        designer.SchematicProvider = () => sheet;
        designer.RefreshPlacement();
        Assert.True(designer.CanPlace, designer.PlaceTip);

        sheet.PointerWorldProvider = () => (0, 0);
        designer.PlaceFilter();
        Assert.True(sheet.IsPasteArmed);
        sheet.OnPointerPressed(0, 0, KeyModifiers.None);

        Assert.DoesNotContain(model.Components, c => c.Symbol is SymbolKind.TermG or SymbolKind.Term);
        var tb = NetExtractor.Extract(model).TestBench;
        var byName = tb.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);
        var (_, ladder) = new CnlReader().Read(designer.Cnl);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        void Bind(string node, string net, string why)
        {
            if (map.TryGetValue(node, out var had))
                Assert.True(had == net, $"{why}: node {node} is on net {had} and on net {net}");
            else
            {
                Assert.False(used.TryGetValue(net, out var other), $"{why}: net {net} joins nodes {other} and {node}");
                map[node] = net;
                used[net] = node;
            }
        }
        var terms = ladder.Instances.Where(i => i.Reference == "Term").OrderBy(i => i.InstanceName).ToList();
        Bind(terms[0].NetBindings[0], "IN", "port 1");
        Bind(terms[1].NetBindings[0], "OUT", "port 2");
        foreach (var e in ladder.Instances.Where(i => i.Reference != "Term"))
        {
            Assert.True(byName.TryGetValue(e.InstanceName, out var inst), $"{e.InstanceName} is not on the sheet");
            var nets = inst!.NetBindings;
            var (a, b) = (e.NetBindings[0], e.NetBindings[1]);
            bool straight = !(map.TryGetValue(a, out var na) && na != nets[0])
                            && !(map.TryGetValue(b, out var nb) && nb != nets[1]);
            Bind(a, straight ? nets[0] : nets[1], e.InstanceName);
            Bind(b, straight ? nets[1] : nets[0], e.InstanceName);
        }
    }

    [Fact]
    public void ASecondPlacedLadderTakesItsOwnEndNames()
    {
        var model = new SchematicEditModel();
        model.NetLabels.Add(new EditableNetLabel { Name = "IN" });
        Assert.Equal(("IN2", "OUT2"), FilterPlacement.EndNames(model));
        Assert.Equal(("IN", "OUT"), FilterPlacement.EndNames(new SchematicEditModel()));
    }

    /// <summary>Place component: the block's parameters are Apply's, and it simulates the Designer's ideal curve.</summary>
    [Theory]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Bandstop)]
    [InlineData(FilterResponse.Elliptic, NetworkForm.Lowpass)]
    [InlineData(FilterResponse.InvChebyshev, NetworkForm.Highpass)]
    public async Task PlaceComponentPlacesTheDesignersFilter(FilterResponse response, NetworkForm form)
    {
        var designer = await Designed(response, form, v =>
        {
            if (form == NetworkForm.Lowpass) { v.FaText = "1"; v.FbText = "1.6"; }
            if (form == NetworkForm.Highpass) { v.FaText = "0.6"; v.FbText = "1"; }
        });
        var sheet = EmptySheet(out var model);
        designer.SchematicProvider = () => sheet;
        sheet.PointerWorldProvider = () => (500, 500);
        designer.PlaceComponent();
        sheet.OnPointerPressed(500, 500, KeyModifiers.None);

        var placed = Assert.Single(model.Components);
        Assert.Equal(SymbolKind.Filter, placed.Symbol);
        var expected = designer.AppliedParameters(FilterPlacement.DefaultParameters())!;
        Assert.Equal(expected.Select(p => (p.Name, p.Expression, p.Unit)),
                     placed.Parameters.Select(p => (p.Name, p.Expression, p.Unit)));

        var extracted = NetExtractor.Extract(model);
        var m = Assert.IsType<FilterModel>(Assert.Single(new Elaborator(extracted.Library).Elaborate(extracted.TestBench).Components).Model);
        var ideal = designer.LastIdeal!;
        foreach (double f in new[] { 0.3e9, 0.9e9, 1.0e9, 1.2e9, 2.5e9 })
        {
            double w = 2 * Math.PI * f;
            var (a11, a21, _) = m.Network.At(w);
            var (b11, b21, _) = ideal.At(w);
            Assert.True((a21 - b21).Magnitude < 1e-9 && (a11 - b11).Magnitude < 1e-9, $"{f:G3} Hz: {a21} vs {b21}");
        }
    }

    [Fact]
    public async Task TheButtonsSayWhyTheyAreDisabled()
    {
        var designer = await Designed(FilterResponse.Chebyshev, NetworkForm.Bandpass);
        designer.RefreshPlacement();
        Assert.False(designer.CanPlace);
        Assert.Contains("no schematic", designer.PlaceTip);

        var sheet = EmptySheet(out _);
        designer.SchematicProvider = () => sheet;
        designer.FbText = "5";   // Fp1 above Fp2: refused
        await designer.DesignAsync(0);
        Assert.False(designer.CanPlace);
        Assert.Equal(designer.Status, designer.PlaceTip);
        Assert.Contains("bandpass needs", designer.PlaceTip);
    }

    /// <summary>
    /// Replace this Filter: the ladder takes the block's nets by name as ONE undo step, and undo
    /// puts the sheet back byte for byte.
    /// </summary>
    [Fact]
    public async Task ReplaceThisFilterSwapsTheBlockForItsLadderInOneUndoStep()
    {
        var sheet = EmptySheet(out var model);
        var filter = NewFilter("FLT1", 0, 0);
        model.Components.Add(filter);
        var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = -600, Y = 0, Rotation = SymbolRotation.R90 };
        var r2 = new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 600, Y = 0, Rotation = SymbolRotation.R90 };
        model.Components.Add(r1);
        model.Components.Add(r2);
        var pins = model.PortDefsOf(filter).OrderBy(d => d.PortIndex).Select(d => model.PortWorldOf(filter, d)).ToList();
        var r1Pins = model.PortDefsOf(r1).Select(d => model.PortWorldOf(r1, d)).OrderBy(p => p.X).ToList();
        var r2Pins = model.PortDefsOf(r2).Select(d => model.PortWorldOf(r2, d)).OrderBy(p => p.X).ToList();
        var w1 = new EditableWire(); w1.Points.Add(r1Pins[^1]); w1.Points.Add(pins[0]);
        var w2 = new EditableWire(); w2.Points.Add(pins[1]); w2.Points.Add(r2Pins[0]);
        model.Wires.Add(w1);
        model.Wires.Add(w2);
        model.NotifyChanged();
        string before = SchematicPersistence.Serialize(model, "s");
        var beforeNets = NetExtractor.Extract(model).TestBench.Instances.ToDictionary(i => i.InstanceName);

        var designer = new LcFilterDesignerViewModel { Schematic = sheet, Target = filter };
        designer.SeedFrom(filter);
        await designer.DesignAsync(0);
        Assert.True(designer.CanReplaceTarget);
        designer.ReplaceTarget = true;
        int undoBefore = sheet.UndoRedo.Entries.Count();
        designer.PlaceFilter();

        Assert.Equal(undoBefore + 1, sheet.UndoRedo.Entries.Count());
        Assert.DoesNotContain(filter, model.Components);
        // The block's two pins kept their nets under the labels the ladder's ends carry, so each
        // resistor now shares a net with the ladder and with nothing else it did not before.
        var after = NetExtractor.Extract(model).TestBench.Instances.ToDictionary(i => i.InstanceName);
        Assert.Contains("IN", after["R1"].NetBindings);
        Assert.Contains("OUT", after["R2"].NetBindings);
        var ladderParts = after.Values.Where(i => i.InstanceName is not ("R1" or "R2")).ToList();
        Assert.Contains(ladderParts, i => i.NetBindings.Contains("IN"));
        Assert.Contains(ladderParts, i => i.NetBindings.Contains("OUT"));
        Assert.Equal(beforeNets["R1"].NetBindings.Count, after["R1"].NetBindings.Count);

        sheet.UndoRedo.Undo();
        Assert.Equal(before, SchematicPersistence.Serialize(model, "s"));
    }

    // ── §2: Synthesise… ─────────────────────────────────────────────────────

    [Fact]
    public void SynthesizeIsOfferedOnAFilterOnly()
    {
        var vm = EmptySheet(out var model);
        var filter = NewFilter();
        var r = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        model.Components.Add(filter);
        model.Components.Add(r);
        Assert.Same(filter, vm.SynthesizeTarget(filter.Id));
        Assert.Null(vm.SynthesizeTarget(r.Id));
        Assert.Null(vm.SynthesizeTarget(null));
    }

    [Fact]
    public void TheContextMenuAndThePropertiesPanelOpenTheSameWindow()
    {
        string view = File.ReadAllText(Source("src", "Ui", "Views", "Content", "SchematicView.axaml.cs"));
        int handler = view.IndexOf("private void OnCtxSynthesizeFilter(", StringComparison.Ordinal);
        Assert.True(handler >= 0);
        int end = view.IndexOf("    private ", handler + 1, StringComparison.Ordinal);
        Assert.Contains("LcFilterDesignerWindow.Show(", view[handler..end]);
        string panel = File.ReadAllText(Source("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml.cs"));
        Assert.Contains("LcFilterDesignerWindow.Show(", panel);
        Assert.Contains("Header=\"Synthesise…\"", File.ReadAllText(Source("src", "Ui", "Views", "Content", "SchematicView.axaml")));
        Assert.Contains("Content=\"Synthesise…\"", File.ReadAllText(Source("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml")));
    }

    [Fact]
    public void ASecondSynthesizeOnTheSameFilterBringsTheFirstWindowForward()
    {
        var windows = new LcFilterDesignerWindows<object>();
        var filter = NewFilter();
        int created = 0, activated = 0;
        var (first, made1) = windows.Show(filter, () => { created++; return new object(); }, _ => activated++);
        var (second, made2) = windows.Show(filter, () => { created++; return new object(); }, _ => activated++);
        Assert.True(made1);
        Assert.False(made2);
        Assert.Same(first, second);
        Assert.Equal((1, 1), (created, activated));

        windows.Closed(filter, first);
        Assert.False(windows.IsOpen(filter));
        var (_, made3) = windows.Show(filter, () => new object(), _ => { });
        Assert.True(made3);
        // A different Filter has its own window.
        var (_, other) = windows.Show(NewFilter("FLT2"), () => new object(), _ => { });
        Assert.True(other);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }
}
