// railRF's window chrome (owner, 2026-09-19): the menu bar, the title, the aggressor row and the
// readouts toggle.
//
// RailPaneToggleTests' own shape, and for its reason: the MEANING lives on the view model, which
// needs no application host, and the part that is genuinely chrome — which controls exist, on which
// surfaces, bound to what — is a scan of the AXAML, because that is where those decisions live.

using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailWindowChromeTests
{
    // ── The title (owner: "add .crail to the text title and the window title"; and then:
    //    "remove the railRF — text in the upper left corner, keep it in the window title") ──

    /// <summary>
    /// The window's title names the application and the FILE; the text drawn inside the window
    /// names the file alone. Both carry the extension and both carry the dirty bullet.
    /// </summary>
    /// <remarks>
    /// <b>Two properties because they are two different answers.</b> In the OS title bar a window is
    /// one row among every window on the machine, so it says which application it belongs to; inside
    /// the window that word is one the reader has already read, over content that could not be
    /// anything else. The extension is on both, because what is named is a file.
    /// </remarks>
    [Fact]
    public void TheWindowTitleNamesTheApplication_TheInWindowLabelNamesOnlyTheFile_BothWithTheExtension()
    {
        var vm = new RailRfViewModel(new RailDocument { Name = "evk_1v8" }, "/boards/evk/evk_1v8.crail");

        Assert.Equal("railRF — evk_1v8.crail", vm.Title);
        Assert.Equal("evk_1v8.crail", vm.DocumentLabel);

        // The extension is APPENDED to the document's own name, never doubled onto one that has it.
        var named = new RailRfViewModel(new RailDocument { Name = "evk_1v8.crail" }, null);
        Assert.Equal("evk_1v8.crail", named.DocumentLabel);

        // An edit marks both, on the one spelling of "unsaved" the whole application uses.
        var dirty = new RailRfViewModel(new RailDocument { Name = "evk_1v8" }, null);
        dirty.Document.Name = "evk_3v3";
        dirty.ToggleResultTextCommand.Execute(null);        // any edit; this one refreshes the mark
        Assert.StartsWith("• ", dirty.Title);
        Assert.StartsWith("• ", dirty.DocumentLabel);

        // A window with nothing in it says so rather than drawing an empty strip.
        Assert.Equal("untitled", new RailRfViewModel().DocumentLabel);
    }

    /// <summary>
    /// The title's tooltip is the full path — the half a file name cannot say — and a window that
    /// has never been saved answers rather than showing an empty tip.
    /// </summary>
    [Fact]
    public void TheTitleTipIsTheFullPath_AndSaysSoWhenThereIsNotOneYet()
    {
        Assert.Equal("/boards/evk/evk_1v8.crail",
                     new RailRfViewModel(new RailDocument(), "/boards/evk/evk_1v8.crail").DocumentPathTip);

        Assert.Contains("Not saved yet", new RailRfViewModel().DocumentPathTip);
    }

    /// <summary>
    /// The title is a right-clickable target with a Reveal item on it, built once on Opening, and
    /// the label spelled by <c>FileReveal</c> rather than by a fourth copy of the platform test.
    /// </summary>
    [Fact]
    public void TheTitleRevealsTheFile_ThroughTheOneSharedPlatformSpelling()
    {
        string xaml = Xaml();
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.Menu.cs"));

        Assert.Contains("Name=\"TitleText\"", xaml);
        Assert.Contains("Text=\"{Binding DocumentLabel}\"", xaml);
        Assert.Contains("ToolTip.Tip=\"{Binding DocumentPathTip}\"", xaml);
        Assert.Contains("Opening=\"OnTitleContextMenuOpening\"", xaml);

        Assert.Contains("FileReveal.Label", code);
        Assert.Contains("FileReveal.Reveal(path)", code);
    }

    // ── The menu bar ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// View and Window exist on BOTH menu surfaces, and every View item runs a command the toolbar
    /// already runs rather than a second spelling of the same state.
    /// </summary>
    /// <remarks>
    /// <b>Both surfaces or neither.</b> The macOS <c>NativeMenu</c> and the in-window <c>Menu</c> are
    /// hand-mirrored — Avalonia offers nothing that keeps them in step — so a menu added to one is a
    /// menu that exists on one platform only, which is a report nobody makes until they are on that
    /// platform. This is the same assertion harmonicaRF and wBond's own menu tests make.
    /// </remarks>
    [Fact]
    public void TheViewAndWindowMenusAreOnBothSurfaces_AndViewBindsTheCommandsTheToolbarAlreadyRuns()
    {
        string xaml = Xaml();

        Assert.True(Index(xaml, "<NativeMenu.Menu>") < Index(xaml, "Name=\"RailMenuBar\""),
                    "The native menu is declared after the in-window one.");

        string nativeBlock   = NativeMenuBlock(xaml);
        string inWindowBlock = InWindowMenuBlock(xaml);

        foreach (string surface in new[] { nativeBlock, inWindowBlock })
        {
            Assert.Contains("Zoom to ", surface);
            Assert.Contains("{Binding ZoomToFitCommand}", surface);
            Assert.Contains("{Binding ToggleSpecificationCommand}", surface);
            Assert.Contains("{Binding ToggleBoardCommand}", surface);
            Assert.Contains("{Binding TogglePartsCommand}", surface);
            Assert.Contains("{Binding ToggleResultsCommand}", surface);
            Assert.Contains("{Binding ToggleResultTextCommand}", surface);

            // A CHECKBOX against the property its toolbar lamp lights from — so the menu and the
            // lamp cannot disagree about what is showing.
            Assert.Contains("IsChecked=\"{Binding ShowSpecification, Mode=OneWay}\"", surface);
            Assert.Contains("IsChecked=\"{Binding ShowResultText, Mode=OneWay}\"", surface);
        }

        // The in-window Menu is hidden on macOS, where the native bar above already carries it —
        // and it is that hiding which makes NeedsUpdate the only refresh hook there.
        Assert.Contains("IsVisible=\"{OnPlatform True, macOS=False}\"", inWindowBlock);

        // The Window menu: declared on both, rebuilt just before either opens, and SEEDED at
        // construction — an empty ItemsSource makes the parent a leaf, so SubmenuOpened would never
        // fire and the menu would latch itself dead.
        Assert.Contains("<NativeMenuItem Header=\"Window\">", nativeBlock);
        Assert.Contains("Name=\"WindowMenuItem\"", inWindowBlock);
        Assert.Contains("SubmenuOpened=\"OnWindowMenuOpened\"", inWindowBlock);

        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml.cs"));
        Assert.Contains("RebuildWindowMenu();", code);
        Assert.Contains("EnsureWindowNativeItem()", code);
    }

    /// <summary>
    /// File is on both surfaces too, and every item on it is a second ROUTE to a toolbar button —
    /// never a second implementation.
    /// </summary>
    /// <remarks>
    /// <b>The hooks are the whole of the claim.</b> Each File command calls a hook the window
    /// installs, and each hook calls the method the matching button's <c>Click</c> already calls
    /// (<c>OpenDocumentAsync</c>, <c>ImportBoardAsync</c>, <c>ExportAsync</c>, <c>CompareAsync</c>).
    /// A menu item that opened a picker of its own would be a second Open and a second Export,
    /// drifting from the buttons beside them the first time either was touched — silently, because
    /// both would keep producing a plausible dialog.
    ///
    /// <para><b>No <c>Gesture</c> on the native items.</b> A <c>NativeMenuItem</c>'s Gesture is
    /// FUNCTIONAL on macOS, and <c>Window.KeyBindings</c> already binds ⌘O / ⌘S / ⇧⌘S / ⌘W there;
    /// spelling them on the menu as well would arm one keystroke twice. The in-window
    /// <c>InputGesture</c> is display-only and cannot.</para>
    /// </remarks>
    [Fact]
    public void TheFileMenuIsOnBothSurfaces_RoutesToTheToolbarsOwnMethods_AndArmsNoKeystrokeTwice()
    {
        string xaml = Xaml();

        string nativeBlock   = NativeMenuBlock(xaml);
        string inWindowBlock = InWindowMenuBlock(xaml);

        foreach (string surface in new[] { nativeBlock, inWindowBlock })
            foreach (string command in new[]
            {
                "OpenDocumentCommand", "SaveCommand", "SaveAsCommand", "ImportBoardCommand",
                "ReportCommand", "ExportCommand", "CompareCommand", "CloseCommand",
            })
                Assert.Contains($"{{Binding {command}}}", surface);

        // ONE KEYSTROKE, ONE HANDLER. The four shortcuts are Window.KeyBindings; the native items
        // carry no Gesture at all, because there it would be a second live binding.
        foreach (string gesture in new[] { "Ctrl+O", "Meta+O", "Ctrl+W", "Meta+W" })
            Assert.Contains($"<KeyBinding Gesture=\"{gesture}\"", xaml);
        Assert.DoesNotContain("Gesture=\"Meta+", nativeBlock);

        // Each hook calls the method the toolbar button already calls — one implementation of Open,
        // of Import, of Export and of Compare, not two.
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.Menu.cs"));
        Assert.Contains("vm.OpenDocumentHook = () => _ = OpenDocumentAsync();", code);
        Assert.Contains("vm.ImportBoardHook  = () => _ = ImportBoardAsync();", code);
        Assert.Contains("vm.ExportHook       = ext => _ = ExportAsync(ext);", code);
        Assert.Contains("vm.CompareHook      = () => _ = CompareAsync();", code);

        // Close goes through OnClosing, so it cannot be a route around the unsaved-work prompt.
        Assert.Contains("vm.CloseHook = Close;", code);
    }

    /// <summary>
    /// The three File items that need a solved result dim themselves, and they are re-asked from the
    /// one place <c>CanExport</c> changes.
    /// </summary>
    /// <remarks>
    /// <b>A menu item needs this and a toolbar button does not.</b> The buttons carry
    /// <c>IsEnabled="{Binding CanExport}"</c> and re-read themselves from the property notification;
    /// a menu item is dimmed by its Command's own <c>CanExecute</c>, and a <c>RelayCommand</c> re-asks
    /// only when it is told to — so a Report item enabled at construction would stay enabled for ever.
    /// </remarks>
    [Fact]
    public void ReportExportAndCompareAreRefusedUntilSomethingHasBeenSolved()
    {
        var vm = new RailRfViewModel();
        Assert.False(vm.CanExport);

        Assert.False(vm.ReportCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CompareCommand.CanExecute(null));

        // Open, Import, Save and Close never are: they are about the DOCUMENT, not about an answer.
        Assert.True(vm.OpenDocumentCommand.CanExecute(null));
        Assert.True(vm.ImportBoardCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.CloseCommand.CanExecute(null));

        // And the re-ask is wired to the one place CanExport is announced.
        string solve = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/RailRf/RailRfViewModel.Solve.cs"));
        Assert.Contains("RefreshExportCommands();", solve);
    }

    /// <summary>
    /// railRF is in circuitRF's OWN Window menu, and it got there by implementing the interface that
    /// menu enumerates rather than by being named in it.
    /// </summary>
    /// <remarks>
    /// <b>This is the other half of "find the way back".</b> A railRF window is shown UNOWNED so it
    /// can go behind the workspace; the workspace therefore owes it a row in its own list, and the
    /// list is built from <see cref="ICrfMenuWindow"/> precisely so a new window is not silently
    /// missing from it — which railRF was.
    /// </remarks>
    [Fact]
    public void RailRfIsAStandaloneWindowInCircuitRfsOwnWindowMenu()
    {
        Assert.True(typeof(ICrfMenuWindow).IsAssignableFrom(typeof(Views.RailRf.RailRfWindow)),
                    "RailRfWindow does not implement ICrfMenuWindow, so circuitRF's Window menu cannot list it.");

        // And its own Window menu resolves the SAME enumeration the workspace's does, rather than a
        // second ordering that disagrees with it the first time either changes.
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/CrfWindowMenu.cs"));
        Assert.Contains("EnumerateWindowEntries()", code);

        // Never an empty list: an empty ItemsSource is the self-latching dead menu above.
        Assert.NotEmpty(CircuitRF.Ui.CrfWindowMenu.BuildItems([]));
    }

    // ── Dark mode: the plot is the Data Display's, so it wears the Data Display's palette ──────

    /// <summary>
    /// The results plot follows the window's light/dark variant, and BOTH halves are set — the
    /// control's own theme and the host's.
    /// </summary>
    /// <remarks>
    /// <b>Two properties, two different sets of pixels</b> (owner, 2026-09-19: in dark mode the axes,
    /// the grid, the labels, the markers and the marker info boxes were drawn in a colour that could
    /// not be seen). <c>PlotControl.PlotTheme</c> is what the control draws the axes, grid, labels
    /// and markers with; <c>DataDisplayViewModel.Theme</c> is what repaints the marker info boxes.
    /// Setting one leaves the other in the light palette, so the test names both — and names
    /// <c>RenderTheme</c>, which is the Data Display's own palette and the whole point of the
    /// request.
    /// </remarks>
    [Fact]
    public void TheResultsPlotAndItsMarkerBoxesFollowTheThemeVariant_OnTheDataDisplaysOwnPalette()
    {
        string menu  = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.Menu.cs"));
        string shell = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml.cs"));

        Assert.Contains("RenderTheme.Dark", menu);
        Assert.Contains("RenderTheme.Light", menu);
        Assert.Contains("vm.PlotHost.Theme", menu);                       // the info boxes
        Assert.Contains("PlotControl.PlotThemeProperty", menu);           // the axes, grid, labels

        // Applied when the variant changes AND once when the view model arrives — a window opened
        // in dark mode never raises the change event.
        Assert.Contains("ActualThemeVariantChanged += (_, _) => SyncPlotTheme();", shell);
        Assert.Contains("SyncPlotTheme();", shell);
    }

    // ── The aggressor row (owner: "I can add an Aggressor, but I can't change its name or
    //    frequency … the inline text editor doesn't appear when I double click on those") ──

    /// <summary>
    /// Every value on an aggressor row is settable, and a value the parser refuses snaps back
    /// rather than staying on screen looking accepted.
    /// </summary>
    /// <remarks>
    /// <b>The row had exactly one control on it and it was a <c>TextBlock</c> bound to
    /// <c>Summary</c></b> — so "+" made a row nobody could name or tune. The view model already had
    /// all three values; nothing on screen reached any of them, and a double-click on a TextBlock
    /// opens no editor. The refusal half matters as much: <c>Commit</c> notifies whether or not
    /// anything changed, which is what puts an <c>InlineEditText</c> back to what is stored.
    /// </remarks>
    [Fact]
    public void AnAggressorRowsName_Frequency_AndHarmonics_AreAllSettable_AndARefusedValueSnapsBack()
    {
        var rail = new RailSpec { Name = "1V8" };
        rail.Aggressors.Add(new RailAggressor("new", 1e6, 1));   // what "+" adds
        var row = new RailAggressorRowViewModel(rail, rail.Aggressors[0], 0);

        row.Name = "  converter  ";
        row.FrequencyEntry = "2.2 MHz";
        row.HarmonicsEntry = "5";

        Assert.Equal("converter", rail.Aggressors[0].Name);
        Assert.Equal(2.2e6, rail.Aggressors[0].FrequencyHz, 3);
        Assert.Equal(5, rail.Aggressors[0].Harmonics);

        // Refused: neither reaches the document, and the getter still reads what is stored — which
        // is what the control re-reads when the row notifies.
        row.FrequencyEntry = "not a frequency";
        row.HarmonicsEntry = "0";
        Assert.Equal(2.2e6, rail.Aggressors[0].FrequencyHz, 3);
        Assert.Equal(5, rail.Aggressors[0].Harmonics);
        Assert.Equal("5", row.HarmonicsEntry);

        // And the row on screen is three InlineEditTexts, not one TextBlock.
        string xaml = Xaml();
        string block = xaml[Index(xaml, "x:DataType=\"rvm:RailAggressorRowViewModel\"")..];
        block = block[..block.IndexOf("</DataTemplate>", StringComparison.Ordinal)];

        Assert.Contains("Text=\"{Binding Name, Mode=TwoWay}\"", block);
        Assert.Contains("Text=\"{Binding FrequencyEntry, Mode=TwoWay}\"", block);
        Assert.Contains("Text=\"{Binding HarmonicsEntry, Mode=TwoWay}\"", block);
        Assert.DoesNotContain("{Binding Summary}", block);
    }

    // ── The readouts toggle (owner: "a way to maximize the plot size … turn the text off") ──────

    /// <summary>
    /// The readouts toggle is document state, it round-trips, absent means shown — and it is
    /// deliberately OUTSIDE the "at least one panel is showing" rule.
    /// </summary>
    /// <remarks>
    /// <b>It divides the results COLUMN, not the window.</b> Turning it off leaves that column
    /// showing its plot, so it can never reach the state the four panel toggles are gated against;
    /// gating it anyway would refuse the one press the request is about — the results panel alone,
    /// given over entirely to the curve.
    /// </remarks>
    [Fact]
    public void TheReadoutsToggleRoundTrips_AbsentMeansShown_AndItIsNotGatedByTheLastPanelRule()
    {
        var doc = new RailDocument { Name = "board" };
        Assert.True(doc.Panels.ShowResultText);

        // Nothing hidden: the file still does not mention panels at all, so an untouched document is
        // the bytes it was.
        Assert.DoesNotContain("\"Panels\"", RailDocumentIo.Serialize(doc));

        // A `.crail` from before this existed opens with the readouts showing.
        Assert.True(RailDocumentIo.Deserialize("""{ "FormatVersion": 1, "Name": "board" }""")
                                  .Panels.ShowResultText);

        var vm = new RailRfViewModel(doc, null);

        // The last-panel rule applies to the four panels and not to this: with results the only
        // panel left, its own toggle is refused and the readouts toggle still works.
        vm.ToggleSpecificationCommand.Execute(null);
        vm.ToggleBoardCommand.Execute(null);
        vm.TogglePartsCommand.Execute(null);
        Assert.False(vm.ToggleResultsCommand.CanExecute(null));

        Assert.True(vm.ToggleResultTextCommand.CanExecute(null));
        vm.ToggleResultTextCommand.Execute(null);
        Assert.False(vm.ShowResultText);

        string json = RailDocumentIo.Serialize(doc);
        Assert.Contains("\"ShowResultText\": false", json);
        Assert.False(new RailRfViewModel(RailDocumentIo.Deserialize(json), null).ShowResultText);
    }

    /// <summary>
    /// The button is in the results panel's own strip, lit by the property it toggles — and the
    /// cards it hides really do go, which is what frees the height the plot then takes.
    /// </summary>
    /// <remarks>
    /// The ceiling is a code-behind number rather than a binding (an <c>AspectRatioPanel</c> in an
    /// <c>Auto</c> row is offered an infinite height, and the cap is the only thing holding it), so
    /// the toggle has to reach <c>CapResultsPlot</c> explicitly — a hidden card list with the old
    /// cap still in force would free the space and leave the plot exactly as it was.
    /// </remarks>
    [Fact]
    public void TheReadoutsButtonIsInTheResultsStrip_HidesTheCards_AndLiftsThePlotsCeiling()
    {
        string xaml = Xaml();
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml.cs"));

        string block = xaml[Index(xaml, "Name=\"ResultTextButton\"")..];
        block = block[..block.IndexOf("</Button>", StringComparison.Ordinal)];
        Assert.Contains("Classes.ToolActive=\"{Binding ShowResultText}\"", block);
        Assert.Contains("Command=\"{Binding ToggleResultTextCommand}\"", block);

        // It is in the results strip, with the two answer tabs — not among the four panel lamps.
        Assert.True(Index(xaml, "Name=\"ResultsTabStrip\"") < Index(xaml, "Name=\"ResultTextButton\""));

        // The cards are gated on it, and the ceiling is re-computed when it changes.
        Assert.Contains("Grid.Row=\"2\" IsVisible=\"{Binding ShowResultText}\"", xaml);
        Assert.Contains("ResultsPlotHeightShareTextHidden", code);
        Assert.Contains("nameof(RailRfViewModel.ShowResultText))", code);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The macOS surface ALONE. Sliced to its own closing tag rather than to the start of
    /// the in-window menu, because <c>Window.KeyBindings</c> sits between the two — and this block is
    /// what the "no functional Gesture here" assertion is about.</summary>
    private static string NativeMenuBlock(string xaml) =>
        xaml[Index(xaml, "<NativeMenu.Menu>")..Index(xaml, "</NativeMenu.Menu>")];

    private static string InWindowMenuBlock(string xaml) =>
        xaml[Index(xaml, "Name=\"RailMenuBar\"")..Index(xaml, "</Menu>")];

    // ── The specification column's two owner rules (2026-09-20) ──────────────────────────────

    /// <summary>
    /// <b>The two rail combo boxes share one grid, and the two actions beside them are square
    /// glyphs.</b>
    /// </summary>
    /// <remarks>
    /// One test because it is one instruction about one card. Both halves are the shape nothing
    /// fails on: two combos at different widths still work, and a wide button carrying a sentence
    /// still presses — so the only thing that would ever notice either being undone is somebody
    /// looking at the window, which is what this scan stands in for.
    ///
    /// <para><b>The shared COLUMNS are the assertion, not two matching widths.</b> Sizing the Ref
    /// combo to whatever the Rail combo happens to be is a pair of numbers to keep in step; putting
    /// them in the same two columns of the same grid is why they cannot come apart.</para>
    /// </remarks>
    [Fact]
    public void TheRailAndReferenceCombosShareOneGrid_AndTheirActionsAreSquareGlyphButtons()
    {
        string xaml = Xaml();

        // ONE grid, four columns, three rows — the rail, the reference layer and the return net
        // (brief-railrf-31) — and every combo inside it.
        string grid = Block(xaml, "ColumnDefinitions=\"Auto,*,Auto,Auto\" RowDefinitions=\"Auto,Auto,Auto\"",
                            "</Grid>");
        Assert.Contains("Name=\"RailSelector\"", grid, StringComparison.Ordinal);
        Assert.Contains("Name=\"ReferenceSelector\"", grid, StringComparison.Ordinal);
        Assert.Contains("Name=\"ReturnNetSelector\"", grid, StringComparison.Ordinal);

        // …all sized by the grid's one scoped style, and no label carrying a Margin of its own: a
        // local Margin replaces rowlbl's right-hand gap, and a local size lets one combo drift
        // (the flagged border made the Ref combo a pixel taller than the other two).
        Assert.Contains("RowSpacing=\"3\"", grid, StringComparison.Ordinal);
        string comboStyle = Regex.Replace(Block(grid, "Selector=\"ComboBox\"", "</Style>"), @"\s+", " ");
        Assert.Contains("Property=\"Height\" Value=\"24\"", comboStyle, StringComparison.Ordinal);
        foreach (System.Text.RegularExpressions.Match combo in Regex.Matches(grid, @"<ComboBox\b[^>]*>"))
            Assert.DoesNotMatch(@"\b(Height|MinHeight|Margin|FontSize|Padding)=", combo.Value);
        foreach (System.Text.RegularExpressions.Match label in Regex.Matches(grid, @"<TextBlock\b[^>]*rowlbl[^>]*>"))
            Assert.DoesNotContain("Margin=", label.Value, StringComparison.Ordinal);

        // The square style exists and is square by explicit metrics, not by arithmetic on padding.
        // Whitespace-insensitive: the alignment of these setters is formatting, not the rule.
        string style = Regex.Replace(Block(xaml, "Selector=\"Button.sqbtn\"", "</Style>"), @"\s+", " ");
        Assert.Contains("Property=\"Width\" Value=\"24\"", style, StringComparison.Ordinal);
        Assert.Contains("Property=\"Height\" Value=\"24\"", style, StringComparison.Ordinal);

        // …and the three buttons of this card wear it, each holding a glyph rather than a label.
        foreach (string name in new[] { "PickFromBoardButton", "PickSelectedNetButton", "RemoveRailButton" })
        {
            string button = Block(xaml, $"Name=\"{name}\"", "</Button>");

            Assert.Contains("Classes=\"sqbtn\"", button, StringComparison.Ordinal);
            Assert.Contains("MaterialIcon", button, StringComparison.Ordinal);
            Assert.DoesNotContain("Content=", button, StringComparison.Ordinal);
        }
    }

    // ── The parts pane's four actions (owner 2026-09-21; add/remove 2026-09-22) ──────────────

    /// <summary>
    /// <b>All four are square glyph buttons whose tooltips are placed off the pointer, the row
    /// itself is live whenever a rail is, and only the three that act on rows are gated.</b>
    /// </summary>
    /// <remarks>
    /// <b>The offset is the half that gets dropped, and without it the placement does nothing.</b>
    /// <c>ToolTip.VerticalOffsetProperty</c> is registered with a default of 20.0 and the positioner
    /// adds it unconditionally, so <c>Placement="Top"</c> alone pushes the popup straight back down
    /// over the button, under the pointer — which reopens the loop that IS the flash. It has been
    /// reported three times now and a Placement written without its offset looks correct in every
    /// review, so the pairing is asserted over the WHOLE window rather than on these two buttons.
    ///
    /// <para><b>And Create part library is deliberately NOT gated on a SELECTION</b> (owner,
    /// 2026-09-21). It seeds the <c>.crlib</c> from every part number the document names, so a row
    /// is not its operand; asserting that absence is what stops the gate being copied onto it by
    /// symmetry. It IS hidden with the rest of the row when the table is empty, because there is
    /// then nothing to seed from.</para>
    ///
    /// <para><b>The row's own visibility is the field report's half</b> (2026-09-22). It was gated
    /// on <c>HasParts</c>, which hid every gesture at exactly the moment the pane is empty — and an
    /// empty pane, after a designer had just placed two footprints, is the whole of what was
    /// reported. <c>CanAddPart</c> is the gate now, and Add is the one button with no further one:
    /// a table you cannot put the first row into is the defect.</para>
    /// </remarks>
    [Fact]
    public void ThePartsPaneActionsAreSquareGlyphs_PlaceTheirTooltipsOffThePointer_AndOnlyAssignIsGated()
    {
        string xaml = Xaml();

        // The buttons of the parts pane's own action row, split out of the panel that holds them —
        // anchoring on each Click handler would start the block PAST the attributes above it.
        string row = Block(xaml, "Grid.Row=\"5\" Orientation=\"Horizontal\"", "</StackPanel>");
        string[] buttons = row.Split("<Button", StringSplitOptions.None);
        Assert.Equal(5, buttons.Length);

        string add     = buttons[1];
        string remove  = buttons[2];
        string assign  = buttons[3];
        string library = buttons[4];
        Assert.Contains("Click=\"OnAddPartClick\"",           add,     StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRemovePartClick\"",        remove,  StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAssignPartNumberClick\"",  assign,  StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCreatePartLibraryClick\"", library, StringComparison.Ordinal);

        foreach (string button in new[] { add, remove, assign, library })
        {
            Assert.Contains("Classes=\"sqbtn\"", button, StringComparison.Ordinal);
            Assert.Contains("MaterialIcon", button, StringComparison.Ordinal);
            Assert.DoesNotContain("Content=", button, StringComparison.Ordinal);
        }

        // THE ROW IS LIVE WHENEVER A RAIL IS, not whenever the table has rows — the field report's
        // half. Gating it on HasParts hid every gesture at exactly the moment the pane is empty.
        Assert.Contains("IsVisible=\"{Binding CanAddPart}\"", row, StringComparison.Ordinal);

        // ADD IS THE ONE WITH NO FURTHER GATE. A table you cannot put the first row into is the
        // defect; every other button here operates on something that has to exist first.
        Assert.DoesNotContain("IsEnabled=", add,     StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=", add,     StringComparison.Ordinal);

        // Remove and Assign both write onto the SELECTION, so both are dead without one.
        foreach (string button in new[] { remove, assign })
            Assert.Contains(
                "IsEnabled=\"{Binding SelectedPart, Converter={x:Static ObjectConverters.IsNotNull}}\"",
                button, StringComparison.Ordinal);

        // The library's operand is the DOCUMENT, so it takes no selection gate — but it is hidden
        // with the empty table, because there are then no part numbers to seed a .crlib from.
        Assert.DoesNotContain("IsEnabled=", library, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasParts}\"", library, StringComparison.Ordinal);

        // Every Placement="Top" in this window carries its offset on the same line — see the remarks.
        foreach (string line in xaml.Split('\n'))
        {
            if (!line.Contains("ToolTip.Placement=\"Top\"", StringComparison.Ordinal)) continue;
            Assert.Contains("ToolTip.VerticalOffset=", line, StringComparison.Ordinal);
        }
    }

    /// <summary>The AXAML from <paramref name="from"/> up to the next <paramref name="until"/>.</summary>
    private static string Block(string xaml, string from, string until)
    {
        int at = Index(xaml, from);
        int end = xaml.IndexOf(until, at, StringComparison.Ordinal);
        Assert.True(end > at, $"{from} is never closed by {until}.");
        return xaml[at..end];
    }

    private static string Xaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml"));

    private static int Index(string xaml, string needle)
    {
        int i = xaml.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{needle} is not in the window's AXAML.");
        return i;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
