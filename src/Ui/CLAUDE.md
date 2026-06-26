# UI (Avalonia) — local conventions

HB spectrum stage 2 — harmonic axis carries orders; Trace reconstructs frequency (brief-hb-spectrum-2-order-axis, 2026-06-23) — COMPLETE: After the engine change (stage 2, Part A), the single-tone `harmonic` axis stores integer orders (unit `""`), never Hz values. The owner (`PlotInspectorViewModel`) resolves the per-X fundamental from `ToneFreqs` via `GetToneFreqsCube` + `ResolveFundamentalByX` and injects it via `Trace.SetSpectrumFundamentals(f0ByX)` immediately before `SetCubeData`/`SetFamilyData`. The Trace reconstructs harmonic frequency for: geometry (`BuildCubePath` + `SetFamilyData` X positions = `order × f0 × freqScale`), marker readouts (`BuildCubeMarkerBoxLines` emits `harmonic={order}` + optional `freq=… GHz`), stem info (`GetStemFreqString` uses `_f0ByX`), and the X-axis label (`Plot.XLabel` treats a harmonic-named axis as frequency). `HarmonicOrderOf` removed (order is now the axis value directly). The harmonic axis is matched by **name**, not by being a frequency unit. 5 UI gate tests (`HbSpectrumStage2Tests.cs`). T3/T5.B in `MarkerSweepFreqLabelTests.cs` updated to use integer orders + `SetSpectrumFundamentals`. **Follow-ups:** two-tone `mixIndex` → same pattern; physical-frequency column in Table.

Marker readout: freq-var sweep axis uses its own name (brief-marker-sweep-freq-label, 2026-06-23) — COMPLETE: The `freq=…`/`harmonic=…` display in `Trace.BuildCubeMarkerBoxLines` is now specific to the **HB harmonic axis** (matched by `HarmonicAxisName == "harmonic"`). Any other frequency-unit axis — notably a parametric sweep over a frequency variable like `RFfreq` — is labelled with its own axis/variable name and shows no `harmonic=` row. 5 new tests in `MarkerSweepFreqLabelTests.cs`. Build 0W/0E; 1403 Ui.Tests pass.

Marker + X-axis per-swept-variable units (brief-sweep-axis-marker-units Part C, 2026-06-22) — COMPLETE: Marker info boxes and X-axis labels now show units for all sweep-axis types. The family `else` branch in `Trace.BuildCubeMarkerBoxLines` appends `FamilyAxisUnit` (e.g. `Vgs=1 V`). `PlotInspectorViewModel` already passes `fAxis.Unit` to `SetFamilyData` — the fix is upstream: `ParametricSweepEngine` now tags the axis with `Units.BaseUnit(origVar.Unit)`, which `fAxis.Unit` picks up. The frequency family branch (→ `freq=2 GHz`) and the X-axis non-frequency branch (→ `{name}={val} {unit}`) were already correct. Gate tests: `SweepAxisMarkerUnitTests.cs` (T1 freq-X, T2 non-freq-X, T3 family). Build 0W/0E; 1398 Ui.Tests pass.

Parametric-sweep range units — UI layer (brief-sweep-range-units, 2026-06-22) — COMPLETE: `SweepAxisRowViewModel` now applies a unit multiplier so sweeping a GHz frequency VAR over `1 … 5` (unit inherited or explicit) materializes `[1e9 … 5e9]`. `EffectiveUnit` = `Unit` if user set one; else the swept VAR's declared unit (`GetVarUnit` scans `model.Components` for `SymbolKind.Var` parameters). `BuildValues` and `BuildSpec` multiply Start/Stop by `Units.Scale(EffectiveUnit) ?? 1.0`; Step is scaled only in StepSize mode; PointCount count is not scaled. `BuildSpec` stores **coefficients** (unscaled) + `EffectiveUnit` on `SweepSpec` — Part A re-applies the scale at PSA construction. `FromPsa` restores `Unit = spec.Unit`. Note: var-unit-wins does NOT apply here — the chosen field/inherited unit always governs (unlike the freq-preview helper). `AnalysisSerialization` adds `PsaUnit` (string?) to `CschAnalysis`; `ToDto` writes it when non-empty; `FromDto` passes `dto.PsaUnit ?? ""` to the `SweepSpec` ctor. Absent `PsaUnit` → base (back-compat). 5 gate tests: 2 in `SweepRowUnitTests.cs` (T6: defaultsUnitFromVar + override; T7: round-trip) + 1 in `AnalysisSerializationTests.cs` (T8: ToDto→FromDto). Build 0W/0E; 2156 total tests pass.

Analysis-editor frequency preview (brief-analysis-freq-preview-units, 2026-06-22) — COMPLETE: Analysis-editor frequency previews now mirror the engine's var-unit-wins rule via `AnalysisPreviewHelper.ComputeFreqPreview(coeff, fieldUnit, model)`. If the coefficient expression references a variable that declares its own frequency unit (found via `LookupParamUnit` scanning `model.Components`), that variable's unit overrides the field-unit dropdown; otherwise the field unit applies. `DesignScope.Build` still resolves the raw numeric value (units stripped — the existing limitation), and `FreqUnit.Multiplier` applies the winning unit. `FreqUnitHelper.ToHzExpr` retired — zero remaining callers (deleted in Part D). Non-frequency parameter previews unchanged (raw numeric, units deferred). 7 gate tests in `FreqExprUnitTests.cs` (Tests 1–7 of the preview brief). Build 0W/0E; 2156 total tests pass.

SDD weighting editor (brief-sdd-weighting-editor, 2026-06-19) — COMPLETE (Option A — minimal): `ParameterRowViewModel.CommitName` now validates SDD equation names inline when `_ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd`. Accepts `I[p]` (p≥1), `I[p,w]` (p≥1, w≥0), `Q[p]` (p≥1), `H[w]` (w≥2); rejects H[0]/H[1] ("built-in"), malformed H[x]/H[] ("integer weight ≥ 2"), and everything else ("Not a valid SDD equation name"). **`TryValidateSddName(string, out string)`** is `internal static` for direct unit testing (no Avalonia runtime needed). **`NameWatermark`** property emits `"I[p,w] · Q[p] · H[w]"` for SDD/FetSdd owners and `""` for all others; bound to the name TextBox via `PlaceholderText` in `ParameterEditorView.axaml`. Regexes duplicated from `ComponentModelFactory` private fields (comment points back). No Core/Engine changes. 26 gate tests in `SddEquationNameValidationTests.cs`. Build 0W/0E; 1953 total tests pass.

Project Tree UX (brief-projecttree-ux, 2026-06-19) — COMPLETE: 9 independent UX items. **Item 1 (recent workspaces):** `ProjectTreeTool.RecentEntry` sealed record; `RecentWorkspaces` ObservableCollection + `HasRecentWorkspaces`; `RefreshRecent()` (called from `SetActions`/`ClearWorkspace`); `OpenRecentCommand` + `ClearRecentCommand`; `GetRecentWorkspaces()` on `ITreeActions`/`WorkspaceViewModel` (skips missing dirs); AXAML shows recent-workspaces panel in place of "No workspace open." with link-button style (`Button.pt-link`, accent foreground, Hand cursor). **Item 2 (Open Recent cascade on workspace-name context menu):** workspace-name TextBlock has its own ContextMenu with Open Workspace / Open Recent submenu / Close Workspace / Reveal items. **Item 3 (workspace-name context menu):** added via `<TextBlock.ContextMenu>` in AXAML (see Item 2). **Item 4 (dynamic menu state):** `<ContextMenu Opening="OnNodeContextMenuOpening">` calls `vm.RefreshDynamicMenuState()` to re-fire INPC for `IsSaveable`/`SaveHeader` so stale CanExecute never shows. **Item 5 (file-info Properties panel):** `FileInfoInspectorViewModel` (Name, SizeText, ModifiedText); `PropertiesTool` gains 5th context `IsFileInfoActive`/`FileInfoVm`; PropertiesView shows a 2-row grid (Size / Modified); `WorkspaceViewModel.OnTreeSelectionChanged` sets file-info context for KnownFile leaf + OtherFile nodes, clears it for other node types. **Item 6 (Duplicate Cell):** `DuplicateCellCommand` (AsyncRelayCommand, CanExecute=IsCell) in node VM; `DuplicateCellAsync` in `WorkspaceViewModel` — prompts name, validates, copies folder, renames primary schematic+symbol to `<newName>.csch/.csym` (skips if target name collides with non-primary), updates `.ccell`, refreshes tree. **Item 7 (Rename Cell):** `RenameCellCommand`; `RenameCellAsync` — `RenameCellDialog` (name TextBox + "Rename primary files to match" checkbox, default checked), validates (NameValidator + no-other-cell collision + no target-file-collision), force-closes open docs, moves directory, calls `CellUsageScanner.RewriteCellReferences`, renames primaries when checkbox is on, updates `.ccell`, refreshes tree. `CellUsageScanner.RewriteCellReferences` (new) parses JSON with `JsonNode`, matches last path segment of `"CellRef"` (PascalCase), rewrites and re-writes file. **Item 8 (Open Cell command):** "Open Cell" context menu item between "Open Schematic" and "Open Symbol" opens the primary schematic. **Item 9 (Close Workspace):** `CloseWorkspaceCommand` (CanExecute = `CurrentWorkspacePath is not null`); `ResetToBlankShell()` — force-closes all docs, clears registries, resets layout, re-wires tools; menu entry in File menu + NativeMenu. 14 gate tests: 6 in `ProjectTreeUxTests.cs` (Items 5/6), 4 in `CellUsageScannerTests.cs` (Item 7 rewrite). Build 0W/0E; 1887 total tests pass.

Table improvements (brief-table-improvements, 2026-06-19) — COMPLETE: 6 independent items. **Item 1 (per-group X-column resize):** `Trace.XColumnWidth` (0 = fall back to `plot.ColumnWidth`) added; `TableRenderer.BuildLayout` and `TotalColumnWidth` read `anchor.XColumnWidth > 0 ? anchor.XColumnWidth : plot.ColumnWidth` for each XAxis column; `PlotControl` resize-start, drag-move, and double-tap auto-fit all write `XColumnWidth` on the anchor trace instead of `plot.ColumnWidth`; round-tripped in `TraceConfig.XColumnWidth` + `BuildTraceConfig`/`LoadPlotContainerConfigAsync`. **Item 2 (sort-arrow bleed):** `DrawHeaderRow` clamps `triCx = Math.Min(triCx, colX + colW - triSize*0.5 - CellBorderWidth)` so the arrow centre stays inside the column. **Item 3 (wheel-zoom vs. scroll):** `TableRenderer.CanScroll(plot, canvasSize, zoomLevel)` added; `OnPointerWheel` returns without setting `e.Handled` when `CanScroll` is false, so the event bubbles to the parent for zoom. **Item 4 (family / rank-2 trace):** `FamilyCurve` gains `RawComplex`/`RawReal`; `SetFamilyData` stores the raw arrays; `TableColumn` gains `FamilyCurveIndex` (−1 = normal) and `IsNodeAxis`; `BuildColumns` branches on `trace.IsFamily` to emit one TraceValue column per curve (header: `baseShorthand @ Vgs=…`) and an explicit `trace.IsCubeBound-but-no-CubeXValues` branch for blank/invalid traces (no fall-through to legacy Data branch); `FormatFamilyCellAt` + `Trace.FormatFamilyCell` handle cell formatting; `DrawHeaderRow` uses `col.Header` directly for family columns. **Item 5 (Copy Table Data):** comes for free from Item 4 — `BuildCopyGrid` calls `BuildColumns` + `FormatColumnCell`. **Item 6 (node axis integer):** `TableColumn.IsNodeAxis` set when `axisName == "node"` (OrdinalIgnoreCase); `FormatColumnCell` XAxis branch returns `((long)Math.Round(xVal)).ToString(InvariantCulture)` before the `IsFreqUnit` check. 11 gate tests in `TableImprovementsTests` (Items 1–6 in `TableCubeTraceTests.cs`). Build 0W/0E; 1876 total tests pass.

Schematic housecleaning (brief-schematic-housecleaning, 2026-06-19) — COMPLETE: 6 independent items. **Item 1 (paste Num dedup):** `SchematicPasteCommand.ResolveNums` (new) runs after `ResolveNames`; builds the used-Num set from existing Term/P1Tone in the model, then for each pasted Term/P1Tone whose `Num` collides, assigns the lowest free positive integer (live set updated between batch-pasted components so intra-batch collisions are also prevented). **Item 3 (Save As title):** `SchematicDocument.OnSavedAs(filePath, cellName)` (new) sets `FilePath`, `_baseTitle`, `Id`, calls `UpdateTitle()` — unlike `Materialize` it may be called repeatedly. `WorkspaceViewModel.SaveLooseSchematic` now targets the active materialized doc when no dirty scratch doc exists; `SaveLooseToWorkspace`/`SaveLoosePlainFile` both branch on `IsScratch` to call `Materialize` vs `OnSavedAs` and update `_openDocsByPath` (remove old path, add new). **Item 4 (toolbar glyphs + Pin):** Wire button is now a `<Path Data="M 1,6 L 15,10"/>` (matches Symbol Editor line glyph); Ground and Term buttons use `<ctrl:PaletteGlyphControl>` to render the actual library symbol; new Pin button (`PlacePinBtn`) added after Term with `OnPlacePin` handler that arms `SymbolKind.Pin` placement. **Item 6 (SNP label position):** `LabelBaseYFor`/`LabelRowGeometry` gain optional `double? glyphHalfH` parameter; the SNP branch uses it when provided instead of hardcoded `SnpBodyRect(Standard, Loose)`. Updated at all callsites: `SchematicRenderer.DrawLabels` → `c.GlyphBbMaxY - c.Y`; `SchematicModelBuilder.BuildComponent` → `gMaxY - cy`; `EditableSchematic.ToRenderComponent` → `glyphMaxY - Y`; `SchematicHitTest.TestComponentLabels` → `comp.ComputeGlyphBb().MaxY - comp.Y` (SnP only); `SchematicView.axaml.cs ComputeComponentLabelScreen` → `GlyphHalfH` on `ComponentLabelAnchor`. 17 gate tests in `OhmLowercaseTests.cs` (Core), `P1ToneLintTests.cs` (Core), `SchematicHousecleaningTests.cs` (Ui). Build 0W/0E; 1220 Ui.Tests pass.

Data Display fixes (brief-datadisplay-fixes, 2026-06-19) — COMPLETE: 3 bugs + 3 toolbar enhancements. **Bug 1 (Ctrl+S):** `SaveAllDocuments` now checks for an active `DataDisplayDocument` first and dispatches to `SaveDataDisplayDoc(activeDisplay, window)`, consistent with schematic/symbol Ctrl+S behavior; the data display's `UserControl.KeyBindings` also binds `Ctrl/Meta+S → SaveDataDisplayCommand` for focus-local routing. **Bug 2 (Copy/Paste):** 2a — `PerformCopy` now serializes live plots via `BuildPlotContainerConfig` into a `DataDisplayConfig.Plots` JSON and writes it via the `_setClipboardTextAction` delegate; 2b — `_setClipboardDataAction` replaced by `_setClipboardTextAction : Func<string,Task>` + `SetSetClipboardTextAction`; both text clipboard delegates wired in `DataDisplayView.axaml.cs OnLoaded`; `CheckPasteStateAsync()` called from `OnAttachedToVisualTree`; 2c — `LoadPlotContainerConfigAsync` now returns `PlotContainerViewModel`; `PasteFromConfigAsync` rewritten to call the loader (handles cube-bound traces, resolves SourceRefs, dedupes marker names) instead of duplicating stale network-only logic; 2d — `Ctrl/Meta+C/X/V` added to `DataDisplayView.axaml` KeyBindings; `InvokeClipboardAsync` in `WorkspaceViewModel` routes Cut/Copy/Paste to the active `DataDisplayDocument` via new public `InvokeCutAsync`/`InvokeCopyAsync`/`InvokePasteAsync` methods. **Bug 3 (tab title):** `DisplayWindowViewModel.SaveAllAsync` raises `ConfigPathSaved : Action<string>` after `CaptureBaseline`; `DataDisplayDocument` ctor subscribes to `vm.Window.ConfigPathSaved += OnSavedToPath`; `OnSavedToPath` updates `FilePath`, `_baseTitle`, `Id`, `Title`; dead `Materialize` stub now delegates to `OnSavedToPath`. **Enhancement 4:** `AddPlot()` now uses `PlotType.Rect`; `AddSmithPlotCommand`, `AddPolarPlotCommand`, `AddTablePlotCommand` added. **Enhancement 5:** `LoadRunResultsCommand` toolbar button removed. **Enhancement 6:** Datasource ComboBox moved to first toolbar position with separator; final order: `[Datasource combo] | [Rect][Smith][Polar][Table] | [NewTab] | [Zoom×4] | [Undo][Redo] | [Save][Open] | [Export]`. 11 gate tests in `DataDisplayFixesTests.cs` (T1–T11). Build 0W/0E; 1205 Ui.Tests pass.

Data Exporter (brief-data-exporter, 2026-06-19) — COMPLETE: Modal `DataExporterDialog` (File → Export… + toolbar button on Data Display) exports `results/<schematic>/run.npy` to `.npy`, `.mat`, tab-delimited `.txt`, or Touchstone. **VM** (`DataExporterViewModel`, `src/Ui/DataDisplay/ViewModels/`): enumerates `results/*/run.npy` into `AvailableSchematicNames`; optional `preselectSchematic` selects the active run; mode-sensitive `IncludeRows` (groups for npy/mat/tsv, only groups with an `S` cube for Touchstone); `MeasurementsAvailable`/`IncludeMeasurements` gate the `measurements` group; `SweepSliceRows` and `AllSweepCheck` for Touchstone slice selection; `Z0Ohms`, `Digits`, `DigitFormat`, `MatrixFormat` (uses `RfCore.MatrixFormat`); `CanExport` guards the Export button; `ExportDataSet(path)` dispatches via `DataSetSubset.SelectGroups`→`DataSetExporter.Export`; `ExportTouchstone(baseNoSuffix)` dispatches via `TouchstoneExporter.Export`. `SuggestedFileName` returns `<schematic>.<ext>`. **Dialog** (`DataExporterDialog.axaml/.cs`, `src/Ui/Views/Dialogs/`): format segmented-buttons wired in code-behind (no data-bindings for ToggleButton state); `StorageProvider` file-picking lives entirely in code-behind per UI firewall; `ShowAsync(Window? owner, …)` walks `ApplicationLifetime.Windows` for a fallback owner. **DisplayWindowViewModel**: `SetExportDataAction(Func<Task>)` + `[RelayCommand] ExportData()` action-seam. **DataDisplayView**: `DoExportDataAsync()` infers preselect schematic from `SelectedDataSourceAbs`; export button after the datasource combo. **WorkspaceViewModel**: `[RelayCommand(CanExecute=CanExportData)] ExportData()` + `CanExportData()` gates on `GetResultsRoot() != null`; `ExportDataCommand.NotifyCanExecuteChanged()` called in `OnCurrentWorkspacePathChanged`. **WorkspaceWindow.axaml**: File → Export… NativeMenuItem + in-window MenuItem with `DatabaseExportOutline` icon. 10 gate tests in `DataExporterViewModelTests.cs` (no-root, enumeration, preselect, default mode, Touchstone-no-S, npy/mat/tsv write, snp write, suggested filename). Build 0W/0E.

Current-picker branch filter (brief-unify-i-cube-engine, 2026-06-18) — SUPERSEDED by unified I cube: The old per-branch `I:*` cube filter is gone. The engine now emits a single `I` cube with a labeled `branch` axis; `__ProbeBranches` marks the IProbe subset. `TraceRowViewModel.ShowAllBranchesToggleVisible` always returns `false`; `ShowAll` is driven solely by `ShowAllNodesToggleVisible`. `CurrentPickerBranchFilterTests.cs` deleted (tested superseded behavior). See `src/Engine/HarmonicBalance/CLAUDE.md` §C2.

MEAS component (brief-meas-component, 2026-06-18) — COMPLETE: `SymbolKind.Meas` is an **annotation component** (no ports, no instance emission) whose `name = expression` parameter rows route to `TestBench.Measurements` at the **top testbench level only** — MEAS inside a sub-cell raises a conflict and is ignored. It reuses the VAR multi-line text editor (`VarEditorViewModel` + `VarEditorDialog`); `VarEditorViewModel.SetTarget` now stores `_compKind` (VAR vs MEAS) and exposes `PanelTitle`, `DialogTitle`, `AddRowLabel` to differentiate labels. `GenerateUniqueName` produces `Meas{n}` for MEAS. The glyph is a port-less box with two `=` bars (`BuildMeas()` in `BuiltInSymbols.cs`). Registry: `[SymbolKind.Meas] = new("MEAS","MEAS", …, IsCommon:true)`, `EngineReference` sentinel `"MEAS"`, `DefaultParameters` → `[]`, `TryParseCode("MEAS")` → `SymbolKind.Meas`, `UserParamTemplate` → `Meas{0}`. `NetExtractor.ExtractModel` was extended: skip MEAS in the instance loop, collect MEAS rows as `Measurement` objects (first-definition-wins on dup), return as 4th tuple element; `Extract` calls `tb.Measurements.AddRange(topMeas)` (replaces vestigial `model.Measurements` loop). `EmitCellInstance` destructures the new 4th element and warns on non-empty sub-cell MEAS. Measurements are evaluated post-run by `MeasurementEvaluator` into the run's **`measurements` group** (one grouped `run.npy`); referenced in the Data Display by **bare name** (analysis cubes stay qualified `Analysis.Cube`). `DataSet.MeasurementsGroup = "measurements"` enables bare resolution — see `src/Core/Data/CLAUDE.md`. 5 gate tests in `MeasComponentTests.cs` (no-instance, rows-become-measurements, duplicate-first-kept, inside-cell-ignored, csch-round-trip). Build 0W/0E.

Analyses toolbar run retain (brief-analyses-toolbar-run-retain, 2026-06-17) — COMPLETE: The Analyses panel toolbar is restructured into two rows: Row 0 = Run ▶ button + schematic name; Row 1 = button toolbar (same buttons, no HeaderLabel). The panel retains `_lastActiveSchematicDoc` in `WorkspaceViewModel` — only schematic docs update it; focusing a data display / symbol / cell tab no longer blanks the panel. `AnalysesListViewModel` gains `public event Action? RunRequested` and `[RelayCommand(CanExecute = nameof(HasActiveSchematic))] private void Run() => RunRequested?.Invoke();`; `RefreshCommandStates` calls `RunCommand.NotifyCanExecuteChanged()`. `WorkspaceViewModel.RunAnalysis` falls back to `_lastActiveSchematicDoc` when no schematic is the active dockable; body extracted to `RunSchematicDocAsync`. `WireAnalysesRun()` subscribes `OnAnalysesRunRequested` (fires `_ = RunSchematicDocAsync(doc)`) to `ListVm.RunRequested`; called after each `OnDocumentDockPropertyChanged` re-subscription (ctor, NewWorkspace, SwitchToWorkspace). `_lastActiveSchematicDoc = null` on workspace clear; `OnDockableClosed` blanks panel if the closed doc is the retained one. 4 gate tests in `AnalysesToolbarRunRetainTests.cs`. Build 0W/0E; 1678 total tests pass.

Sweep card reorder (brief-sweep-card-reorder, 2026-06-17) — COMPLETE: Up/Down on a sweep card reorders it within its chain (`ReorderSweepInChainCommand`, Up=inner/Down=outer); base selection still moves the whole chain (`MoveAnalysisChainCommand`). `ReorderSweepInChainCommand` locates the chain block, swaps the two adjacent sweeps in a local sequence, then relinks `InnerAnalysisName` bottom-up and writes back; snapshots old instances for Undo. `AnalysesListViewModel.MoveUp/MoveDown` branch on whether `SelectedRow.Analysis is ParametricSweepAnalysis`. `CanMoveUp` for a sweep returns true only when the slot above it is also a sweep (i.e. there is an inner sibling); `CanMoveDown` returns true only when the slot below is also a sweep. 12 new gate tests in `SweepCardReorderTests.cs`; existing `CanMoveUp_LoneSweepRow_ReturnsFalse` test updated to reflect new semantics. Build 0W/0E; 1674 total tests pass.

Analyses copy/paste chains (brief-analyses-copy-paste-chains, 2026-06-17) — COMPLETE: `CloneAnalysis` now handles `ParametricSweepAnalysis` (both Spec and Values forms) and takes an optional `newInnerName` parameter; callers can re-target the inner link. `ExpandSelectionToChains` (internal) on `AnalysesListViewModel` walks `sweepsByInner` outward from each selected base to include its entire sweep chain in model order; `Copy` calls it before serializing. `PasteAnalysesCommand` rewrites `ResolveNames` as a two-pass algorithm: Pass 1 computes collision-free names and builds an old→new `remap` dict; Pass 2 clones each analysis with both its new name and a remapped `InnerAnalysisName` (inner in paste set → remapped name; lone sweep → `retargetInner ?? original`). `Paste` in `AnalysesListViewModel` passes `retargetInner: SelectedRow?.Analysis.Name`. 8 gate tests in `AnalysesCopyPasteChainTests.cs`. Build 0W/0E; 1662 total tests pass.

Analyses list grouping (brief-analyses-list-grouping, 2026-06-17) — COMPLETE: The Analyses list renders `ParametricSweepAnalysis` members indented (20 px left margin) under their base simulation with a live `"N pts: a…b"` summary and `"SW"` type badge. `AnalysisRowViewModel` gains `IsSweep` (bool), `Name` returns `psa.SweepVarName` for sweeps (the internal `Analysis.Name` is unchanged), `TypeLabel` gains the `ParametricSweepAnalysis => "SW"` arm, `ComputeSummary` gains `FormatSweepSummary`+`FmtNum`. `BoolToIndentConverter` (new, `namespace CircuitRF.Ui.ViewModels`) maps `true → Thickness(20,0,0,0)`; `AnalysesListView.axaml` adds `<UserControl.Resources>` with it and binds `Border.Margin` via `IndentConv`. `MoveAnalysisChainCommand` (new, `src/Ui/Commands/Analysis/`) moves the whole chain (base + its contiguous sweeps) past the adjacent chain; `MoveRange` rotates the block into the target slot. `AnalysesListViewModel.MoveUp/Down` switch to `MoveAnalysisChainCommand`; `CanMoveUp/Down` recompute the block start/end before checking boundaries. 15 gate tests in `AnalysesListGroupingTests.cs`. Build 0W/0E; 1652 total tests pass.

Sweep revamp Stage 3 (brief-sweep-revamp-3-editor, 2026-06-17) — COMPLETE: The analysis editor is the unified model — base type + ordered `SweepAxes` with per-axis `Enabled` and ↑/↓ reorder. **Critical fix:** `BuildAnalyses` now writes the dialog's `Enabled` to the base and each row's `Enabled` to its own sweep (the old `!hasSweeps && Enabled` / `isLast && Enabled` hack produced dead chains under Stage 2's collapse logic and is gone). `SweepAxes[0]` = innermost = plot X axis; rows below are outer (slower) sweeps. `SweepAxisRowViewModel` gained `[ObservableProperty] bool Enabled = true`; `FromPsa` restores it from `psa.Enabled`. `AnalysisEditorViewModel` gained `MoveSweepAxisUpCommand`/`MoveSweepAxisDownCommand` (mirror `RemoveSweepAxisCommand`). Edit-restore: `_enabled = inner.Enabled` (was `sweepChain[^1].Enabled`). `SweepAxisRowView.axaml` Row 1 gains a `CheckBox` (Enabled, left) and `↑`/`↓`/`×` button group (right, `sw-icon` style); code-behind wires two new click handlers same as `OnRemoveClick`. `AnalysisEditorDialog.axaml` shows a one-line order hint above the axes. 8 new gate tests in `SweepRevamp3EditorTests.cs`; 1637 total tests pass. Build 0W/0E. **Parametric-sweep revamp (Stages 1–3) COMPLETE.**

Phase 7.3b (family role, 2026-06-17) — COMPLETE: A single `Trace` with a `FamilyIterate` axis renders **N curves** (`FamilyCurves`), one per value of the iterated axis. Two entry points: (1) **Picker** — axis-role editor now has a 3-state toggle (X / Pinned / Family); `AxisRoleRowViewModel.IsFamily` added, `OnAxisSetToFamily` demotion callback, `FlushSliceAndRebuild` emits `AxisRole.FamilyIterate`; (2) **Auto-recognition** — bare `Name` (no `[`) or `Name[:, :]` (2 kept axes) parses as family (convention: last-kept=X, earlier-kept=Family). `CubeTraceSpecParser` synthesizes all-`:` tokens for bare names and replaces the old `xCount!=1` error with family assignment (keptDims≤2) or error (>2). `PlotInspectorViewModel.TrySetCubeData` routes single-cube specs (CubeName+Slice both non-null) through the slice path even when `Expression` is set; multi-cube expressions use `TraceExpression`. Static `ResolveFamily` loops the family axis, slices each rank-1 curve, calls `Trace.SetFamilyData`. `Trace` gains: `AxisRole.FamilyIterate`, `MaxFamilyCurves=101` (hard cap), `FamilyCurve` nested class, `FamilyCurves` list, `FamilyAxisName`, `IsFamily`, `SetFamilyData`, `RectY` private helper, updated `PathBoundingRect` (spans all curves for autoscale). `TraceRenderer.Draw` short-circuits for `IsFamily`: one stepped-color path per curve, then `DrawFamilyLegend` (corner box, axis name title, swatch+label rows, capped at 12 with "(+N) more" tail). Persistence: `AxisRole.FamilyIterate` round-trips via numeric enum value in `.cdd`; `FamilyCurves`/`Points` are derived and never serialized. Markers on family traces are deferred. 7 new gate tests (Parser_TwoKept, Parser_Bare, Parser_ThreeKept_Errors, Family_RendersNCurves, Family_Cap101, Family_Autoscale, plus TwoXAxes test updated). Build 0W/0E; 1605 total tests pass. **Phase 7.3 COMPLETE.**

Inline editor fixes (brief-inline-editor-fixes, 2026-06-16) — COMPLETE: The inline edit box derives its position solely from `SchematicComponent.LabelRowGeometry` → `WorldToScreen` (same source as the renderer and hit-test), so tweaking label placement is a one-line change in `SchematicComponent`. The hand-rolled `cpy + zoom*120 + textSize + row*(textSize+2)` formula (which drifted progressively lower at low zoom due to a non-scaling `+2` per-row term) is gone; `LabelBaseYFor` (N-aware) also places SDD/ZPort param boxes correctly for free. `ComponentLabelAnchor` now carries `Symbol` and `PortCount`; prefix measured once at the renderer's reference size (70) so it is zoom-independent. **VAR/SDD parameters are name-editable inline**: the param text includes `"Name = Expr Unit"` (select-all on open), and `CommitInlineEdit` parses out the `=` split → `EditParameterCommand` now takes an optional `newName` (snapshot old name for full undo). **Other params select value-only**: `InlineEditSelLength = param.Expression.Length` when a unit is present. **Unit remap**: `ParseExpressionUnit(raw, param)` overload (internal, testable) splits a no-space trailing unit ("1Ω" → "1"+"Ω") by checking whether the run matches `param.Unit` (canonical casing) or is a recognized engine unit (not a bare SI prefix like "n"). `FocusAndSelectInlineEditBox` is a shared helper used by both the component-label path and the wire-label path so both honour the selection contract. 8 gate tests in `InlineEditorFixesTests.cs`. Build 0W/0E; 1507 total tests pass.

Component label hitbox (brief-component-label-hitbox, 2026-06-16) — COMPLETE: Label row geometry has a single source of truth — `SchematicComponent.LabelRowGeometry` (anchor + hit band) and `SchematicComponent.LabelBaseYFor` (N-aware base-Y for SDD/ZPort, reusing `SymbolPortDefs.SddBodyRect`). The renderer (`DrawLabels`), the hit-test (`TestComponentLabels`), and both FullBb builders (`ToRenderComponent`, `SchematicModelBuilder`) all derive from these, so the clickable zone always tracks the rendered text and SDD/ZPort labels always clear the port-count-grown body. **Do not reintroduce a parallel copy of the label-layout constants in the hit-test.** Previously `TestComponentLabels` had private stale constants (`LabelRowHeight=72`, `LabelStartOffY=134`) that drifted from the renderer, especially after user-moved offsets. Bug B: fixed `LabelBaseY=280` constant caused label overlap on SDD/ZPort for N≥4; `LabelBaseYFor` returns `Math.Max(LabelBaseY, SddBodyRect(N).HalfH + LabelWorldStep)`. 6 gate tests in `ComponentLabelHitboxTests.cs`. Build 0W/0E; 1498 total tests pass.

Symbol library overhaul (brief-symbol-library-overhaul, 2026-06-16) — COMPLETE: SDD/ZPort autogen symbol uses a port-count-aware rounded-rect body (grows in ±Y with N, `RRect` radius=12) with 2N ± pins whose Y coordinates are ALWAYS whole multiples of the connection grid (100). Root cause fixed: `portSpacing=300` caused half-grid pin Y via `(nLeft-1)*0.5 * 300` fractions (e.g. ±50, ±150); banker's rounding collapsed these to the same P-cell → false "connected" on empty schematic. Fix: `portSpacing=400` so every center is an even multiple of 200, ±100 pins land on odd multiples of 100. N=1 special-cased (+ left at (−200,0), − right at (+200,0)) for both Sdd and ZPort. Body edges at ±90, stubs from ±90 to ±200; port-number/polarity `TextPrimitive` labels placed inside body near each stem. ZPort "Z" mark (diagonal lines) removed. `BuiltInSymbols.Primitives(kind, portCount)` overload added; old `Primitives(kind)` calls `Primitives(kind, 2)` for backward compat. Pin symbol reoriented horizontal: 6-point hexagon body + stem to right, tip at (200,0); `SymbolPortDefs.For(Pin)` → `("1", 200, 0)`. VAR symbol carries centered `TextPrimitive("VAR")`. ToneSource, Vdc, P1Tone, Term each carry "+" and "−" `TextPrimitive` indicators left of their stems. `SchematicModelBuilder` Pin port updated from `(0,−200)` to `(200,0)`. 10 gate tests in `SymbolLibraryOverhaulTests.cs`. Build 0W/0E; 931 Ui.Tests pass.

Trace expressions (brief-trace-expressions, 2026-06-16) — COMPLETE: Cube traces accept full **element-wise expressions** over cube slices (`TraceExpression`), reusing the circuitRF scalar expression engine evaluated per X-sample with cube refs bound as placeholder variables (`__c0`, `__c1`, …). Examples: `mag(V[:, 0, 0]) + mag(V[:, 0, 1])`, `dB20(V[:, 0, 1]) - dB20(V[:, 0, 0])`, `conj(V[:, 0, 0])`. Transforms are function calls (`mag(...)`, `dB20(...)`, `conj(...)`) — `CubeShorthand` and `BuildPickerExpression()` now emit function-call syntax (e.g. `mag(V[:, 0])` not `mag V[:, 0]`). `Trace.Expression` (nullable string) supersedes `CubeName`/`Slice`/`Transform` for value production when set; `Trace.ExpressionError` carries parse/eval failure text (cleared on success). `IsCubeBound` now includes `Expression is not null`. Pipeline in `TraceExpression.TryEvaluate`: scan expression for `CubeName[...]` refs → slice each to rank-1 → validate same X length → substitute placeholders → `Parser.Parse` → evaluate per X-sample via `Evaluator.InjectResolved` → yield `(xVals, complexValues?, realValues?, xAxisName, xUnit)`. `PlotInspectorViewModel.TrySetCubeData` branches: `trace.Expression is not null` → `TraceExpression.TryEvaluate` path, else existing single-slice path. `CommitSpec` commits to `trace.Expression` and delegates validation to `TrySetCubeData`. `FlushSliceAndRebuild` and `ApplySelectedTransform` set `trace.Expression = BuildPickerExpression()` after picker edits. `SpecError` is a computed getter reading `trace.ExpressionError`. `dB20` added as alias for `dB` in `Evaluator.cs`. Smith/Polar + real-valued expression → gentle "needs complex" error. Invalid syntax / mismatched slice dimensions surface as the ` <invalid>` hint. Matrix math is out of scope (element-wise only). 9 gate tests in `TraceExpressionTests.cs`. Build 0W/0E; 921 Ui.Tests pass.

Node-picker labeled filter (brief-node-picker-labeled-filter, 2026-06-16) — COMPLETE: The cube `node` axis-role picker filters to **user-labeled nodes only** (filter ON by default). Provenance is threaded `NetExtractor` → `TestBench.LabeledNets` → `NodeMap.LabeledNames` → `__LabeledNodes` side cube in the HB DataSet (persisted in `.npy`). `TraceRowViewModel.RebuildAxisRoles` reads `__LabeledNodes` (if present) and filters the `node` axis `PinOptions` to labeled nodes. A parallel `PinOptionIndices[]` list maps display-row → true cube-axis index so `TruePinIndex` (= `PinOptionIndices?[PinIndex] ?? PinIndex`) always resolves the true cube index. `FlushSliceAndRebuild` uses `TruePinIndex` in the emitted `AxisSlice`. `ShowAllNodes` (per-trace observable) defaults to `false` (filter ON) when `__LabeledNodes` is present; defaults to `true` when the cube is absent (hand-written netlist → show-all so those files stay usable). A present-but-empty `__LabeledNodes` shows nothing. A "Show all nodes" toggle appears on trace cards with a node axis. `__`-prefixed cubes are metadata: `RebuildSignals` skips them so `__LabeledNodes` never appears as a selectable signal. 11 gate tests (T1–T11) in `NodePickerLabeledFilterTests.cs` (Ui.Tests) and `HbLabeledNodesCubeTests.cs` (Engine.Tests). Build 0W/0E; 1460 total tests pass.

Trace-card layout fixes (brief-table-cube-layout-fixes) — COMPLETE: Five trace-card / Table fixes. (#1) Z0 row gated entirely on S-param traces: `ShowZ0Row => IsScatteringTrace` on `TraceRowViewModel`; outer StackPanel uses `IsVisible="{Binding ShowZ0Row}"` so cube/HB traces show no Z0 label or Ω. (#2) `OnFreqUnitChanged` in `PlotInspectorViewModel` now calls `vm.OnFreqUnitChanged()` on each row (which calls `RebuildAxisRoles()`) then `RebuildAndNotify()` so harmonic pin labels rebuild in the new unit. (#3+4) Identity row reordered to **signal | unified-transform | matrix(S-only, Auto) | →R**; two overlaid combos (network `YAxis` and cube `CubeTransform`) replaced by one `SelectedTransformItem` combo bound to `TraceTransformItems` (returns `AllCubeTransforms` for cube, `AllTransformsForNetwork` for network traces). `CubeTransformItem` gains `Enabled` flag; `AllTransformsForNetwork` disables `dB10`, `dB`, `Conj` (cube-only). `SelectedTransformItem` maps via `YAxisToCubeTransform`/`CubeTransformToYAxis`; `SyncTransformItem()` resyncs silently from `RefreshDescription`. (#5) Table trace-header double-click routes to the inline spec TextBox via `PlotInspectorView.FocusSpecTextBox(idx)` (stores `_inspectorView` in `PlotControl`, posts focus at `Render` priority). 3 gate tests in `TraceCardLayoutTests.cs`. Build 0W/0E; 1449 total tests pass.

Vdc component (brief-vsource-vdc-fix) — COMPLETE: `SymbolKind.VoltageSource` removed; replaced by `SymbolKind.Vdc`. Registry entry: DisplayName `"Vdc"`, prefix `"V"`, `IsCommon: true`, SearchTerms `["Vdc","DC","bias","supply","voltage","V"]`; `EngineReference(Vdc)` → `"Vdc"`; `TryParseCode("V")` and `TryParseCode("VDC")` → `SymbolKind.Vdc`; `DefaultParameters(Vdc)` → `[Vdc=0 V]` (single param, `ShowOnSchematic: true`). `ToneSource` gains a hidden `Vdc=0 V` param (`ShowOnSchematic: false`) as the 3rd default. Glyph: `BuildVdc()` in `BuiltInSymbols.cs` — 4-primitive battery (top lead, long +bar, short −bar, bottom lead); old 6-primitive circle+±marks removed. Ground glyph changed from 2-primitive (stem + filled triangle) to 4-primitive (stem + 3 horizontal bars). `LibraryCatalog.AllItems` sort updated to `StringComparer.OrdinalIgnoreCase` (explicit). `SchematicModelBuilder`: `SymbolKind.VoltageSource` → `Vdc`; demo params updated. 8 gate tests: 4 Engine.Tests + 4 Ui.Tests. Build 0W/0E; 1419 total tests pass.

Table/trace-card cube UX cluster (brief-table-cube-ux-cluster) — COMPLETE: Four refinements for cube (HB-sweep) data. (#3) Sort-arrow gap widened: `triCx` uses `boldFont.MeasureText(" ") * 1.5f` instead of the hardcoded `+2f`; `CalcFitWidth` reservation updated to match. (#5) MatrixType (S/Z/Y) combo gated to S-parameter (network/SNP) sources only via `ShowMatrixTypeCombo => !IsCubeBound && Data is { } d && !d.IsEmpty`; notified from `OnSelectedSignalChanged`, `RebuildSignals`, and `RefreshDescription`. (#6) Harmonic axis renders in the plot's `FreqUnits` (display-only): `TableRenderer` detects `IsFreqUnit(CubeXUnit)` and scales both the column-0 header and data cells by `FreqUnits.Scale()`; non-frequency cube axes (Pin/dBm, bias/V) are unscaled. Axis-role pin options in `TraceRowViewModel.RebuildAxisRoles` also scale freq-unit axes via `_parent.FreqUnit`. (#4) Inline spec editor: `Trace.InvalidSpecText` (string?) stores user's raw text when invalid; `CubeShorthand` returns `"{text} <invalid>"` and `FormatCubeCell` returns `""` when set. `CubeTraceSpecParser.TryParse(text, ds, ...)` is the pure-static inverse of `Trace.CubeShorthand` (transform + cube name + per-axis token: `:`, quoted label, or integer index). `TraceRowViewModel` gains `SpecShorthand` (raw editable text, no `<invalid>` suffix), `SpecError`, `HasSpecError`, and `CommitSpec(text)` (parse + apply / set invalid). `PlotInspectorView.axaml` adds a TextBox + `SelectableTextBlock` hint (gentle, selectable) below the axis-role editor for cube traces; event handlers in code-behind (LostFocus / Enter). 6 new gate tests in `TableCubeTraceTests.cs` (`CubeTraceSpecParserTests` class). Build 0W/0E; tests green.

Table plot cube-bound traces (brief-table-cube-traces) — COMPLETE: The Table plot supports cube-bound traces. When all traces in a plot are cube-bound, column 0 becomes the trace's kept (X) axis (name + unit, no freq scaling); cells read cube values via `Trace.FormatCubeCell`; and trace column headers use `Trace.CubeShorthand` (DataCube `Name[pinned, …, :]` index form). Mixed cube+SNP plots fall back to frequency mode. `TableRenderer.GetSortedRowAxis` returns the union of all cube X values (sorted) and delegates to `GetSortedFrequencies` for legacy/mixed plots. `Trace` exposes `CubeXValues`/`CubeComplex`/`CubeReal`/`CubeXAxisName`/`CubeXUnit` read accessors (no recompute). Markers on cube traces remain unsupported. 11 gate tests in `TableCubeTraceTests.cs`. Build 0W/0E; 1391 total tests pass.

Sweep Start/Stop/Step|Npts (brief-parametric-sweep-stepcount) — COMPLETE: `SweepExpander`/`SweepAxisMode` moved to `CircuitRF.Core.Design` (Core firewall). `SweepSpec` redesigned to `{ Start, Stop, StepOrCount, Mode, Kind }` (no Variable). `ParametricSweepAnalysis` gains spec constructor (expands eagerly + stores `Spec` for round-trip). `SweepAxisRowViewModel` adds `BuildSpec() → SweepSpec?` (returns null for List mode) and `FromPsa` now restores `StartExpr/StopExpr/StepOrCountExpr/Mode/Kind` from `psa.Spec` when present (falls back to List). `AnalysisEditorViewModel.BuildAnalyses()` uses spec constructor for StepSize/PointCount axes so the `.cnl` writer emits compact `Start=/Stop=/Step=|Npts=` form. Build 0W/0E; 260 Core.Tests + 880 Ui.Tests pass.

Sweep results one-file (brief-sweep-results-one-file) — COMPLETE: A parametric-sweep tree writes a **single** results file named after its **root inner analysis** (`HB1.npy`, not `HB1_sweep_Pin.npy`). Analyses referenced as the `Inner` of any `ParametricSweepAnalysis` are not run or written standalone. Implementation in `SchematicRunService.RunNetlist`: (1) builds `innerOfSweep` set (all `InnerAnalysisName` values) before the dispatch loop; (2) adds `if (innerOfSweep.Contains(analysis.Name)) continue;` guard (name-membership based, independent of `Enabled`); (3) for sweeps, the result name comes from `RootInnerName(psa, tb)` (walks `InnerAnalysisName` down to the first non-sweep analysis, max 64 hops). `DeduplicateName` still guards if two sweep trees resolve to the same root name. 4 new gate tests (S1–S4): single-sweep one-result, nested-sweep one-result, standalone-still-runs regression, mixed-standalone-and-swept two-results. Build 0W/0E; 1380 total tests pass.

P1Tone source component (brief-sweep-5-p1tone-source) — COMPLETE: `SymbolKind.P1Tone` added to `SchematicModel.cs` enum. Registry entry: DisplayName `"P1Tone"`, prefix `"P"`, `IsCommon: true`, SearchTerms `["P1Tone","power","Pavl",...]`; `EngineReference(P1Tone)` → `"P1Tone"`; `TryParseCode("P1TONE")` → `SymbolKind.P1Tone`; `DefaultParameters(P1Tone)` → `[Pavl=0dBm, Z=50Ω, Freq=1GHz, Phase=0deg]`; `SymbolPortDefs.For(P1Tone)` uses default 2-pin (top/bottom). Glyph: `BuildP1Tone()` in `BuiltInSymbols.cs` (circle + sine + power-arrow chevron ↑). Core layer: `P1ToneModel` in `src/Core/Devices/P1ToneModel.cs`; `ComponentModelFactory` registers `"P1Tone"` in `_parameterizedTypes` and dispatches to `CreateP1ToneModel`; `Elaborator` mints `__p1tone_{path}_drv` and calls `ResolveP1ToneParameters`. HB layer: `HbEngine.Run`/`RunTwoTone` call `SetToneContext(fc, driveFreqHz)` on every `P1ToneModel` before extraction; commensurability checks include `P1ToneModel.FreqHz`. 7 gate tests in `P1ToneTests.cs`. Build 0W/0E; 1346 total tests pass.

Sweep Fix 4 (brief-sweep-4-edit-analysis-ui) — COMPLETE: Analysis editor now supports 0..N parametric sweep axes wrapping any inner analysis (DC/SP/HB). **Headless helper:** `SweepExpander` (`src/Ui/Schematic/SweepExpander.cs`) + `SweepAxisMode` enum (`StepSize`/`PointCount`/`List`) — static `ExpandSweep(start, stop, stepOrCount, mode, kind)` and `ExpandList(csv)`. **Row VM:** `SweepAxisRowViewModel` (`src/Ui/ViewModels/SweepAxisRowViewModel.cs`) — VarName (combo with `KnownVarNames` from VAR components + soft unknown-variable warning), Mode (seg-btns), per-mode fields, Lin/Log kind, live preview, `BuildValues() → double[]?`, `FromPsa` restore factory, `FromLegacyHbSweep` migration factory. **Row view:** `SweepAxisRowView.axaml(.cs)` — card-style with AutoCompleteBox for variable name, Mode seg-btns, Lin/Log, Start/Stop/Step|Count|List fields, Remove button (walks visual tree to find `AnalysisEditorViewModel.RemoveSweepAxisCommand`). **Analysis editor VM:** `AnalysisEditorViewModel` gains `ObservableCollection<SweepAxisRowViewModel> SweepAxes`, `SweepsExpanded`, `AddSweepAxisCommand`, `RemoveSweepAxisCommand`, `EditingChainNames`, `BuildAnalyses() → IReadOnlyList<Analysis>?` (replaces `BuildAnalysis()`). Chain: [inner (Enabled=false), sweep₁ (false), …, sweepₙ (Enabled=true)]; naming scheme `<innerName>_sweep_<varName>`. Legacy HB `SweepVar*` migrated into a StepSize row on `FromAnalysis`. Edit constructor handles `ParametricSweepAnalysis` by resolving to the innermost non-sweep analysis and loading the chain via `ResolveChain`. **Dialog:** `AnalysisEditorDialog.axaml` adds `x:DataType` to Window and a "Parametric Sweeps" Expander below the analysis body panels; AXAML uses `DataTemplate DataType="vm:SweepAxisRowViewModel"` → `SweepAxisRowView`. Code-behind updated: `ShowAsync` returns `IReadOnlyList<Analysis>?`; `OnOkClick` calls `BuildAnalyses()`. **HB body:** old `SweepEnabled`/`SweepVarName`/`SweepStart|Stop|StepExpr` fields + AXAML section removed. **New commands:** `AddAnalysesCommand` (adds list contiguously, undo removes all); `EditAnalysisChainCommand` (replaces old chain by names, undo restores at original index). **AnalysesListViewModel:** `Add` → `AddAnalysesCommand`; `Edit` collects `vm.EditingChainNames` → `EditAnalysisChainCommand`. **Serialization:** `CschAnalysis` gains `PsaVarName/PsaValues/PsaInnerName`; `AnalysisSerialization.ToDto/FromDto` handles `"sweep"` type tag → `ParametricSweepAnalysis` round-trip. **NetExtractor:** `Enabled` filter removed — ALL analyses flow into `tb.Analyses` (so `ParametricSweepEngine` can find inner analyses by name); comment explains the split. **SchematicRunService:** `if (!analysis.Enabled) continue;` guard added at dispatch loop — disabled chain members are never run directly. 21 new tests: `SweepExpanderTests` (9 tests covering all modes/kinds), `SweepBuilderTests` (4 tests: nested chain, no-axes single, legacy HB migration, outer-sweep edit load), `NetExtractorAnalysesTests` updated (5 existing tests adapted + 1 new chain test). Build 0W/0E; 1339 total tests pass.

VAR component UI (brief-var-component-ui) — COMPLETE: double-clicking a VAR component opens `VarEditorDialog` (instead of the generic `ParameterEditorDialog`). The editor has two modes — **Mode A (Text, default)**: a single multi-line `TextBox` where each line is `name = expression`; comments (`#`/`//`) and blank lines are skipped; a validation banner shows parse errors and duplicate names; "Apply" commits via `SetVarParametersCommand` (atomic, undoable). **Mode B (Rows)**: an `ItemsControl` of editable name/expression/unit rows with Add/Remove per-row, routing through `SetVarParamNameCommand`/`EditParameterCommand`/`Add-Remove VarParameterCommand`. Switching Text→Rows applies pending text; switching Rows→Text serializes current params back to text. All edits flow through `SchematicViewModel.Execute` (undo/redo + dirty dot). Parse/serialize logic is in `VarTextParser` (static, framework-free, testable): `ParseLines()` and `SerializeLines()`. VAR symbol is now a port-less box (no leads) in `BuiltInSymbols` (`BuildVar()`). `AllBuiltIns_HaveAtLeastOnePin` updated to skip `SymbolKind.Var`. 2 new gate tests in `VarComponentTests`: `ParseLines_RoundTrips` and `Duplicate_EmptyName_Flagged`. Build 0W/0E; 1315 total tests pass. **VAR component complete.**

VAR component (brief-var-component-core) — COMPLETE: `SymbolKind.Var` is a node-less, port-less component whose `EditableParameter` rows are routed by `NetExtractor.ExtractModel` into the enclosing frame's `Cell.Variables` (sub-cell) or `TestBench.GlobalVariables` (testbench top). VAR is **never emitted as an `ElaboratedComponent`** — it is skipped in the emission loop (alongside `Ground`/`Pin`) and `EngineReference(Var)` returns sentinel `"VAR"` (not a factory primitive). Per-cell isolation and HB sweepability fall out of the existing scope machinery (`Elaborator.BuildGlobalScope` / `BuildCellScope` already bind `Variables`). `ComponentTypeRegistry` entry: DisplayName `"VAR"`, prefix `"VAR"`, `IsCommon: true`, SearchTerms `["VAR","Variable","var","vars","parameter","sweep"]`; `TryParseCode("VAR")` → `SymbolKind.Var`; `DefaultParameters(Var)` → `[]`; `SymbolPortDefs.For(Var)` → `[]`. Duplicate variable names across VAR rows in the same frame emit a conflict and keep the first. `.cnl` representation: VAR-sourced variables emit naturally via the existing `name = expr [unit]` variable directive — no new `.cnl` syntax needed. 6 gate tests in `VarComponentTests.cs`. Build 0W/0E; 1313 total tests pass.

Load Run Results (brief-datadisplay-load-results) — COMPLETE: Data Display can load a schematic's run results via "Load Run Results…" (toolbar button, `FolderArrowDownOutline` icon). `DisplayWindowViewModel` gains `_loadRunResultsAction` + `SetLoadRunResultsAction` + `GetResultsRootAction` (public `Func<string?>?`) + `LoadRunResultsCommand`. `DataDisplayView.axaml.cs.OnLoaded` injects `DoLoadRunResultsAsync` via `SetLoadRunResultsAction`; `DoLoadRunResultsAsync` opens `StorageProvider.OpenFolderPickerAsync` with `SuggestedStartLocation` = `GetResultsRootAction?.Invoke()` (via `TryGetFolderFromPathAsync`), then calls `DataSourceLibrary.LoadFileAsync(path)` for every `*.npy` in the chosen folder (dedup is handled there); no `.npy` files → `ShowErrorAsync`. `WorkspaceViewModel` injects `GetResultsRootAction = GetResultsRoot` (private helper: `<workspaceRoot>/results` when workspace open, null otherwise) in both `NewDataDisplay` and `OpenOrActivateDataDisplayCoreAsync`. A richer results-browser flyout (list of schematic-key folders) is a noted follow-up. Build 0W/0E; 1307 total tests pass.

Phase 7.3a (axis-role assignment picker) — COMPLETE: cube-bound traces are now authored via a per-axis role editor (X / Pinned) over any-rank DataCube; the flat ≤2-D enumeration is replaced by one `AvailableSignals` item per cube (rank ≥1; "S"/"Z0" still skipped). New `AxisRoleRowViewModel` (per-axis row: `AxisName`, `Unit`, `IsX`, `PinIndex`, `PinOptions`, `IsRoleToggleable`); `ObservableCollection<AxisRoleRowViewModel> AxisRoles` on `TraceRowViewModel`, rebuilt in `RebuildSignals()` (at the end) and in `OnSelectedSignalChanged` (cube apply block). Auto-flip invariant: setting IsX on a row calls `OnAxisSetToX` which silently demotes other X rows via `SetIsXSilent`; `FlushSliceAndRebuild` writes the new `AxisSlice[]` back to `Trace.Slice` (by axis order, Role=KeepAsX/PinToIndex) and calls `_parent.RebuildAndNotify()`. No-X guard in both `RebuildAxisRoles` and `FlushSliceAndRebuild` (first axis is forced X if none has KeepAsX). Owner-side resolution in `TrySetCubeData` generalised to N-D: build `object[] args` by **name-matched** lookup (`foreach (var s in slice) if (s.AxisName == axName)`) instead of positional, with fallback (axis 0 kept when no KeepAsX entry found). `PlotInspectorView.axaml` gains `ItemsControl` bound to `AxisRoles` (inside `IsStandardTrace` StackPanel, `IsVisible=IsCubeBoundTrace`); each row shows axis label, X/Pin seg-btns, and pin-index ComboBox. `CubeTraceTests.CubeTrace_RankGE3_NotOffered` renamed/updated to `CubeTrace_RankGE3_Offered` (old assertion reversed). 5 new gate tests in `AxisRolePickerTests.cs`. Build 0W/0E; 811 Ui.Tests pass.

Phase 7.2f-2 (Z0 box default-locked + Override checkbox) — COMPLETE: The trace-card Z0 box is read-only by default, showing the source's port-1 uniform reference (`SourceZ0PerPort[0]`, or `Data.Z0` when no per-port vector). An "Override" checkbox to the right unlocks the box for uniform-renorm; unchecking reverts the box and `_trace.Z0` to the source port-1 value and triggers a recompute. Non-uniform sources (`Z0Kind.NonUniform`) replace the box+checkbox entirely with subtle grey "Multiple Port Normalization" text — no editing, no glyph. UniformComplex is treated as uniform (box shown with complex port-1 value; Override available). The per-trace `AlertCircleOutline` orange glyph (7.2e) is removed from the trace card; the one-time Messages warning on load is retained. VM: `TraceRowViewModel` gains `_sourceZ0Kind` (stashed by `ApplySourceZ0`, now instance method; static trace-only path renamed `StampSourceZ0OnTrace`), `SourceZ0IsNonUniform`, `IsScatteringTrace`, `IsMultiPortNormalization`, `ShowZ0Control`, `[ObservableProperty] Z0OverrideEnabled`, `SeedZ0FromSource()`, `_applyingSource` + `_seedingZ0` flags to suppress partial-method rebuilds during seeding. `IsZ0Editable` changed to `ShowZ0Control && Z0OverrideEnabled`. `Z0DisabledReason` retained for existing tests. 5 gate tests in `Z0OverrideTests.cs`. Build 0W/0E; 1296 total tests pass.

Phase 7.2f (per-port Z0 compute) — COMPLETE: scattering traces now compute against the source's true per-port Z0 vector (`SourceZ0PerPort`/`SourceZ0IsUnusual`, populated from the Z0 cube via 7.2e classification). `Trace.BuildMatrixPath` uses `RFNetwork.SToZ(mat, SourceZ0PerPort)`/`SToY(mat, SourceZ0PerPort)` when `SourceZ0IsUnusual` (not the scalar-collapse cheat); `GetMarkerImpedanceString` picks `SourceZ0PerPort[Row]` instead of the scalar `_z0`. `BuildDerivedPath` (stability, max-gain) renorms non-uniform sources to uniform-real via `SToS(m, SourceZ0PerPort, z0RealArray)` before calling `StabilityMu`/`MaxGain`. Uniform/Touchstone path unchanged. `StampSourceZ0OnTrace` (internal static) propagates both fields from the library entry to the trace during `PlotInspectorViewModel.RefreshSourceZ0`. 5 gate tests in `PerPortZ0ComputeTests.cs`. Build 0W/0E.

Phase 7.2e (non-uniform/complex Z0 indicator) — COMPLETE: Data Display surfaces an always-on per-trace badge (Material `AlertCircleOutline`, `CrfWarningBrush`, tooltip = per-port Z0 values formatted as `portN=<real>Ω` or `portN=<re>+<im>jΩ`) on scattering traces whose source `HasUnusualZ0` (i.e. `DataSetBuilder.ClassifyZ0(_data["Z0"])` returns `NonUniform` or `UniformComplex`, not `UniformReal`). `DataSourceEntryViewModel` gains `Z0Kind?`, `HasUnusualZ0`, `Z0PerPort` (computed by `ClassifyZ0FromData()`, called from both constructors and both `Refresh*` methods). `TraceRowViewModel` gains `ShowZ0Badge` (= non-cube-bound S-trace with `entry.HasUnusualZ0`) and `Z0BadgeTooltip` (per-port list + kind suffix); notified from `OnSelectedSignalChanged`, `RebuildSignals()`, and `RefreshDescription()`. `PlotInspectorView.axaml` Z0 row Grid extended to 5 columns (`Auto,Auto,Auto,*,Auto`) with `mi:MaterialIcon` in column 4. One-time Messages warning fires via the library→workspace event seam: `DataSourceLibraryViewModel.UnusualZ0Detected` event (guarded by `_warnedPaths HashSet`, cleared on `Remove`); `WorkspaceViewModel.WireDataDisplayLibraryEvents()` subscribes at document-creation time and posts via `Messages.Warning`. 3 gate tests in `Z0IndicatorTests.cs`. Build 0W/0E; 1286 total tests pass. Full per-port Z0-dependent compute (S→Y/Z, marker impedance, stability on non-uniform sources) remains the **7.2f** follow-on.

Phase 7.2c-c (DataSource rename) — COMPLETE: pure rename pass. `SnpLibraryViewModel` → `DataSourceLibraryViewModel` (`DataSourceLibraryViewModel.cs`); `SnpEntryViewModel` → `DataSourceEntryViewModel` (`DataSourceEntryViewModel.cs`); `DisplayWindowViewModel.SnpLibrary` → `DataSourceLibrary`; view files `SnpLibraryView.axaml(.cs)` → `DataSourceLibraryView.axaml(.cs)`. All consumers retyped: `TabViewModel`, `DataDisplayViewModel`, `PlotContainerViewModel`, `PlotInspectorViewModel` (`_library` field + `TrySetCubeData` param + `LibraryEntries` collection type), `TraceDataItem` (Entry type), `PlotControl.Library` DirectProperty, `WorkspaceViewModel.RefreshOpenDataDisplaysAsync`, AXAML `x:DataType` and `DataTemplate` references, `DataDisplayView.axaml(.cs)`. NAMING DEBT headers removed from both renamed files. No behavior change; no serialization change. Build 0W/0E; 1283 total tests pass. **Phase 7.2c COMPLETE.**

Phase 7.2c-b (minimal-label display-name policy) — COMPLETE: trace display names are computed at the plot level (`TraceLabeler.ComputeMinimalLabels`) from two separate identity components — source (`Path.GetFileNameWithoutExtension(SourcePath)`) and quantity (`ShortDescription` for network-bound; `CubeName(pinned=idx) transform` for cube-bound). Any component constant across the plot's traces is dropped; recomputed on add/remove. Label priority: `CustomLabel` (user override, theme color) › `AutoLabel` (computed policy, trace color) › legacy `ShowFilePrefix` fallback. New `AutoLabel` DirectProperty on `AxisLabelControl`; `LabelStripViewModel._autoLabel` observable property; `PlotContainerViewModel.UpdateLabelStrips()` calls `TraceLabeler` and stamps `AutoLabel` on each non-custom strip; `AxesRenderer.DrawTitleAndAxisLabels()` uses the same lookup for Rect Y-axis margin labels. `alwaysShowSource` reads `AppSettingsViewModel.Instance.AlwaysDisplayDataSourcePrefix`. Separator `·` (U+00B7). 5 gate tests in `MinimalLabelTests.cs`. Build 0W/0E; 793 Ui.Tests pass.

Phase 7.2c-a (cube-native trace path) — COMPLETE: `Trace` is now either **network-bound** (SNP/matrix/derived, unchanged) or **cube-bound** (`SourcePath`+`CubeName`+`Slice`+`Transform`, identity stored as separate fields). Three new types in `Trace.cs`: `CubeTransform` enum (`None dB20 dB10 dB Mag Phase Real Imag Conj`), `AxisRole` enum (`PinToIndex KeepAsX`), `AxisSlice` readonly record struct. `IsCubeBound => CubeName is not null` discriminates the two paths in `BuildPath`. Owner-injects-data pattern: `PlotInspectorViewModel.TrySetCubeData` (internal static) resolves the DataSet from the library by `SourcePath`, slices the DataCube, calls `trace.SetCubeData(xVals, complexValues?, realValues?, xAxisName, xUnit, plotType, freqUnit)` — `Trace` never holds a `DataSet` reference. Signal picker (TraceRowViewModel.RebuildSignals) enumerates ≤2-D cubes only (rank 1 → one signal; rank 2 → enumerate pinned axis per keep-axis; rank ≥ 3 → skip; "S"/"Z0" → skip). `PlotInspectorView.axaml`: CubeTransform ComboBox in col2 (`IsVisible=IsCubeBoundTrace`); YAxis ComboBox visibility bound to `ShowYAxisCombo = IsRectOrTablePlot && !IsCubeBoundTrace`. `.cdd` persistence via `TraceConfig`: new nullable `CubeName`/`CubeTransform`/`CubeSlice` fields, no format-version bump. Cube-bound traces loaded via `LoadPlotContainerConfigAsync` use a placeholder SNP and call `TrySetCubeData` immediately. Markers/derived remain network-only for now (all marker methods guard `if (IsCubeBound) return`). 4 gate tests in `CubeTraceTests.cs`. Build 0W/0E; 788 Ui.Tests pass.

Quit latch fix — COMPLETE (brief-quit-latch): `App._isShuttingDown` is released via `App.AbortQuit()` from `WorkspaceWindow.OnClosing` whenever a close/quit prompt is cancelled (user hit Cancel, or cancelled the save dialog). `AbortQuit() => _isShuttingDown = false` is a harmless no-op when called during a plain window-close cancel (latch was already false). A caught exception in `OnClosing` also calls `AbortQuit()` so an unexpected error in the save pipeline never wedges all future quits. Build 0W/0E.

Data Display tab dirty indicator — COMPLETE (brief-datadisplay-dirty-indicator): `DataDisplayDocumentViewModel.IsDirty` is now live. `DataDisplayViewModel.ContentChanged` fires via two channels: structural undo edits (`UndoRedo.StateChanged`) and inspector redraws (`PlotContainerViewModel.PlotNeedsRedraw`, hooked via `OnPlotsCollectionChanged` on `_plots.CollectionChanged`). `DisplayWindowViewModel.DirtyChanged` bubbles `ContentChanged` from the active tab; it also fires in `OnUndoRedoStateChanged` (tab add/remove) and after each `CaptureBaseline()` call (save and load, so the bullet clears on save). `DataDisplayDocumentViewModel` subscribes `Window.DirtyChanged` and recomputes `IsDirty = Window.HasUnsavedChanges()` — authoritative, ignores view-only state (selection, zoom, pan). 3 gate tests (`DataDisplay_DirtyBullet_On{StructuralEdit,ClearsOnSave,InspectorEdit}`). Build 0W/0E; 784 Ui.Tests pass.

Project Tree "Save" context item — COMPLETE (brief-tree-save-dirty): a "Save" `MenuItem` (first in the context menu) appears on Cell and ViewFile/DataDisplayFile nodes that are currently dirty; hidden when clean. Header is node-kind-specific ("Save Cell" / "Save Schematic" / "Save Symbol" / "Save Data Display"). `ITreeActions` gains `IsNodeDirty(node)` + `SaveNodeAsync(node)`. `WorkspaceViewModel` implements both: `IsCellDirty` is extracted from the old `RefreshCellDirty` body so both the cell-dirty indicator and `IsNodeDirty` share one aggregation; `IsNodeDirty` covers Cell (via `IsCellDirty`), .csch (registry dirty set), .csym (open symbol docs), and .cdd (via `HasUnsavedChanges()`); `SaveNodeAsync` dispatches to `SaveCellViewsAsync` (saves all dirty schematics + symbol editors under the cell dir, then calls `RefreshCellDirty`), `SaveSchematicByPath` (registry → `SchematicPersistence.SaveToFile` → `NotifySessionSaved`), `SaveSymbolByPathAsync` (delegates to existing `SaveMaterializedSymbolDoc`), or `SaveDataDisplayByPathAsync` (delegates to existing `SaveDataDisplayDoc`). `ProjectTreeNodeViewModel` gains `IsSaveable` (plain getter, re-evaluated at menu-open time), `SaveHeader` (kind-specific label), and `SaveCommand` (`AsyncRelayCommand`, no CanExecute guard — `IsVisible=IsSaveable` gates the item in AXAML). 5 new gate tests in `ProjectTreeSaveTests.cs`. Build 0W/0E; 1271 total tests pass.

Close/quit save pipeline — COMPLETE (brief-close-quit-save): two bugs fixed. **Bug #2 (crash):** `PromptSaveBeforeClose` crashed with `ArgumentOutOfRangeException` when only orphaned dirty sessions existed — the old final branch `dirtyMatSymbols[0].Id` had no guard. Fixed: `firstId` is now `string?` with a 7-branch nullable chain ending in `Path.GetFileNameWithoutExtension(orphanedPaths[0])` and a `null` fallback; the message uses `(total == 1 && firstId is not null)`. **Bug #1 (data loss):** dirty `.cdd` documents slipped through the close/quit pipeline. `DataDisplayDocumentViewModel.IsDirty` is never wired to live edits; the fix bypasses it entirely — `HasAnyDirtyWork()`, `PromptSaveBeforeClose`, and `ConfirmCloseDockable` all call `DisplayWindowViewModel.HasUnsavedChanges()` directly (a polled baseline-comparison, set by `SaveAllAsync` / `LoadAllAsync`). New `SaveDataDisplayDoc(dd, owner)` saves materialized docs in-place and scratch docs via a `.cdd` file picker (mirrors the schematic/symbol pattern). `OnClosing` in `WorkspaceWindow.axaml.cs` hardened with try/catch so a future exception in the prompt keeps the window open rather than crashing the app. 3 gate tests in `CloseQuitSaveTests.cs`. Build 0W/0E.

Data Display auto-refresh — COMPLETE (brief-datadisplay-autorefresh): after a successful run, `RunAnalysis` captures the paths returned by `RunResultsWriter.WriteResults` (return type changed from `void` to `IReadOnlyList<string>`) and calls `RefreshOpenDataDisplaysAsync(written)`. That helper iterates all open `DataDisplayDocument`s (both `_openDocsByPath` and `_scratchDataDisplays`) and calls `DataSourceLibraryViewModel.ReloadChangedAsync(changedPaths)` on each. `ReloadChangedAsync` (new) reloads only the entries whose `FilePath` matches one of the changed absolute paths — skipping missing files (no `FindMissingFileAsync` prompt), reusing `ReloadAsync` in-place so SNP/DataSet identity is preserved and `LibraryChanged` fires per entry triggering inspector rebuild + redraw. Brand-new `.npy` files not already in a display are NOT auto-added. Build 0W/0E; 2 new tests (`RunResultsWriter_ReturnsWrittenPaths`, `DataSourceLibraryViewModel_ReloadChanged_OnlyMatching`); 1261 total tests pass.

Tree Remove Cell — COMPLETE (brief-tree-remove-cell): `CellUsageScanner.CountReferencingCells(workspaceRootDir, targetCellDir)` scans the workspace for distinct cells whose schematics contain a `CellRef` resolving to the target (best-effort; skips unreadable schematics). `ITreeActions.RemoveCellAsync` implemented in `WorkspaceViewModel`: guards on `CurrentWorkspacePath`, counts referencing cells, shows a big warning dialog (usage count appended when > 0), force-closes all open tabs/sessions under the cell dir, calls `SystemTrash.TryMoveToTrash`, then refreshes the tree. `ProjectTreeNodeViewModel` gains `RemoveCellCommand` (`IAsyncRelayCommand`, CanExec: `IsCell`). Context menu bottom gains `<Separator IsVisible="{Binding IsCell}"/>` + `<MenuItem Header="Remove Cell" .../>`. Referencing cells are NOT auto-repaired — broken cell-refs already degrade gracefully (Not Found placeholder). Build 0W/0E; 4 new `CellUsageScannerTests` pass; 1255 total tests pass.

Tree Remove-to-Trash — COMPLETE (brief-tree-trash-and-file-remove): removals route through `SystemTrash.TryMoveToTrash` (OS Trash/Recycle Bin; recoverable; **never hard-delete on failure** — returns false + error). Windows uses `SHFileOperation` with `FOF_ALLOWUNDO` (P/Invoke; works for files and directories). macOS uses `osascript → Finder delete`. Linux uses `gio trash`. `ITreeActions` gains `RemoveDataDisplay`/`RemoveFile`; both delegate to the shared private `RemoveNodeToTrashAsync` (confirm dialog → close open tabs via `ForceCloseDockable` → trash → `ProjectTreeTool.Refresh`). `ForceCloseDockable` on `CircuitRfDockFactory` bypasses the dirty-save confirm hook (file is being deleted; saving would be wrong). `ProjectTreeNodeViewModel` gains `IsDataDisplayFile`, `IsRemovableFile` (OtherFile/UserFolder/`.csch`/`.csym` ViewFiles), `RemoveDataDisplayCommand`, `RemoveFileCommand`. `.npy`/results live under `OtherFile`/`UserFolder` node kinds (no dedicated NodeKind). Context menu items added to `ProjectTreeView.axaml`. macOS osascript requires Finder AppleScript authorization (entitlement); headless `dotnet test` returns -1743 — tests treat that as a pass (environment gap). Build 0W/0E; 32 new test assertions pass.

Schematic hierarchy save — COMPLETE (brief-schematic-hierarchy-save): single-document Save (`WorkspaceViewModel.SaveSingleDocument`, the funnel for both toolbar Save and ⌘S-single) now persists the base session **and** every dirty session in the document's nav stack. After writing `doc.ViewModel.EditModel` to `doc.FilePath`, the method iterates `doc.NavFrames`, skips the base (by `ReferenceEquals`), skips clean frames (`!session.UndoRedo.IsModified`), looks up each dirty session's path via `_registry.TryGetPath`, calls `SchematicPersistence.SaveToFile` per session, then `NotifySessionSaved` to clear the dirty flag and refresh the tree dot. Hierarchy edits live in pushed-in shared sessions, NOT in `doc.ViewModel.EditModel`; popped-out dirty sessions are still covered by the Save-All / close-prompt orphaned-session sweep. 3 new gate tests in `HierarchySaveTests.cs`. Build 0W/0E; 1227 tests pass.

Engine diagnostics channel — COMPLETE (Phase brief-engine-diagnostics-channel): `SchematicRunService` drains `nl.Warnings` (populated by `ElaboratedNetlist.AddWarning`/`AddWarningOnce` in `src/Engine`) into `RunResult.Warnings` after every dispatch, including on `EngineError`. `WorkspaceViewModel.RunAnalysis` posts each warning to the Messages pane at Warning level (`Messages.Warning(w)`). The engine never touches `IMessageSink` directly. Gated by `SchematicRunServiceTests.RunNetlist_FloatingNodeFromBuriedTerm_WarningsNonEmpty` (L1e) and `RunNetlist_CleanNetlist_WarningsEmpty` (L1f).

Net extraction pin geometry — COMPLETE: `NetExtractor` now uses `SchematicEditModel.PortDefsOf`/`PortWorldOf` (cell-ref-aware, the render model's single source of truth) for component port positions. Built-in `SymbolPortDefs` is the fallback for non-cell components and cell-reference instances where `SchematicDirectory` is not set (backward-compat). For resolved cell-refs, `NetExtractor.BuildCellRefResolutions` pre-builds a `Dictionary<compId, CellSymbolResolution>` via `CellSymbolResolver.Resolve` and passes it to `GetEffectivePortDefs`, which replaces every old `SymbolPortDefs.For + GetPortWorldCoord` callsite (Layer-1 seeding, short-disable union, `AssignNetNames` auto-scan, `EmitCellInstance` binding, `EmitInstance` terminals). `EmitCellInstance` binding guard now compares against the **resolved pin count** (not the always-2 `SymbolPortDefs` length). 4 new hierarchy tests in `NetExtractorHierarchyTests.cs` gate the fix. Build 0W/0E; 1220 tests pass.

Phase 7.2b (data-source library) — COMPLETE: Source library now loads `.npy` via `DataSetImporter` alongside Touchstone (via `TouchstoneIO` + `DataSetBuilder.FromSnp`). Each `SnpEntryViewModel` carries a `DataSet? Data` (unified payload) and `SNP? Snp` (S-param facet — non-null for Touchstone and `.npy`-with-S; null for cube-only `.npy`). `.npy`-with-S: `DataSetBuilder.ToSnp(data)` exposes an `SNP` for the existing picker; `.npy`-without-S: `Snp = null` (not pickable until 7.2c). `SourceKind {Touchstone, Npy}` enum routes `LoadFileAsync`, `ReloadAsync`, `RestoreBrokenEntry`, `AddBrokenEntry`. `IsBroken => _snp?.IsEmpty ?? false` (null Snp is NOT broken). Command properties use `{ get; private set; } = null!` (assigned in `InitCommands`). File-picker updated to "Data Files" (Touchstone + .npy). `SnpLibraryView.axaml.cs` drop handler uses `e.IsBroken` + `e.FilePath`. Naming debt: `Snp{Library,Entry}ViewModel` rename to `DataSource*` deferred to 7.2c. Build 0W/0E; 1211 tests pass. S-param gate met (`.npy`-with-S pickable + plottable via existing SNP machinery). **Next: 7.2c** (cube-native trace path for non-S cubes + identity components + minimal labels + class rename).

Pre-7.2 cleanup (Skia Plex glyph fallback) — COMPLETE: Data Display renderers now use a per-glyph DejaVu fallback for any code point IBM Plex Sans lacks. New `DataDisplay/Renderers/RendererText.cs` adds `DrawLeftTextWithFallback` + `MeasureTextWithFallback` (splits text into Plex/DejaVu runs via `SKTypeface.GetGlyph`). `TableRenderer` uses these for all trace-data cells (where `∠` U+2220 appears in MA/DB format) and builds matching DejaVu fonts in `Draw()` and `CalcFitWidth()`. Table sort-direction arrow (▲/▼) is now an `SKPath` drawn just right of the freq header text (`DrawSortArrow`) — the glyph characters are gone from both `DrawHeaderRow` and `CalcFitWidth`. `MarkerRenderer.DrawInfoBox` uses `DrawLeftTextWithFallback` per line; `MeasureInfoBox` uses `MeasureTextWithFallback` so info-box sizing accounts for real `∠` width. IBM Plex remains the primary font for all other text. Build 0W/0E; 1206 tests pass. **Next: 7.2** (DataSet as trace data source).

Phase 7.1d-3b (stale-marker guard) — COMPLETE: `MarkerEditorViewModel` gains `private bool MarkerIsLive => _parent is not null && _parent.Trace.Markers.Contains(_marker)`; all nine model-mutating paths (`OnNameChanged`, `OnMatrixFormatChanged`, `OnStyleChanged`, `OnDigitsChanged`, `OnUseNormalizedChanged`, `OnFormatStringChanged`, `OnIsMultiChanged`, `OnIsDeltaChanged`, `CommitFrequency`) start with `if (!MarkerIsLive) return;`. Edits to a detached marker (Ctrl+Z removed it) are silently dropped; after Ctrl+Shift+Z redo the same instance is live again and edits resume. Read-only display properties unchanged. Build 0W/0E. **7.1d-3 COMPLETE. Next: 7.2** (DataSet as trace data source).

Phase 7.1d-3a (MarkerEditorView restyle) — COMPLETE: `MarkerEditorView.axaml` restyled to match the `PlotInspectorView` idiom. Outer card (`SystemChromeMediumLowColor`, CornerRadius=8, Padding=10); all labels use `TextBlock.label` (FontSize 10, Opacity 0.6); `UserControl.Styles` block copies the compact `TextBox`/`ComboBox`/`NumericUpDown` styles and the `ToggleButton.seg-btn` family (idle + `:checked` accent on `/template/ ContentPresenter`, Light1/Dark1 hover/press). Data readout block wrapped in a `Border`(`SystemChromeLowColor`, `CrfTileBorderBrush`, CornerRadius=6, Padding=8,6) — card look separating read-only values from editable fields; secondary lines at 0.55 opacity. Normalize `CheckBox` → `ToggleButton.seg-btn` ("Norm Z"); Multi/Δ `CheckBox`es → two `ToggleButton.seg-btn`s ("Multi" / "Δ"). Width=240→250. All VM bindings preserved; code-behind unchanged. Build 0W/0E.

Phase 7.1d-2 (plottype + label strip rebuild) — COMPLETE: `PlotInspectorViewModel` gains `PlotStructureChanged` event; raised from `OnPlotTypeChanged`, `AddTrace`, `RemoveTrace`, `OnTraceSecondaryAxisChanged`, and `OnLibraryChanged` (not from appearance/text handlers). `PlotContainerViewModel` constructor subscribes `Inspector.PlotStructureChanged += (s, e) => UpdateLabelStrips()` immediately after the `PlotNeedsRedraw` subscription. Switching PlotType (e.g. Smith → Table) now clears label strips immediately; add/remove trace and →R toggle update strip count/side live. Appearance changes (color/line/slider drags) remain on the revision-bump path with no flicker. Build 0W/0E. **Next: 7.1d-3** (marker editor polish).

Phase 7.1d-2 (combo shrink + label redraw) — COMPLETE: Two PlotInspector follow-ups. (A) All three trace-card rows (identity/line/symbol) now use explicit `<Grid.ColumnDefinitions>` with priority-shrink star sizing: col1 = `Width="*" MinWidth="54"` (signal combo / NUD+slider), col2 = `Width="1000*" MinWidth="40" MaxWidth="95"` (dB/Mag/Phase / color combos). At wide widths col2 pins to 95 and col1 takes the rest; as the inspector narrows, col1 shrinks to its 54 floor first, then col2 releases and shrinks. Signal combo gains `MinWidth="20"`; both sliders gain `MinWidth="20"`. (B) Live label-strip redraw fix: `LabelStripViewModel` gains `[ObservableProperty] int _appearanceRevision`; `AxisLabelControl` gains an `AppearanceRevision` direct property whose setter calls `InvalidateVisual()`; both `AxisLabelControl` instances in `PlotContainerView.axaml` bind `AppearanceRevision="{Binding AppearanceRevision}"`; `PlotContainerViewModel` constructor now bumps `st.AppearanceRevision++` on every `PlotNeedsRedraw` for all left and right strips. Build 0W/0E. **Next: 7.1d-3** (marker editor polish).

Phase 7.1d-2 (width-flex follow-up) — COMPLETE: PlotInspectorView is now width-flexible. (1) `PlotInspectorView.axaml`: `Width="430"` → `MaxWidth="430"` (control stretches to fill its host, capped at 430). (2) `DataDisplayView.axaml` inner `Border`: added `Width="430"` so the flyout/docked inspector renders at exactly 430 as before. (3) `PropertiesView.axaml` `ScrollViewer`: `HorizontalScrollBarVisibility="Auto"` → `Disabled` so the viewport constrains the inspector to the dock width rather than giving it unbounded measure room. Build 0W/0E. **Next: 7.1d-3** (marker editor polish).

Phase 7.1d-2 — COMPLETE: PlotInspectorView hosted in the Properties dock as a fourth context. (1) `PropertiesTool` gains `IsDataDisplayActive` + `PlotInspectorVm` (`[ObservableProperty]`) and `SetActiveDataDisplay(PlotInspectorViewModel?)` — clears all other contexts, sets header "Plot"/"Properties". `IsSchematicContextActive` now guards against all three non-schematic contexts. (2) `PropertiesView.axaml` adds `xmlns:ddv` + a `Panel IsVisible="{Binding IsDataDisplayActive}"` wrapping a `ScrollViewer` (horizontal auto) containing `ddv:PlotInspectorView DataContext="{Binding PlotInspectorVm}"`. (3) `WorkspaceViewModel` adds `_subscribedDisplayWindow`/`_displayInspectorHandler` fields and `RouteDataDisplayProperties(DataDisplayDocument?)` — subscribes to `DisplayWindowViewModel.PropertyChanged`, tracks `ActiveInspector`, calls `SetActiveDataDisplay`; unsubscribes on every non-DataDisplay activation path. `OnDocumentDockPropertyChanged` branches on `DataDisplayDocument` first; all other branches call `RouteDataDisplayProperties(null)` to unsubscribe. `OnProjectTreeSelectionChanged` guards also skip clobbering when `ActiveDockable is DataDisplayDocument`. Build 0W/0E; 1206 tests pass. **Next: 7.1d-3** (marker editor polish).

Phase 7.1f — COMPLETE: Data Display workspace/tree integration. (1) `OpenDataDisplayFileCommand` — file picker opens `.cdd` into a Content-pane tab (deduped); `NativeMenuItem` + in-window `MenuItem` added to File menu next to "Open Symbol…" / "New Data Display". (2) `WriteWorkspaceFile` persists open `DataDisplayDocument`s as `kind="datadisplay"` + covers active-doc path; `RestoreOpenDocuments` adds `"datadisplay"` case via fire-and-forget `OpenOrActivateDataDisplay`. (3) `WorkspaceScanner.Scan` enumerates loose files at workspace root via `BuildFileNode` (`.cws` excluded); root-level `.cdd` → `NodeKind.DataDisplayFile`. (4) `OpenNode` `DataDisplayFile` case opens via `OpenOrActivateDataDisplay`. Refactored `OpenDataDisplayFromFileAsync` delegates to `OpenOrActivateDataDisplayCoreAsync` (stream-or-path). Build 0W/0E; 1206 tests pass.

Phase 7.1e — COMPLETE: `.cdd` layout persistence. (1) `DataDisplayConfig.CurrentFormatVersion = 1` const + `FormatVersion` property (default 1 so clipboard JSON passes); `SaveAllAsync` writes `FormatVersion = CurrentFormatVersion`; `LoadAllAsync` throws `InvalidDataException` on mismatch — no partial load. (2) `DataDisplayView.axaml.cs OnLoaded` injects `SetSaveDataDisplayAsAction`/`SetOpenDataDisplayAction`/`SetGetWindowGeometryAction`; `DoSaveDisplayAsAsync` uses `StorageProvider.SaveFilePickerAsync` (`.cdd` filter); `DoOpenDisplayAsync` uses `OpenFilePickerAsync` + `await using var stream = file.OpenReadAsync()` (macOS security-scoped); errors surfaced via `ShowErrorAsync` (reuses `SaveChangesDialog` with OK-only). (3) Toolbar: `ContentSaveOutline` → `SaveDataDisplayCommand`, `FolderOpenOutline` → `OpenDataDisplayCommand`, preceded by a separator. `Ctrl/Cmd+S`/`O` not clobbered (global workspace shortcuts untouched). Build 0W/0E; 1206 tests pass.

Phase 7.1d-1 polish R5 — COMPLETE: Single slider-thumb fix in `PlotInspectorView.axaml`. Removed `Height="20"` from the `Slider` style so Avalonia's Fluent template keeps its natural thumb-centered height; replaced with `Margin="2,-7"` (negative vertical trims the layout footprint so line/symbol rows stay tight). Added `ClipToBounds="False"` to the nested col-1 `Grid ColumnDefinitions="30,*"` in both the line and symbol rows so the thumb can't be clipped if it slightly overhangs. Build 0W/0E. **7.1d-1 inspector look is now closed out; next is 7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish R4 — COMPLETE: Six fixes to `IconSelectButton` / `PlotInspectorView` / `App.axaml`. (1) Line-glyph bug: `CrfIconBrush` (`SolidColorBrush Color=SystemBaseMediumColor`) added to `App.axaml Application.Resources`; `Line Stroke` in the line-ISB `DataTemplate` bound directly to `{DynamicResource CrfIconBrush}` (not the defunct `Button.seg-btn Canvas Line` style which couldn't reach popup visual trees); both defunct canvas-line styles removed. (2) `HighlightSelected` styled property added to `IconSelectButton` (bool, default true); `ApplyHighlight()` now gates `active` class on `Highlight && HighlightSelected`; `ApplyHighlightSelected()` adds/removes `flat-select` class on `PART_ListBox`; `flat-select ListBoxItem:selected` style in popup `Border.Styles` keeps transparent background; all three trace-card ISBs set `HighlightSelected="False"`. (3) Col 0 widened `28→34` in all three rows; ISB margins `0,0,3,0→0,0,6,0` for clear right gap. (4) Slider style `Height 35→20`; both inline `TranslateTransform Y="-7.5"` removed; row heights now uniform. (5) `Border.traceCard` style gains `BorderBrush=CrfTileBorderBrush`, `BorderThickness=1`, `CornerRadius 4→6`. (6) Trash button (`TrashCanOutline`, 14px, `Classes="removeTrace"`) in card's `Grid.Column=1` `VerticalAlignment=Top`; `Button.removeTrace` style: transparent bg, no border, `CrfIconBrush` foreground, red on `:pointerover`; old `×` button removed from Z0 row. Build 0W/0E; 1206 tests pass. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish R3 — COMPLETE: (A) `ComboBox.icon-pick` styling removed entirely. New `IconSelectButton` custom `TemplatedControl` (`src/Ui/DataDisplay/Controls/IconSelectButton.cs`) — StyledProperties: `ItemsSource`, `SelectedItem` (TwoWay default), `ItemTemplate`, `Highlight`; `OnApplyTemplate` finds PART_Button + PART_Popup + PART_ListBox; button click toggles popup; list selection sets `SelectedItem` + closes popup; `Highlight` adds/removes `active` class on PART_Button so the existing `seg-btn`/`seg-btn.active` idiom handles all visual states. ControlTheme defined in `PlotInspectorView.axaml` `UserControl.Resources` (Width=28, Height=22; Popup `Placement="Bottom"`, `IsLightDismissEnabled=True`; ListBox with inline `ListBoxItem` ControlTheme for hover/selected; Avalonia 12: `Popup.Placement` not `PlacementMode`). (B) Added `Button.seg-btn Canvas Line` style (grey) + `Button.seg-btn.active Canvas Line` style (White) so line-glyph strokes flip with accent state. (C) All three trace-card rows now share `ColumnDefinitions="28,*,95,26"` — identity row: matrix ISB(28) · signal combo(*) · YAxis(95) · →R(26); line/symbol rows: ISB(28) · nested `Grid(30,*)` with NUD+slider · color combo(95, HorizontalAlignment=Stretch) · blank(26). Slider right edge aligns with signal combo above. Color swatches use `Height="10"` (no fixed Width) + `HorizontalAlignment="Stretch"` to fill the 95-px column. Slider `Margin="2,0"`. Build 0W/0E; 1206 tests pass. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish R2 — COMPLETE: (A) Inspector icon buttons match toolbar idiom — `Button.seg-btn` default icons use `SystemBaseMediumColor` via targeted `mi|MaterialIcon` and `ctl|PlotTypeGlyphControl` styles; active state uses accent via `/template/ ContentPresenter` (Background=SystemAccentColor, Foreground=White, hover=Light1, pressed=Dark1); white icon overrides on `.seg-btn.active` ensure glyphs and custom PlotTypeGlyphControl strokes flip to White. Smith/Polar PlotTypeGlyphControl Stroke no longer bound to `$parent[Button].Foreground`; driven by styles instead. (B) `ComboBox.icon-pick` style: Width=28, Padding=2, no chevron (`/template/ PathIcon IsVisible=False` + `/template/ Path IsVisible=False`), grey/transparent background; popup items centered. (C) VM — `LineModeItem` and `SymbolModeItem` classes added to `ComboItems.cs`; `PlotInspectorViewModel.LineModes` (Off + all `LineType`s) and `SymbolModes` (Off, Circle, Square) static lists; `TraceRowViewModel.SelectedLineMode` and `SelectedSymbolMode` computed properties (get=derive from LineEnabled/LineType/MarkerEnabled/SelectedMarkerTypeItem, set=drive them); `OnLineEnabledChanged`/`OnLineTypeChanged`/`OnMarkerEnabledChanged`/`OnSelectedMarkerTypeItemChanged` all call `OnPropertyChanged(nameof(SelectedLineMode/SelectedSymbolMode))`. (D) Trace card — line row and symbol row now `ColumnDefinitions="Auto,30,*,Auto"` (4 cols): icon-pick + NUD + slider + color-combo Width=52; separate enable toggles and style/shape combos removed (2 fewer combos per card). MatrixType identity-row combo also uses `Classes="icon-pick"` at Width=30. (E) Color combos Width=52 with 34px swatch. Build 0W/0E; 1206 tests pass. icon-pick used ComboBox restyle approach; verify chevron hiding in the running app. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish — COMPLETE: (1) Base `ComboBox` style (no class) with FontSize 10, MinHeight 0, Padding 4,1, Height 22, VerticalContentAlignment Center — all combos now uniformly compact; `ComboBox.compact` kept as no-op. (2) MatrixType Width 52, LineType Width 52, MarkerType Width 52 — no left/right clipping. (3) Line-toggle now shows live dash-pattern preview via `Canvas/Line` with `StrokeDashArray` bound to `LineType` through `LTD` converter; Symbol-toggle shows selected marker icon via `mi:MaterialIcon Kind="{Binding SelectedMarkerTypeItem.Icon}"`. (4) New `src/Ui/DataDisplay/Controls/PlotTypeGlyphControl.cs` — Avalonia `Control` with `Kind` (Smith/Polar) + `Stroke` styled properties; Polar draws 2 concentric circles + H/V axes; Smith draws outer circle + real axis + R=1 circle + X=±1 arc circles clipped to unit circle. Smith/Polar header buttons now use `PlotTypeGlyphControl` (Rect=ChartLine, Table=TableLarge unchanged). (5) Both Line and Symbol rows use `ColumnDefinitions="Auto,30,*,46,52"` — sliders are same length, columns align; Slider `Margin` reduced to `4,0`; colour combos Width=46, style/marker combos Width=52, `HorizontalAlignment="Right"` dropped. Build 0W/0E; 1206 tests pass. Owner to hand-tweak `PlotTypeGlyphControl.cs` geometry. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 — COMPLETE (pass 2): Segmented plot-type header (4 `Button.seg-btn` glyphs: `ChartLine`/`ChartArc`/`ChartDonut`/`TableLarge`, centered, `Classes.active` bound to `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot`/`IsTablePlot`, commands `SetPlotType*Command`); `+ Trace` now left-aligned in Row 2 alongside Freq + Font-Size controls; `ToggleButton.trace-toggle` style (`:checked` for active look) replaces Line/Symbol checkboxes with equal-size glyph toggles (`VectorPolyline`/`ChartScatterPlot`); `ComboBox.compact` style (FontSize 10, reduced MinHeight/Padding) applied to MatrixType/YAxis/LineType/Format combos; MatrixType ItemTemplate → letter on `Border` (SystemBaseLowColor, CornerRadius=3, 18×16); LineType ItemTemplate → `Line` glyph with `StrokeDashArray` from `LineTypeToDashArrayConverter` (new `Converters/LineTypeToDashArrayConverter.cs`); MarkerType combo shrunk to Width=40; `PlotInspectorViewModel` gains `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot` bool getters + `OnPlotTypeChanged` notifies all four + four `SetPlotType*Command` relay commands; build 0W/0E; 1206 tests pass. Smith/Polar icons are `ChartArc`/`ChartDonut` fallbacks — flagged for owner review. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1c COMPLETE — splotRF Data Display engine ported (canvas + containers + tabs + toolbar + SNP library + Load Touchstone + flyout/docked inspector), splotRF-styled. Summary of 7.1c-3b additions: `TabHeaderView.axaml(.cs)` (double-click rename, close button); `SnpLibraryView.axaml(.cs)` (header with import button, drop hint, entry list, drag-drop, context menu); `SnpLibraryViewModel` gains `ImportCommand` + `System.Windows.Input` using; `DataDisplayView.axaml` replaced with full chrome — `UserControl.KeyBindings` (Ctrl+Meta for Add Plot/New Tab/Remove/Zoom In-Out/Actual Size/Fit All/Undo/Redo/Load Touchstone), in-document `StackPanel.Toolbar` (ChartLine · TabPlus · sep · MagnifyPlusOutline · MagnifyMinusOutline · Magnify · FitToPageOutline · sep · Undo · Redo), `Grid 180,4,*` with `SnpLibraryView` + `GridSplitter` + `TabControl(TabStripPlacement=Bottom)` hosting `TabHeaderView` + `PlotCanvasView` per tab + docked `PlotInspectorView` gated on `IsInspectorOpen`/`HasSingleSelection`; `DataDisplayView.axaml.cs` injects `SetOpenFileAction`/`SetGetCanvasSizeAction` + wires `SnpLibrary.ImportCommand`, `CopyToClipboardFunc`, `FindMissingFileAsync` on `Loaded`; `DataDisplayDocumentViewModel` stripped to `Window + IsDirty` (demo seed removed); build 0W/0E; 1206 tests pass. Next: **7.1d** (inspector restyle to the §2.8 merge + dual surface + marker polish).

Phase 7.1c-3a — COMPLETE: real canvas + containers + provider wiring (single tab); `PlotContainerView` ported (`src/Ui/Views/DataDisplay/PlotContainerView.axaml(.cs)`) with full move/resize/select code-behind and all four provider wires (`NextMarkerIndexProvider`/`FindMarkerInfoBoxVmProvider`/`ContainerProvider`/`SelectedMarkersProvider`); splotRF `DataDisplayView` ported and renamed `PlotCanvasView` (avoids collision with document-level `DataDisplayView`), with middle-pan/drag-select/scroll-zoom/background-deselect code-behind; `DataDisplayDocumentViewModel` now wraps `DisplayWindowViewModel` as `Window` property; 7.1b `CurrentPlot`/`HasPlots`/`InsertDemoPlotCommand` harness removed; `SeedDemoPlot()` seeds one Rect S21-dB plot into the first tab (TEMP 3a); document `DataDisplayView` hosts `PlotCanvasView` bound to `ViewModel.Window.ActiveTab` + temp "Add Plot" button; build green 0W/0E; 1206 tests pass.

Phase 7.1c-2 — COMPLETE: 7.1b render-only `PlotControl` replaced with splotRF's full interactive version (pan left-drag, zoom Ctrl+scroll, context menu, flyouts); `AxisLabelControl`, `DragSelectOverlay`, `DoubleToDecimalConverter` ported; five flyout/overlay views ported (`PlotInspectorView`, `AxesLimitsView`, `AxesLabelsFlyout`, `MarkerEditorView`, `MarkerInfoBoxView`); `PlotExporter` ported with `"circuitRF.pdf"/"circuitRF.svg"` app formats; harness updated with `ContentGrid`, `EnablePanning="True"`, `DoubleTapped→HandleDoubleTapAt`; canvas.Clear bug fixed (only clears when `_plot is null`); build green 0W/0E.

Phase 7.1c-1 — COMPLETE: splotRF view-model stack faithfully ported to `src/Ui/DataDisplay/ViewModels/` (namespace `CircuitRF.Ui.DataDisplay.ViewModels`); 21 VM files created; 3 model files added (`AppSettings.cs`, `DataDisplayConfig.cs`, `UndoRedo.cs`); `DataDisplayDocumentViewModel` rename complete; `RfCore.csproj` extended with `InternalsVisibleTo CircuitRF.Ui` for `SNP.CreateBroken`/`RefreshFrom`; `DisplayWindowViewModel.PerformCopy` stubbed (`// TODO 7.x`); build green 0W/0E; smoke tests pass.

Phase 7.1b — COMPLETE: splotRF plot model (`Misc`, `Axes`, `Marker`, `Plot`, `Trace`) + Skia renderers (`RenderTheme`, `PlotRenderer`, `AxesRenderer`, `TraceRenderer_MarkerRenderer`, `TableRenderer`) ported to `src/Ui/DataDisplay/`; font seam retargeted to IBM Plex (`SkiaFonts.PlexRegular`/`PlexBold`); color seam picks `RenderTheme.Light`/`Dark` from `ActualThemeVariant`; render-only `PlotControl` in `src/Ui/DataDisplay/Controls/`; demo `InsertDemoPlot` harness seeds a synthetic S21-in-dB Rect plot; build green.

Phase 7.1a — COMPLETE: `DataDisplayDocument`/`DataDisplayViewModel` (`src/Ui/DataDisplay/`), `DataDisplayView` (`src/Ui/Views/DataDisplay/`), `NewDataDisplayCommand` on `WorkspaceViewModel`, DataTemplate in `App.axaml`. New Data Display opens an `Untitled-Display-N` tab with an empty placeholder canvas; tears off and re-docks; closes cleanly; Ctrl+Shift+D / ⌘⇧D shortcut wired.

Standing instructions for `src/Ui`. Read with the root `CLAUDE.md`, the interaction spec
`docs/design/ui-design.md`, and the architecture/firewall note `docs/design/ui-architecture.md`. The UI is
how people drive the engine; it must never become the source of truth for simulation.

---

## Testing without the Avalonia runtime

Unit tests in `tests/Ui.Tests/` must be framework-free (no Avalonia app host, no UI thread). Here is what can and cannot be instantiated.

**Constructable without Avalonia:**
- `SchematicViewModel`, `SchematicEditModel`, `SchematicDocument`, `SymbolEditorDocument`, `SchematicSessionRegistry` — all pure C#, no Avalonia dependency.
- `DisplayWindowViewModel`, `DataDisplayDocumentViewModel`, `DataDisplayDocument` — confirmed by `DataDisplayVmSmokeTest.cs`. `DisplayWindowViewModel.SaveAllAsync(path)` is plain async disk I/O and works in tests; it also sets the dirty baseline via `CaptureBaseline()`. `HasUnsavedChanges()` is synchronous.
- `SchematicPersistence.SaveToFile` / `LoadFromFile` — disk I/O only.

**Requires Avalonia runtime (cannot be directly unit-tested):**
- `WorkspaceViewModel` — constructor calls `new CircuitRfDockFactory()`, `CreateLayout()`, `InitLayout()`, and posts to `Dispatcher.UIThread`. Never instantiate in tests.
- `SaveChangesDialog` and any `Window` subclass — require the Avalonia app host.
- Any `WorkspaceViewModel` method that calls `dlg.ShowDialog(owner)`.

**Pattern for testing `WorkspaceViewModel` logic:** use the "simulate" pattern — write a private static helper in the test class that replicates the relevant production logic using real types (empty lists are fine). See `HierarchySaveTests.SimulateSingleDocSave` and `CloseQuitSaveTests.PromptSaveBeforeClose_OrphanedOnly_NoCrash` for examples.

**`DataDisplayDocumentViewModel.IsDirty` is NOT wired to live edits** — nothing propagates `DisplayWindowViewModel.HasUnsavedChanges()` into it. The close/quit pipeline (and any test checking dirty state) must call `docVm.Window.HasUnsavedChanges()` directly. A brand-new `DisplayWindowViewModel` returns `false` from `HasUnsavedChanges()` until `SaveAllAsync` or `LoadAllAsync` has been called once to establish a baseline.

---

## Keyboard shortcut routing — focus-independent tunnel handler (SchematicView + SymbolEditorView)

**The problem:** Toolbar `Button` clicks steal keyboard focus from the canvas. `Window.KeyBindings`
(e.g. `<KeyBinding Gesture="Escape" Command="{Binding DisarmPlacementCommand}"/>`) are processed
**before** visual-tree routing begins and always mark `e.Handled = true`. A plain `protected override
OnKeyDown` on the `UserControl` is registered without `handledEventsToo`, so it is silently skipped after
the Window KeyBinding runs. A `KeyDown +=` handler on the canvas is also skipped (canvas is not in the
bubble path from a sibling toolbar button).

**The fix — one authoritative tunnel handler per editor view:**
```csharp
// In the UserControl constructor:
this.AddHandler(
    InputElement.KeyDownEvent,
    OnViewKeyDownTunnel,
    RoutingStrategies.Tunnel,
    handledEventsToo: true);
```

- `RoutingStrategies.Tunnel` fires **before** the focused element processes the key, so the View claims
  Esc/S/W/F/Z first and marks them handled — the canvas's bubble handler then naturally skips them (no
  double-processing).
- `handledEventsToo: true` fires even when `e.Handled` is already `true` (the Window KeyBinding pre-mark).
- Gate with `IsKeyboardFocusWithin` so the handler is a no-op when focus is on a different panel
  (Properties, Project Tree, etc.).
- Gate with `InlineEditBox.IsKeyboardFocusWithin` (schematic) so the inline TextBox keeps its own
  Esc/Enter behaviour.

**Schematic editor** (`SchematicView`): owns Esc (→ SetSelectTool or Selection.Clear), S, W, Z, F.
**Symbol editor** (`SymbolEditorView`): owns Esc (delegates to `vm.OnKeyDown` which handles text/pin/general
modes), S (→ SetActiveToolCommand "Select"), F (→ ZoomToFit).

**Do NOT add a `protected override OnKeyDown` on these views** — it is a dead path after a toolbar click
and causes double-handling if the tunnel handler is also present. The tunnel handler IS the single
authoritative path; the canvas's `KeyDown` handler remains for canvas-specific keys (Ctrl+C/X/V, F5,
Delete, R, nudge).

### Select All (Ctrl/Cmd+A) is per-editor and focus-gated — NO window-level binding (2026-06-25)
There is intentionally **no** window-level Ctrl+A binding and **no** Edit→Select All menu (both removed —
the menu's command was a dead no-op and its `InputGesture="Ctrl+A"` risked hijacking Ctrl+A in a docked
panel's text box). Each editor owns Ctrl/Cmd+A, fired only when that editor has keyboard focus, checking
`(Control | Meta)` so Cmd works on macOS:
- **Schematic** — `SchematicCanvas.OnKeyDown` → `vm.OnKeyDown` → `Key.A when ctrl` → `SelectAll()` (components +
  wires + canvas objects). Focus-gated because the canvas only receives keys when focused.
- **Symbol** — `SymbolEditorView` tunnel handler: `ctrl && Key.A && !IsTypingText` → `vm.SelectAll()` (all
  `EditableSymbol.Primitives`). Gated by `IsKeyboardFocusWithin`; suppressed while typing a text primitive.
- **Data Display** — `DataDisplayView.axaml` `Ctrl+A`/`Meta+A` KeyBindings → `Window.SelectAllCommand` →
  `DataDisplayViewModel.SelectAll()` (everything selectable: all plot containers **and** all marker info
  boxes). A focused `TextBox` inside the view consumes Ctrl+A first (select-all-text), so the binding doesn't
  hijack it; the Properties inspector lives in a separate dock, so its text boxes are unaffected.
Tests: `SymbolEditorViewModelTests.SelectAll_SelectsEveryPrimitive`, `DataDisplaySelectAllTests`. Ui 1539.

### Editor view grabs keyboard focus on tab activation (2026-06-25)
Bug: after switching Content tabs, shortcuts (Select All, nudges) didn't work until the user clicked the
canvas — the activated view had no keyboard focus. Fix via `IActivatableDocument` (`src/Ui/Commands/`):
`{ event ActivationFocusRequested; RequestActivationFocus(); ConsumeActivationFocus(); }`, implemented by
`SchematicDocument`/`SymbolEditorDocument`/`DataDisplayDocument` (sets a pending flag + raises the event).
`WorkspaceViewModel.OnDocumentDockPropertyChanged` (the canonical tab-switch hook — views stay realized, so
`OnAttachedToVisualTree` does NOT reliably re-fire on tab-switch) calls `RequestActivationFocus()` on the new
`activeDockable`. Each editor view, in its `DataContextChanged`, subscribes to `ActivationFocusRequested`
(focus when already bound) **and** checks `ConsumeActivationFocus()` (focus when it binds AFTER the request —
first open, view built on the next layout pass). Focus is deferred via `Dispatcher.Post(Background)` and
targets the canvas (`SchematicCanvasCtrl`/`SymbolEditorCanvasCtrl`) or — for the data display — the
`DataDisplayView` itself (`Focusable=true`, so its `UserControl.KeyBindings` fire). Contract test
`ActivationFocusTests`.

---

## Library Palette — catalog metadata + LibraryCatalog projection (Step 1 — done, updated for multi-category)

**`ComponentTypeRegistry`** (Avalonia-free, `src/Ui/Schematic/ComponentTypeRegistry.cs`) carries
**Palette metadata** on every `ComponentTypeInfo` entry:
- **`ComponentCategory`** enum — `Lumped`, `TransmissionLine`, `Microstrip`, `Sources`, `DataFiles`,
  `Terminals`, `Other`. All 11 built-ins are populated. `All`/`Common`/`RecentlyUsed` are virtual
  categories (filters in `LibraryCatalog`), not enum values.
- **`SearchTerms: IReadOnlyList<string>?`** — display name, type code, and aliases.
- **`IsCommon: bool`** — curated Common subset. True for R/L/C/V/VTone/Ground/Port.
- **`ExtraCategories: IReadOnlyList<ComponentCategory>?`** — additional categories a component belongs to.
  A component with `ExtraCategories = [TransmissionLine]` appears under both its primary category AND
  `TransmissionLine` in `ByCategory` filtering. `AllItems` still lists it once, sorted by the primary
  `Category`. Null means single-category (most built-ins). ZPort declares
  `ExtraCategories: [TransmissionLine]` as the mechanism demonstration.

**`LibraryCatalog`** (`src/Ui/Schematic/LibraryCatalog.cs`) — framework-free, headless; the single source
the Palette VM binds to:
- **`PaletteItem`** record — `{ Kind, PortCount, DisplayName, Category, SearchTerms, IsCommon, ExtraCategories }`.
  Bind to this, not `SymbolKind` directly (keeps v2 re-key catalog-internal, not a Palette rewrite).
- **`AllItems`** — stable ordered projection from registry (by primary category rank then display name).
  Multi-category items appear **once** under their primary category sort key.
- **`ByCategory(category)`** — **set-containment filter**: returns items where `Category == category` OR
  `ExtraCategories.Contains(category)`. A multi-category component appears under every category it lists.
- **`Common`** — virtual: items where `IsCommon = true`.
- **`RecentlyUsed(mru)`** — virtual: caller supplies `IReadOnlyList<SymbolKind>` (MRU list); returns items
  in that order, unknown kinds skipped.
- **`Search(query, category?)`** — case-insensitive substring over `DisplayName` + `SearchTerms` + category
  name; composes with an optional real-category filter (which uses the same set-containment).

**Developer-contribution point:** multi-category = set `ExtraCategories` in the registry entry. `ByCategory`
picks it up automatically — no Palette code changes.

Gate: 65 new tests; all 1042 tests green; firewall green.

---

## Dock 12.0.0.2 — ToolControl / DeferredContentControl tab-switch fix (FIXED app-wide)

**Root cause (historical):** `ToolControl` in Dock.Avalonia 12.0.0.2 uses `DeferredContentControl+ControlRecyclingDataTemplate` internally. On tab switch, `DeferredContentControl` retains the existing realized view and only updates DataContext. Views with `x:DataType` compiled bindings silently no-op on the wrong DataContext type → stale fallback content (e.g. "No workspace open").

**Fix (in `App.axaml`):** `CrfToolControlCachedContentTemplate` — the tool analog of `DockDocumentControlCachedContentTemplate`. Applied via `<Style Selector="dockCtrl|ToolControl"><Setter Property="Template">`. The template exactly mirrors the package ControlTheme chrome (ToolTabStrip at `DockPanel.Dock="Bottom"`; Border with `DockSurfacePanelBrush`/`DockBorderSubtleBrush`/`BorderThickness="1 0 1 0"`; PART names preserved; DockableControl wrapper) but replaces `DeferredContentControl` with a plain `ContentControl`. Avalonia re-resolves the App DataTemplate on each `Content` change, realizing the correct view for the new active dockable type.

**Both left ToolDocks are now tabbed:** `projectTreeDock` (Project Tree + Library Palette) and `propertiesDock` (Properties + Analyses). Tab switching works correctly for all pairs.

**On Dock version upgrades:** re-extract the ToolControl ControlTheme from the package source (Controls/ToolControl.axaml) and update `CrfToolControlCachedContentTemplate` in `App.axaml` to mirror the new chrome — change only `PART_ContentPresenter` (keep `DeferredContentControl → ContentControl`).

**If you add a new tool:** tabbed ToolDocks work correctly — place the new tool in whichever ToolDock makes sense for the UX. No per-tool isolation required.

---

## Dock 12.0.0.2 — tool tear-off window close crashes FactoryBase.CloseDockable (FIXED via CrfHostWindow)

**Symptom:** Tearing a **tool** panel (Properties, Analyses, Project Tree, Library/Palette) out into its own
floating window and then closing that window — either via the window's OS close box, or by closing the tool
tabs one-by-one down to empty — crashed the app with an unrecoverable `NullReferenceException` thrown from
`Dock.Model.FactoryBase.CloseDockable`. Document tear-off windows never had the problem.

**Root cause (instrumented, not guessed):** closing a tool float-out cascades closes — each child tool first
(these succeed), then the now-empty container `ToolDock`. By that final close the floating window's `RootDock`
is already stripped bare: `VisibleDockables.Count == 0`, `ActiveDockable`/`FocusedDockable == null`, and
`Window`/`Windows == null`. `FactoryBase.CloseDockable`'s window-management/collapse path dereferences one of
those nulls → NRE. The throw is **inside the library** (we don't control that code), and the exact null moves
depending on whether the close arrives as a separate empty-dock call (OS close box) or from inside the
last-tool collapse (closing tabs). Temp instrumentation in `CircuitRfDockFactory.CloseDockable` dumped the
full owner/root/window chain and confirmed the bare-floating-root state; it has since been removed.

**What did NOT work (and why it's not in the tree):** guarding `CloseDockable` to detach empty docks before
`base`, and wrapping `base.CloseDockable` in `try/catch (NullReferenceException)` + manual teardown. Both just
relocated the NRE (next time it surfaced directly in our own cleanup, posted via `Task.ThrowAsync` on the
dispatcher). Chasing a moving null through library teardown we can't see was the wrong layer. `CloseDockable`
is back to its clean original (confirm hook + `base.CloseDockable`).

**Fix (`src/Ui/ViewModels/Dock/CrfHostWindow.cs`):** a `HostWindow` subclass that overrides `OnClosing`. If
the floated layout contains any `ITool` (walks `Window?.Layout` recursively, all derefs null-guarded), it sets
`e.Cancel = true` — the tool float window's close box is **inert**; the user re-docks the panel by dragging its
tab back. Document float windows have no `ITool` in their layout, so they fall through to `base.OnClosing` and
close normally. Both host-window construction paths build it: `CircuitRfDockFactory.DefaultHostWindowLocator`
and `WorkspaceWindow.MainDockControl.HostWindowFactory` (the belt-and-suspenders pair) both `=> new
CrfHostWindow()`. This prevents the crashing entry point rather than patching the library's teardown.

**On Dock version upgrades:** re-test the tool tear-off close path (it may be fixed upstream — if so, this
workaround can be dropped and the close box restored). Verify `HostWindow.OnClosing(WindowClosingEventArgs)`,
`HostWindow.Window` (`IDockWindow`) and `IDockWindow.Layout` still exist with those names; if the floated-root
accessor was renamed, update the one line in `CrfHostWindow.FloatsAnyTool()`.

---

## Library Palette — glyph tile + inert list (Step 2 — done)

**`PaletteGlyphControl`** (`src/Ui/Controls/PaletteGlyphControl.cs`) — Skia `Control` using the
`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` pattern (mirror of `SymbolEditorCanvas`):
- Takes `Kind: SymbolKind` (styled property; `AffectsRender`).
- Calls `BuiltInSymbols.Primitives(kind).Primitives` for geometry.
- Computes glyph bbox via `SymbolGeometry.ComputeBb`; derives zoom+pan so the glyph fits centered
  with 12% padding (same math as `SymbolEditorCanvas.ZoomToFitInternal`).
- Calls `SchematicRenderer.DrawSymbol(canvas, prims, compX:0, compY:0, R0, mirrorX:false, panX, panY, zoom, theme)`
  — the exact same glyph-only call the symbol editor uses. **No second renderer.**
- Transparent background (the hosting tile supplies the tile background).
- Subscribes to `ThemeService.ThemeChanged` for reactive redraws; uses `SchematicRenderTheme.FromTheme`.

**GOTCHA — never `canvas.Clear(SKColors.Transparent)` in a transparent-overlay custom-draw op (Windows
desktop punch-through, 2026-06-25).** `SKCanvas.Clear` uses **Src** blend mode: it REPLACES the leased
region with fully-transparent pixels, erasing the tile background Avalonia already composited behind the
control. On macOS the opaque window backing masks it; on Windows the cleared pixels punch through to the
desktop (the Library Palette rendered see-through). A glyph-only overlay must draw on TOP of the existing
composited content — do not clear at all. (`PlotControl`'s `Clear(SKColors.Transparent)` is safe: it only
fires in the null-plot branch and sits over the opaque parent DataDisplay canvas, never window chrome.)

**`PaletteTile`** (`src/Ui/Controls/PaletteTile.axaml(.cs)`) — `UserControl` (DataContext = `PaletteItem`):
- Layout: `StackPanel` → square `Button` (60×60) containing a 50×50 `PaletteGlyphControl` + `TextBlock` caption.
- `IsArmed: bool` styled property exists; **step 4 drives it** (nothing drives it now).
- Tooltip: `StackPanel` with `DisplayName` (semibold) + `Category` line.
- Caption: `DisplayName` at 10pt, centered, `TextTrimming="CharacterEllipsis"`, max 68px wide.

**`PaletteTool`** (`src/Ui/ViewModels/Dock/PaletteTool.cs`) — `Tool` with `Items = LibraryCatalog.AllItems`.
Tabbed with `ProjectTreeTool` in `projectTreeDock` in `CircuitRfDockFactory` (left column, upper dock).
Title = "Library". Tab switching works correctly via `CrfToolControlCachedContentTemplate` — see Dock fix above.

**`PaletteToolView`** (`src/Ui/Views/Palette/PaletteToolView.axaml(.cs)`) — `ScrollViewer` → `ItemsControl`
with `WrapPanel`, `DataTemplate DataType="PaletteItem"` → `PaletteTile`. Inert; step 3 replaces with
column-driven grid + category header.

**Spine invariants honored:**
- Glyph-only: `DrawSymbol` draws primitives; no pin pass, no label pass called separately.
- `DrawSymbol` reused directly — no second renderer.
- Auto-scale + center via `SymbolGeometry.ComputeBb` + padding math.
- Theme-driven colors via `SchematicRenderTheme`; no literal colors in any tile code.
- `IsArmed` exists but nothing drives it; no placement wired.

Gate: all 1037 tests green; firewall green (no SKColor in framework-free models; Skia only in `src/Ui`).

---

## Library Palette — responsive grid + header (Step 3 — done)

**`PaletteTool`** extended with filter/search state and computed `DisplayedItems`:
- **`PaletteCategoryEntry`** / **`PaletteCategoryKind`** (in `PaletteTool.cs`) — category selector for
  the ComboBox; covers virtual (All/Common/Recently Used) and real `ComponentCategory` values.
- **`Categories: IReadOnlyList<PaletteCategoryEntry>`** — stable ordered list for the header ComboBox:
  All · Common · Recently Used · (real categories that have ≥1 item, in catalog sort order).
  `TransmissionLine` → "Transmission Line"; `DataFiles` → "Data Files" in display names.
- **`SelectedCategory`** / **`SearchQuery`** — `[ObservableProperty]`; drive all computed properties.
- **`DisplayedItems: IReadOnlyList<PaletteItem>`** — computed on demand via `LibraryCatalog`:
  - Real category → `LibraryCatalog.Search(query, category)` (search composes within the category)
  - Virtual + no query → `AllItems` / `Common` / `RecentlyUsed(emptyMru)` respectively
  - Virtual + query → `LibraryCatalog.Search(query)` across All (search overrides virtual filter)
  - MRU is `Array.Empty<SymbolKind>()` for step 3; persistence is step 4.
- **`HasNoItems`** / **`HasSearchQuery`** — boolean flags updated via partial callbacks; drive AXAML visibility.
- **`ClearSearchCommand`** — sets `SearchQuery = ""`.

**`PaletteToolView`** (`src/Ui/Views/Palette/PaletteToolView.axaml`) — rewritten:
- **Header** (row 0): `StackPanel` with category `ComboBox` (full-width, `SelectedItem` two-way) + search
  `TextBox` (`PlaceholderText="Search…"`, padded for overlaid icons) + magnifier icon overlay (left,
  non-hit-test) + clear `Button` overlay (right, `IsVisible="{Binding HasSearchQuery}"`).
- **Content** (row 1): `Grid` — "No matching components." `TextBlock` (visible when `HasNoItems`) +
  `ScrollViewer`(`HorizontalScrollBarVisibility="Disabled"`) → `ItemsControl` → `WrapPanel` bound to
  `DisplayedItems` (hidden when empty).

**Width-driven column count — one rule for dock + tear-off:**
`columns = max(1, floor(availableWidth / 74))` is implicit in `WrapPanel` with fixed-width tiles (68px +
6px margin). `HorizontalScrollBarVisibility="Disabled"` ensures the scroll viewer's viewport width is the
`WrapPanel`'s measure constraint. Dock default (~160px) → ~2 columns; torn-off + widened → more. No
docked-vs-floating special-case.

Gate: all 1037 tests green; firewall green.

---

## Library Palette — placement state machine (Step 4 — done)

**App-level armed state:**
- **`PendingPlacement`** (`src/Ui/Schematic/PendingPlacement.cs`) — `sealed record (SymbolKind Kind, int PortCount, SymbolRotation Rotation = R0)`. Null on the service means nothing is armed.
- **`PlacementService`** (`src/Ui/Schematic/PlacementService.cs`) — framework-free `ObservableObject`. `Pending: PendingPlacement?`. `Toggle(kind, portCount)` (arm/disarm/switch), `Disarm()`, `Rotate(clockwise)`. Owned by `WorkspaceViewModel.PlacementService`.

**Tile arming (L1):**
- **`PaletteTileVm`** (in `PaletteTool.cs`) — per-tile `ObservableObject` wrapper: `Item: PaletteItem`, `[ObservableProperty] bool IsArmed`, `ICommand ArmCommand` (calls `PlacementService.Toggle`).
- **`PaletteTool.SetPlacementService`** — subscribes to `Pending` changes; calls `UpdateArmedState()` which stamps `IsArmed` on all current tile VMs. `DisplayedItems` now returns `IReadOnlyList<PaletteTileVm>`.
- **`PaletteTile.axaml`** — `x:DataType="PaletteTileVm"`, `Button.Command="{Binding ArmCommand}"`, `Classes.armed="{Binding #TileRoot.IsArmed}"` → accent background style; bindings updated to `Item.Kind/DisplayName/Category`.
- **`PaletteToolView.axaml`** — DataTemplate `DataType` → `PaletteTileVm`; `IsArmed="{Binding IsArmed}"` on tile.
- **`WorkspaceViewModel`** — `public PlacementService PlacementService { get; } = new()`, injected into `PaletteTool` (+ after each `CreateDefaultLayout()` call), `[RelayCommand] DisarmPlacement()`.
- **`WorkspaceWindow.axaml`** — `<KeyBinding Gesture="Escape" Command="{Binding DisarmPlacementCommand}"/>`.

**Ghost-follow + rotate (L2):**
- **`SchematicViewModel.SetPlacementService`** — subscribes to `Pending`. When `Pending` non-null: activates `Tool.Place` with correct symbol/rotation/portCount on THIS canvas (also clears conflicting drag/wire state). When same kind but rotation changes: patches `Overlay.Ghost` in-place (preserves X/Y). When `Pending` null: calls `SetSelectTool()`. Called at all 4 document-creation sites in `WorkspaceViewModel`.
- **R/Shift-R rotation** — when `ActiveTool == Tool.Place` and `_placementService` is set, routes through `PlacementService.Rotate()` so ALL open canvases update simultaneously. Falls back to direct `RotateSelection` for keyboard-initiated placement (P key).
- **Cursor** — `Tool.Place` now maps to `StandardCursorType.Cross` in `SchematicCanvas.UpdateCursor`.
- **Esc** — canvas `OnKeyDown` passes Esc to VM → `SetSelectTool()` locally; `SetSelectTool()` also calls `PlacementService.Disarm()` (if `ActiveTool` was `Tool.Place`) → `Pending = null` → all canvases exit Place mode + tile un-highlights. **Critical gotcha:** the ARM state lives in `PlacementService.Pending`, NOT in the VM's `ActiveTool` enum. Setting `ActiveTool = Select` alone leaves `Pending` non-null — the tile stays highlighted and other canvases stay armed. Always clear via `Disarm()`. The feedback-loop guard: `Disarm()` is called *after* `ActiveTool` is set to `Select`, so `OnSvcPropertyChanged(Pending=null)` sees `ActiveTool != Tool.Place` and does not re-enter. `Disarm()` when `Pending` is already null is a no-op (CommunityToolkit.MVVM `SetProperty` no-change guard).

**Commit + stay-armed + MRU (L3):**
- **Stay-armed** — `HandlePlacePress` does not call `SetSelectTool()` after commit; `Tool.Place` persists; ghost continues from cursor.
- **`SchematicViewModel.ComponentPlaced`** event — `Action<SymbolKind>`, fired in `HandlePlacePress` after each commit.
- **`_placementPortCount`** — stored on VM; set by service path; used in `HandlePlacePress` for variadic types (Sdd/ZPort) so the palette-specified PortCount is honoured.
- **`AppPreferences.RecentlyPlaced: List<string>?`** — SymbolKind as string, MRU cap 12, saved in `preferences.json`.
- **`WorkspaceViewModel.OnComponentPlaced`** → `PushMruPlaced` — dedup+front+cap; calls `PaletteTool.SetMru(_recentlyPlaced)` live and saves preferences.
- **Recently-Used category** — `PaletteTool.SetMru` sets `_mruList`; `ComputeRawItems` uses it for `RecentlyUsed` category; live-updated on each commit.
- **Connectivity on commit** — reuses existing on-`P` union via `BuildRenderModel` after `PlaceComponentCommand.Execute`. No second connectivity path.

**Scope fence:** arm/ghost/rotate/commit/connect/stay-armed/MRU only. Drag-and-drop is step 5.

Gate: all 1037 tests green; firewall green.

---

## Library Palette — system drag-and-drop (Step 5 — done)

**DnD and click-arm converge on ONE commit:**
- **`SchematicViewModel.CommitPlacement(kind, portCount, rotation, worldX, worldY, mirrorX=false)`** —
  extracted shared commit: places `EditableComponent` (auto-name + defaults), runs the on-`P` connectivity
  union (`BuildRenderModel` after `Execute`), one undoable `PlaceComponentCommand`. Both click-arm
  (`HandlePlacePress`) and DnD drop call it — no duplicated commit logic.
- **`SchematicViewModel.CurrentPlacementRotation`** — public property exposing `_placementRot` so the
  canvas drop handler can use the last-used rotation for the drop.

**Drag payload:**
- **`PaletteDragPayload(SymbolKind Kind, int PortCount)`** (`src/Ui/Schematic/PaletteDragPayload.cs`) —
  `sealed record` carrying the catalog item. Holds:
  `static readonly DataFormat<PaletteDragPayload> Format = DataFormat.CreateInProcessFormat<PaletteDragPayload>("circuitrf/palette-item")`
  (Avalonia 12 in-process typed DnD format). Two instances created with the same identifier string compare
  equal (`DataFormat` equality is identifier-based), so source and sink can independently reference it.

**Canvas drop target (Layer 1):**
- `DragDrop.SetAllowDrop(this, true)` + `AddHandler(DragDrop.DragOverEvent, ...)` + `AddHandler(DragDrop.DropEvent, ...)` in
  `SchematicCanvas` constructor.
- `OnPaletteDragOver`: `e.DataTransfer.Formats.Contains(PaletteDragPayload.Format)` → `DragDropEffects.Copy`;
  all other formats → `DragDropEffects.None` (foreign drags silently ignored).
- `OnPaletteDrop`: reads payload via `foreach (var item in e.DataTransfer.Items) { item.TryGetRaw(Format) }`;
  calls `CommitPlacement` at the snapped drop world point with `_editContext.CurrentPlacementRotation`.

**Avalonia 12.0.3 DnD API (changed from older Avalonia — reference this on any DnD work):**
- `DragEventArgs.DataTransfer: IDataTransfer` (NOT `e.Data`; `IDataObject` was removed)
- `IDataTransfer.Formats: IReadOnlyList<DataFormat>` / `IDataTransfer.Items: IReadOnlyList<IDataTransferItem>`
- `IDataTransferItem.TryGetRaw(DataFormat) → object?`
- `DataFormat.CreateInProcessFormat<T>(string identifier)` — in-process typed format (no serialization)
- `DataTransfer` (concrete) / `DataTransferItem` (concrete): `item.Set(DataFormat<T>, T)` then `transfer.Add(item)`
- `DragDrop.DoDragDropAsync(PointerPressedEventArgs, IDataTransfer, DragDropEffects)` (NOT `DoDragDrop`)

**Tile drag source (Layer 2):**
- **`PaletteTile.axaml.cs`** — `PointerPressed` stores the event args; `PointerMoved` detects 5 px
  threshold (Euclidean), clears stored args, builds `DataTransferItem.Set(Format, PaletteDragPayload)` +
  `DataTransfer.Add(item)`, calls `await DragDrop.DoDragDropAsync(pressArgs, transfer, Copy)`.
  `PointerReleased` clears stored args. `DataContext` is `PaletteTileVm`; payload is `vm.Item.Kind` +
  `vm.Item.PortCount`.

**Invariants:**
- Last-used rotation for drop — raw OS drag can't rotate mid-drag; `CurrentPlacementRotation` is the single source.
- Click-arm unaffected — DnD is purely additive; the step-4 arm/ghost/rotate/Esc path is unchanged.
- Foreign drags silently rejected (`DragDropEffects.None` in `DragOver`; payload null-check in `Drop`).
- Drop works on any open schematic (all canvases are registered drop targets independently).

Gate: all 1037 tests green; firewall green.

---

## Library Palette — ghost pins + DnD ghost + grid polish (Polish step — done)

**Ghost shows pins (L1):**
- **`PlacementGhost`** (`src/Ui/Schematic/SchematicOverlay.cs`) gains `int PortCount = 2` — carries the
  port count for variadic devices (ZPort/Sdd) so the renderer knows how many pins to draw.
- Both `PlacementGhost` construction sites in `SchematicViewModel` pass `_placementPortCount` (already tracked
  since step 4).
- **`SchematicRenderer.DrawOverlay`**: after `DrawSymbol` for the ghost body, iterates
  `SymbolPortDefs.For(ghost.Symbol, ghost.PortCount)` and draws a small solid square at each port via the same
  `LocalToPixel` transform. Uses `PortBoxHalf` for size, `theme.GhostBody` color, no path effect (body is
  dashed; pins are solid). Rotation moves pins correctly (the same `LocalToPixel` math that `DrawPortMarkers`
  uses). Tiles remain glyph-only; pin squares are on the schematic ghost only.

**DnD ghost follows cursor (L2):**
- **`SchematicCanvas.OnPaletteDragOver`** now extracts the payload (`TryGetRaw`) on every drag-over event and
  sets `_editContext.Overlay = overlay with { Ghost = new PlacementGhost(sx, sy, kind, rotation, mirrorX=false, portCount) }`,
  snapped to `EditModel.SnapToGrid`. Ghost is invalidated each tick → ghost (with pins) follows the cursor.
- **`DragLeaveEvent` handler** (`OnPaletteDragLeave`) added — clears `overlay.Ghost` when drag exits canvas.
- **`OnPaletteDrop`** clears the ghost at the top before processing.

**Grid tightening + subtle border (L3, superseded by Fix v2 below):**
Tile `StackPanel` margin tweak to `2 3 2 3` — 1 px change, imperceptible. `Button` gains `BorderThickness=1` /
`BorderBrush=DockBorderSubtleBrush` / `CornerRadius=3`.

Gate: all 1042 tests green; firewall green.

---

## Library Palette — DnD root-cause fix + real grid tightening (Fix v2 — done)

**Root cause (Button-eats-drag):** `Button` captures the pointer on `PointerPressed` and handles the
press→move gesture for its own click mechanic. The tile's drag-source handlers lived on the outer `UserControl`,
which never received an owned press→threshold gesture because the `Button` already owned the pointer. Result:
`DoDragDropAsync` was never called, no `DragOver` fired, no ghost appeared, no drop landed.

**Fix — single pointer owner:**
- **`PaletteTile.axaml`** — `Button` replaced by a plain `Border` (`x:Name="TileGlyph"`). The `Border` is not
  a button control and does not capture the pointer. `Classes.armed` moves to the `Border`. Styles use
  `Border#TileGlyph` selectors: `SystemBaseLowColor` 1 px border (unarmed), `:pointerover` tint
  (`SystemChromeMediumColor`), `.armed` accent background + border, `.armed:pointerover` light accent.
  No `Command` attribute — arm is handled in code-behind.
- **`PaletteTile.axaml.cs`** — three handlers on the `UserControl` (events bubble from the `Border`):
  - `PointerPressed`: record `_pressArgs`, set `_dragOccurred = false`. No capture.
  - `PointerMoved`: detect 5 px threshold; set `_dragOccurred = true`, clear `_pressArgs`, call
    `DoDragDropAsync(savedArgs, transfer, Copy)`.
  - `PointerReleased`: if `_pressArgs` is still set (no drag) → call `vm.ArmCommand.Execute(null)` (arm toggle).
  All `Console.Error` `[DnD]` instrumentation removed.
- **`SchematicCanvas.cs`** — all `Console.Error.WriteLine` DnD logs removed from `OnPaletteDragOver` and
  `OnPaletteDrop`. Canvas-side drop handling was already correct.

**Real grid tightening:**
- `StackPanel Width="60"` (was 68), `Margin="1 2 1 2"` (was 2 3 2 3) → slot = 62 px (was ~74 px; visibly tighter).
- `Border` 52×52 px (was `Button` 60×60), glyph 44×44 (was 50×50). Column rule: `floor(availableWidth / 62)`.
- Border brush `SystemBaseLowColor` (guaranteed system resource, resolves in all Avalonia themes; renders visibly).

**Key gotcha (do not reintroduce):** never wrap the tile glyph in a `Button` or any pointer-capturing control.
`Button` (and `ToggleButton`) capture the pointer on press — this consumes the press→move gesture before
`DoDragDropAsync` can be called, silently killing the drag. Use a plain `Border` or `Panel` for the clickable
area and handle arm + drag entirely in pointer handlers on the `UserControl`.

Gate: all 1042 tests green; firewall green.

---

## Library Palette — macOS DnD crash fix + visible border (DnD crash fix — done)

**Root cause (macOS NSPasteboard crash):** `DataFormat.CreateInProcessFormat<T>(...)` is an in-process-only
format. On macOS, a real system drag goes through NSPasteboard — but an in-process format writes nothing to
it. AppKit detects a drag image with 0 pasteboard items and throws an uncaught NSException → app terminates.
The crash trace: `NSDraggingSession … 'There are 0 items on the pasteboard, but 1 drag images'`.

**Fix — text pasteboard format (mirrors SchematicClipboard):**
- **`PaletteDragPayload.cs`** — the `Format = DataFormat.CreateInProcessFormat<...>` field is **removed**.
  Instead the record adds `Serialize() → "circuitrf-palette:{Kind}:{PortCount}"` and
  `static bool TryParse(string?, out PaletteDragPayload)` that accepts **only** strings with the
  `circuitrf-palette:` prefix (foreign-text guard — random text drags are ignored silently).
- **`PaletteTile.OnTilePointerMoved`** — `transferItem.Set(DataFormat.Text, payload.Serialize())` instead
  of the in-process format. Everything else unchanged.
- **`SchematicCanvas.OnPaletteDragOver` / `OnPaletteDrop`** — reads `TryGetRaw(DataFormat.Text)`, calls
  `TryParse`; strings without the prefix → `DragDropEffects.None` / ignored.

**Tile border visibility fix:** `SystemBaseLowColor` (~12% opacity) replaced with `SystemBaseMediumLowColor`
(~38%) in `PaletteTile.axaml` → tile borders are now visibly readable in light and dark without fighting
the armed accent.

**Critical rule — palette DnD must use a platform pasteboard format:**
`DataFormat.Text` (or a `DataFormat.CreateBytesPlatformFormat` bytes format) writes to the native pasteboard.
`DataFormat.CreateInProcessFormat<T>` does NOT — it crashes macOS system DnD. The working pattern for DnD
payloads is a prefix-guarded serialized string on `DataFormat.Text`, exactly like `SchematicClipboard`.

Gate: all 1042 tests green; firewall green.

---

## Library Palette — tile border fix + T-junction drag-follow (BorderAndDragFollow — done)

### Color-vs-Brush on BorderBrush (GOTCHA — do NOT reintroduce)
`BorderBrush` requires a **Brush** object. `{DynamicResource System*Color}` keys (including
`SystemBaseLowColor`, `SystemBaseMediumLowColor`, etc.) resolve to a `Color` struct, NOT a
`SolidColorBrush`. Avalonia's `Background` property has an implicit `Color`→`Brush` conversion;
`BorderBrush` does **not** — the assignment silently produces no border regardless of which `*Color` key
is used, no error is raised. **Rule:** always use a `SolidColorBrush` application resource for
`BorderBrush`. `CrfTileBorderBrush` (`#55808080`, 33% opacity neutral gray, defined in `App.axaml`) is
the palette tile border resource. Do NOT reference `System*Color` keys on `BorderBrush` anywhere.

### Pin-on-wire-body drag-follow (T-junction follow — done)
When a component pin is placed on a wire's mid-span (T-junction), both the live drag path and the commit
path now re-route that wire so the connection survives the move:
- **Detection:** `PointOnSegmentInterior` + `ConnectTolerance` — the same single connectivity predicate
  used by `BuildRenderModel.IsConnected`. No second predicate.
- **Re-route:** `RouteBodyFollow(orig, nx, ny)` in `SchematicViewModel` — routes `orig[0] → P' → orig[^1]`
  via two `OrthogonalRoute` legs stitched at the new port position, then `SimplifyWirePoints`. Mirrors
  `RouteStem` (the wire-segment stem-follow re-route); do NOT invent a parallel re-router.
- **Undo:** folded into the same `MoveCommand` (`followWireSnaps`) so one Undo restores the component and
  every followed wire. Mirrors the existing endpoint-follow.
- **Perf:** O(N × S); only checks `_dragUnselectedWirePoints` (snapshotted at drag-start). No O(N²) scan.
- `BuildPortMoves` was fixed to pass `cs.Component.PortCount` to `SymbolPortDefs.For` (was using default=2,
  which silently gave wrong ports for variadic Sdd/ZPort components).

See `docs/design/placement-connectivity-and-drag-follow.md` for the full design note.

Gate: all 1038 tests green; firewall green.

---

## Pin-on-pin connectivity detection (PinOnPinConnectivity — done)

**Root cause:** `BuildRenderModel.IsConnected` was checking only `WirePointHash` (wire vertices). Two
component ports coincident with no wire both reported Unconnected; no junction dot appeared.

**Fix — single connectivity source extended to ports:**
- **`IsConnected`** now uses `conPointCounts >= 2`: the tested port contributes 1 to its P-cell's count;
  `cnt >= 2` means something else (another port OR a wire vertex) is also there → connected.
  The wire-body fallback scan (`PointOnSegment`) is unchanged for the rare port-on-wire-body case.
- **Port-coincidence dot pass** added at the end of `ComputeConnectivityGeometry`: after the wire
  auto-dot loop, iterates all component ports and emits a junction dot at any P-cell where
  `conPointCounts >= 2` OR `PointOnSegmentInterior` (port on wire body). Skips cells already covered
  by a wire auto-dot (no double-dots). Uses the same `segList`/`segIndex` and `QuantKey` already built
  in that pass — O(N), no new data structures.

**"Exclude self" invariant:** `conPointCounts[key] >= 2` is the correct threshold because every port
contributes exactly 1 to its own P-cell. A lone port has count = 1, so `>= 2` requires at least one
other endpoint. Do not use `> 0` here — that would mark a lone port as connected to itself.

**No double-dots:** `autoDotKeys.Contains(key)` skip in the port-coincidence pass ensures a P-cell
already covered by a wire T-junction or corner auto-dot never gets a second dot.

**Oracle (permanent):** `PinOnPinConnectivityTests.cs` — three headless assertions:
- `PinOnPin_BothPortsConnected_ExactlyOneDot`: two coincident ports → Connected + one dot.
- `PinOnWireVertex_PortConnected_Control`: port on wire vertex → Connected (was already correct).
- `LonePort_StaysUnconnected_NoDot`: lone port → Unconnected / no dot (anti-over-connect guard).

Gate: all 1041 tests green; firewall green.

---

## Drag invariant — auto-wire on pin-on-pin separation (DragInvariant Layer 3 — done)

The governing invariant ("a connection, once made, survives any drag") now covers all four cases:

**Case 2 (pin-on-pin → auto-wire):** When a component drag separates two pins that were in direct contact
(no wire between them), an auto-wire is created so the connection becomes a wired contact rather than
breaking.

**Implementation in `SchematicViewModel`:**
- **`PinOnPinContact` record struct** — snapshot of a pin-on-pin contact at drag start: `(StationaryX,
  StationaryY, MovingCompId, MovingPortIndex)`.
- **`_dragPinOnPinContacts: List<PinOnPinContact>?`** — cleared in `ClearDragState`; populated in
  `SnapshotDragStartPositions`.
- **Snapshot (`SnapshotDragStartPositions`):** after building `_dragUnselectedWirePoints`, iterates all
  moving component ports; skips ports already on a wire (Case 1 — handled by follow-wires); records each
  coincident pair with an unselected port. O(moving ports × wires × unselected ports).
- **Live preview (`UpdateDragOverlay`):** for each contact whose moving port has separated from the
  stationary pin, inserts a synthetic route keyed `"pop-preview-N"` into `wireOverrides` — the renderer
  draws it as a live preview wire throughout the drag. `wireOverrides ??= new()` handles the component-
  drag-only case (no wire drag in progress).
- **Commit (`CommitDragAsCommand`):** for each separated contact, builds an `EditableWire` via
  `WireGeometry.OrthogonalRoute(stationaryPin → movingPinEndPos)` and wraps it in a `PlaceWireCommand`.
  Auto-wires are chained onto the `MoveCommand` via `new CompositeCommand(finalCmd, wc)`. One Undo
  removes every auto-wire AND restores the component to its pre-drag position.
- **No-wire if still coincident:** both the preview and commit skip contacts where the drag kept the
  pins touching (drag that lands them on the same P-cell forms no wire).

**Key invariants:**
- Reuses `WireGeometry.OrthogonalRoute` (the same routing primitive as Case 1 follow-wires).
- Reuses `CoincidentPoints`/`ConnectTolerance` — no second connectivity predicate.
- `autoWireCmds` and `mergeCmd` are mutually exclusive (`mergeCmd` requires `compSnaps.Count == 0`).
- Does not drag the stationary component — only a wire is formed (no rigid coupling).

**Oracle (permanent):** `DragInvariantOracleTests.cs` — all four cases green:
- `Case1a_ComponentDrag_WireEndpointFollowsPin_StaysConnected`: endpoint follow.
- `Case1b_ComponentDrag_TJunctionBodyFollowsPin_StaysConnected`: T-junction body follow.
- `Case2_ComponentDrag_PinOnPinSeparates_AutoWireConnectsBothPins`: auto-wire on separation.
- `Case3_WireDrag_ConnectedEndpointStaysPinnedToComponentPin_StaysConnected`: wire drag pin pinned.

Design doc: `docs/design/placement-connectivity-and-drag-follow.md` (rev 5).

Gate: all 588 tests green; firewall green.

---

## Drag invariant — shared-point rule (DragFollowSharedPoint — done)

**Case 4 (shared-point disambiguation):** When a moving pin starts coincident with BOTH a stationary
component pin AND a wire endpoint (three things at one point), the stationary connection wins —
the wire endpoint must NOT follow the moving pin. A new auto-wire forms to keep the moving component connected.

**Root cause (two faults):**
1. `UpdateConnectedWireEndpointsLive` and the follow block in `CommitDragAsCommand` matched a wire
   endpoint to the moving port's ORIGINAL position via `CoincidentPoints(orig[k], ox, oy)`. A wire
   ending at the shared point coincided with the moving pin's start position, causing the endpoint to
   follow the moving component off the stationary pin.
2. `SnapshotDragStartPositions` skipped pin-on-pin recording when the moving port was "already on a
   wire" (`onWire` guard). This suppressed the compensating auto-wire even though the wire was not
   actually going to follow (fault 1 mis-attributed the follow).

**Fix in `SchematicViewModel`:**
- **`IsPointHeldByStationaryPin(x, y)`** — new private helper; returns true if any UNSELECTED component
  port coincides with (x, y) within `ConnectTolerance`. Handles selected/unselected correctly: dragging
  a component that owns the wire endpoint is unselected-free at that point, so the follow still works.
- **`UpdateConnectedWireEndpointsLive`** — added `&& !IsPointHeldByStationaryPin(orig[k].X, orig[k].Y)`
  guard to both endpoint-follow checks. Wire endpoint stays pinned when a stationary pin holds the same point.
- **`CommitDragAsCommand` follow block** — same `IsPointHeldByStationaryPin` guard on both endpoints.
- **`SnapshotDragStartPositions`** `onWire` skip — changed from `if (onWire) continue` to
  `if (onWire && !IsPointHeldByStationaryPin(wx, wy)) continue`. The pin-on-pin contact is now recorded
  (and the auto-wire formed) even when a wire endpoint is present, if a stationary pin also holds the point.

**Key invariant:** a wire endpoint held by a stationary (unselected) pin is treated as pinned, exactly
like the wire-drag `IsWireEndpointConnectedToUnselected` pinning rule. A moving pin merely starting
coincident there does not override a stationary connection.

**Preserved cases:**
- Case 1a (genuine endpoint follow): no stationary pin at the endpoint → `IsPointHeldByStationaryPin`
  returns false → follow proceeds unchanged.
- Case 2 (pin-on-pin, no wire at shared point): `onWire = false` → pin-on-pin recording unchanged.
- Case 3 (wire drag): unchanged — the stationary-pin guard is in the component-drag paths only.

**Oracle (permanent):** `DragInvariantOracleTests.cs` `Case4_SharedPoint_WireStaysOnStationaryPin_AutoWireConnectsMovingComponent`:
C1–C2 wire + C3 pin-on-pin on C1-bottom → drag C3 away → wire endpoint stays at C1-bottom, new
auto-wire (0,200)→(0,400) forms, C1/C2/C3 all Connected.

Design doc: `docs/design/placement-connectivity-and-drag-follow.md` (rev 6).

Gate: all 589 tests green; firewall green.

---

## Extraction carries enabled analyses + run executes them (Phase 6e Step 6 — done)

**`NetExtractor.Extract`** now carries the schematic's authored analyses + measurements into the emitted
`TestBench`: layer 4 copies `model.Analyses` (enabled filter: `Analysis.Enabled`) + `model.Measurements`
into `tb.Analyses`/`tb.Measurements`. The `Enabled` flag lives on the `Analysis` base class and is persisted
in `.csch` (`CschAnalysis.Enabled`), so it round-trips and gates extraction automatically.

**SP multi-segment → one flat freq array:** `CnlWriter` emits one `analysis Name type=sparam` line per
segment (same analysis name). `CnlReader` now merges consecutive segments with the same name into a single
`SParameterAnalysis` with all sweeps. At engine time, `SParameterAnalysis.Expand()` unions all segment
points into one sorted/deduped `double[]`.

**CnlReader additions (round-trip-exact for all v1 typed analyses):**
- `TryParseDcDirective`: `analysis Name type=dc` → typed `DcAnalysis` (was falling through to raw directive).
- Multi-segment SP merge: consecutive `analysis N type=sparam` lines with same name collapsed into one.
- `TryParseMeasurementLine`: extracts trailing unit token — `measure Name = expr unit` round-trips unit.
  `IsMeasurementUnit` detects bare-word units (`dB`, `V`, `%`, `dBm`, …) without false-positive on expressions.

**Run flow (no new engine code):** `RunAnalysis` → `WriteNetlist` (now includes analyses) → `RunNetlist`
dispatches typed analyses. "No analysis" message appears only when all analyses are disabled or none exist.

Gate: 8 new tests (5 `NetExtractorAnalysesTests` + 3 `CnlWriterTests`); all 977 tests green; firewall green.

---

## Analysis reuse: copy/paste + .canl templates (Phase 6e Step 5 — done)

**One serializer (§5.4):** clipboard + `.canl` + `.csch` all use `AnalysisSerialization.Serialize/Deserialize`
(for clipboard/`.canl`) and `ToDto/FromDto` (for `.csch`). Never write a second encoder.

**Copy/paste:** `AnalysesListViewModel.CopyCommand` / `CopyAllCommand` / `PasteCommand` — multi-select supported;
`PasteAnalysesCommand` appends with intra-paste collision resolution (`{name} copy`, `{name} copy 2`, …),
undoable; §5.1 unresolved-ref surfacing via `AnalysisPreviewHelper` (≈ unknown: f0).

**Templates (`.canl`):** `AnalysisSerialization.SerializeCanl/DeserializeCanl` + `CanlFile` DTO wrap the same
analysis DTOs with `Name` + optional `Description`. `TemplateManager` (framework-free, `src/Ui/Schematic/`) loads
the resolution chain workspace→user (`LocalApplicationData/circuitRF/templates/`), saves atomically, checks
existence, deletes.

**Save as Template dialog** (`src/Ui/Views/Dialogs/SaveAsTemplateDialog.axaml`): name (validated via
`NameValidator`) + description + read-only preview list of analyses to be saved + collision guard (overwrite confirm
via `SaveChangesDialog`). Saves to workspace templates dir when a workspace is open, user templates dir otherwise.
Reports path via `_schematicVm.MessageSink?.Success(path, path)`.

**Insert from Template dialog** (`src/Ui/Views/Dialogs/InsertFromTemplateDialog.axaml`): lists all `.canl` from
the resolution chain; selected template shows preview; Delete button (minimal Manage). On Insert, appends via
`PasteAnalysesCommand` (same collision resolution + §5.1 surfacing).

**Workspace dir tracking:** `WorkspaceViewModel.OnCurrentWorkspacePathChanged` → `AnalysesTool.SetWorkspaceDir(dir)`
→ `AnalysesListViewModel.SetWorkspaceDir` — workspace dir flows into template commands so they target the workspace
templates dir when a workspace is open.

**`SaveChangesDialog`** now supports `dontSaveLabel: null` to show only 2 buttons (Save + Cancel).

**TextBox vertical centering (HIG):** global `<Style Selector="TextBox">` in `App.axaml` sets
`VerticalContentAlignment="Center"` — applies to all TextBoxes app-wide.

**Double-click to edit:** `AnalysesListView.axaml.cs :: OnRowDoubleTapped` calls `vm.EditCommand.ExecuteAsync(window)`.

Gate: 956 tests green (5 new `.canl` round-trip tests in `AnalysisSerializationTests`); firewall green.

---

## Analysis Add/Edit dialog — Layer 1 (Phase 6e Step 4 — Layer 1 done)

**`src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml(.cs)`** — HIG Add/Edit dialog. Returns
`Analysis?` via `ShowDialog<Analysis?>`. Static `ShowAsync(owner, vm, isEdit)` factory handles
null-owner fallback (same `ResolveOwner` pattern). Code-behind driven (no `x:DataType`).

**Layout:** title | type picker (WrapPanel RadioButtons: DC · S-Parameter · Harmonic Balance;
Load Pull · LP Pursuit greyed + "coming soon" tooltip) | Name TextBox + inline validation |
Enabled CheckBox | swappable body panel | Cancel / OK (IsDefault, centered labels, gated on
CanCommit).

**Body panels (IsVisible by type):**
- `DcBodyPanel` — "Operating point — no additional configuration required." (DC is the novice path)
- `SpBodyPanel` — Layer 1 placeholder ("Default: 1–10 GHz, 101 pts"); Layer 2 replaces with segment sub-list
- `HbBodyPanel` — Layer 1 placeholder ("Default: f₀ = 1 GHz, 7 harmonics"); Layer 3 replaces with full form

**`AnalysisEditorViewModel`** (`src/Ui/ViewModels/AnalysisEditorViewModel.cs`) — staging VM:
- `AnalysisKind` enum (DC/SP/HB), `Type`, `Name`, `Enabled`, `NameError`, `CanCommit`
- `SpBody: SpBodyViewModel` / `HbBody: HbBodyViewModel` — per-type body VMs
- `ComputePreview(string expression)` — delegates to `AnalysisPreviewHelper` (§4.3, no fork)
- `BuildAnalysis()` — builds the staged `Analysis` on OK; null on validation failure
- `NextFreeName(kind, existing)` — generates "DC1"/"SP1"/"HB1", lowest free

**`AnalysisPreviewHelper`** (`src/Ui/ViewModels/AnalysisPreviewHelper.cs`) — static helper
reusing `DesignScope.Build + new Evaluator().Eval`, swallow-errors → empty, bare-number/blank
gates. Shared across all analysis-editor expression fields (SP segment Start/Stop in L2, HB
Tone/MaxHarm in L3).

**`SpBodyViewModel`** / **`HbBodyViewModel`** (`src/Ui/ViewModels/`) — sealed partial
`ObservableObject` stubs; `BuildSweeps()` / `BuildAnalysis()` return sensible defaults in L1.
L2 adds segments collection + commands to `SpBodyViewModel`. L3 adds all HB fields to
`HbBodyViewModel`. Both have `FromAnalysis` factory for the edit path.

**`AddAnalysisCommand`** / **`EditAnalysisCommand`** (`src/Ui/Commands/Analysis/`) — undoable
mutations. Add appends at count; Undo removes by reference. Edit stores old/new + index; Undo
restores original.

**Wiring:** `AnalysesListViewModel.Add(Window?)` / `Edit(Window?)` are now `async Task` commands.
`AnalysesListView.axaml` passes `CommandParameter="{Binding $parent[Window]}"` on all Add/Edit
buttons (toolbar + empty-state). `SetupAnalysesDialog` continues to work (same VM, modal host).

Gate: `dotnet build` / `dotnet test` green, all 951 tests pass, firewall green.

---

## Analyses panel + modal (Phase 6e Step 3 — done)

**`src/Ui/ViewModels/AnalysisRowViewModel.cs`** — wraps one `Analysis`; exposes `Enabled` (routes through
`EnableAnalysisCommand`), `Name`, `TypeLabel` ("DC"/"SP"/"HB"), `Summary` (one-liner with SI-suffixed
frequency; raw expression string for non-literal values).

**`src/Ui/ViewModels/AnalysesListViewModel.cs`** — `ObservableCollection<AnalysisRowViewModel>` for the
active schematic. `SetActiveSchematic(vm?)` rebinds on tab switch. Commands: Add/Edit (placeholder
no-ops — step 4 builds the real form), Remove, Duplicate (name-collision resolved: "{name} copy", then
"{name} copy 2", …), MoveUp, MoveDown. All mutations route through `SchematicViewModel.Execute` → undo
stack → marks document dirty. `NoActiveSchematic` / `IsEmpty` flags drive the two empty states.

**`src/Ui/ViewModels/Dock/AnalysesTool.cs`** — Dock `Tool` wrapping `AnalysesListViewModel`; Id = "Analyses".
Placed in the lower-left `propertiesDock` alongside `PropertiesTool` in `CircuitRfDockFactory`.

**Commands** (`src/Ui/Commands/Analysis/`):
- `EnableAnalysisCommand` — toggles `Analysis.Enabled`; undoable.
- `RemoveAnalysisCommand` — removes + records insertion index for undo re-insert.
- `DuplicateAnalysisCommand` — switch-expression clone for DC/SP/HB; `ResolveName` resolves collisions.
- `MoveAnalysisCommand` — swaps adjacent items; Execute/Undo swap in opposite directions.

**Views** (`src/Ui/Views/Analyses/`):
- `AnalysesListView.axaml` — toolbar + three-state body (no-schematic / empty-list / rows); rows show
  Enabled checkbox + TypeLabel badge + Name + Summary. Footer "Analyses run in listed order." when non-empty.
- `AnalysesToolView.axaml` — thin dock wrapper: `AnalysesListView DataContext="{Binding ListVm}"`.

**Dialog** (`src/Ui/Views/Dialogs/SetupAnalysesDialog.axaml`):
- Modal host for the **same** `AnalysesListViewModel` the dock uses (one VM, two hosts).
- Opened via `WorkspaceViewModel.SetupAnalysesCommand` (`Window? owner` → `ResolveOwner`).
- Bound in Simulate menu: NativeMenuItem + in-window MenuItem, both with "Setup Analyses…" label.

**Active-schematic tracking**: `WorkspaceViewModel.OnDocumentDockPropertyChanged` calls
`_factory.AnalysesTool?.SetActiveSchematic(activeVm)` after the PropertiesTool call — same pattern.

**`Analysis.Enabled`** added to `Core.Design.Analysis` base class (`bool Enabled { get; set; } = true`).
Persisted in `CschAnalysis.Enabled`; existing files without the field default to `true` on load.

Gate: 22 tests in `tests/Ui.Tests/AnalysesListViewModelTests.cs`; all 951 tests green; firewall green.

---

## Analysis persistence + shared encoder (Phase 6e Step 2 — done)

**`src/Ui/Schematic/AnalysisSerialization.cs`** — the **one encoder** (§5.4) for `Analysis` +
`Measurement` lists.  Three destinations reuse it; never write a second encoder:
- **`.csch`** (now): `CschFile.Analyses` + `CschFile.Measurements` populated via `AnalysisSerialization.ToDto/FromDto`.
- **Clipboard** (step 5): `AnalysisSerialization.Serialize(analyses, measurements) → json`.
- **`.canl` templates** (step 5): same `Serialize/Deserialize`.

**DTOs** (in `AnalysisSerialization.cs`): `CschAnalysis` (flat, type-discriminated by `Type: "dc"/"sp"/"hb"`),
`CschFrequencySpec`, `CschMeasurement`.  Enum-as-string, WhenWritingNull, Id never persisted.
Unknown type tags are silently skipped on load (forward-compat for loadpull/pursuit).

**`SchematicEditModel`** now carries `Analyses: List<Analysis>` + `Measurements: List<Measurement>`.
`CschFile` gains `Analyses: List<CschAnalysis>?` + `Measurements: List<CschMeasurement>?` (null =
omitted in file; absent on read = empty — old files load cleanly).

**`SavePlanBuilder.SchematicHasAnalyses`** returns `model.Analyses.Count > 0` (was `false`), so a
schematic carrying analyses sets `IsTestBench = true` on its cell step.

Gate: 19 new tests in `tests/Ui.Tests/AnalysisSerializationTests.cs`; all 929 tests green; firewall green.

---

## Run service + RunAnalysis wiring (Phase 6e Step 5 — done)

`src/Ui/Schematic/SchematicRunService.cs` — headless `static RunNetlist(path) → RunResult`.
Mirrors the CLI engine chain exactly: `CnlReader.ReadFile → new Elaborator(lib).Elaborate(tb)` →
dispatch each declared analysis → collect `DataSet`s. Never throws — all engine exceptions are
captured into `RunStatus.EngineError`.

**Analysis dispatch:**
- Typed: `SParameterAnalysis` (freq array from `FrequencySpec`), `HarmonicBalanceAnalysis`
  (`HbEngine.Resolve` + `new HbEngine(nl, tb).Run(p).DataSet`), `LoadpullAnalysis`
  (`LoadpullEngine.Resolve` + `Run`), `LoadpullPursuitAnalysis` (`LoadpullPursuitEngine.Resolve` +
  `new LoadpullPursuitEngine(lpEngine).Run`), `ParametricSweepAnalysis` (`ParametricSweepEngine.Run`).
  `DcAnalysis`: deferred (noted in message).
- Raw `type=sparam` directives from `RawDirectives`: parsed for `start/stop/step` with optional
  frequency-unit tokens (`GHz`, `MHz`, `kHz`, `Hz`); dispatched to `SParameterEngine.Run`.

**`WorkspaceViewModel.RunAnalysis`** (now `async Task`):
1. `WriteNetlist` → posts clickable path + extraction warnings.
2. `await Task.Run(() => SchematicRunService.RunNetlist(path))` — engine on background thread.
3. Posts `Messages.Success` / `Messages.Info` (NoAnalysis) / `Messages.Error` (EngineError).
4. Holds `_lastRunDataSets: IReadOnlyList<DataSet>` for Phase 7 (not plotted here).

**`StopAnalysis`**: informational stub — engines have no `CancellationToken` in v1; run completes.

**Scope fence:** Run → DataSet + reporting only. No results visualisation (Phase 7), no
analysis-authoring UI, no new engine code.

Gate: 4 tests in `tests/Ui.Tests/SchematicRunServiceTests.cs`; all 884 tests green.

---

## Net extractor (Phase 6e Step 1 — done)

`src/Ui/Schematic/NetExtractor.cs` — headless, framework-free `SchematicEditModel → TestBench` pass.

**Key invariants:**
- **Reuses `ComputeConnectivityGeometry`** (now `internal`) as the single source of connectivity: wire
  vertex hash, auto-dot T-junctions (`AutoDotKeys`), and dot-gated crossing predicate (`IsCrossingAtDot`).
  The extractor consumes these outputs; it does NOT re-implement T-junction or crossing logic.
- **Connection = exact on-`P` equality** — union-find is keyed by integer P-cells
  `(long)Math.Round(x/GridSize)`, not floating-point tolerance.
- **Same-name label union (§2.1.6):** labels with the same name union all their nets, even across
  physically-disjoint wires. `FindLabelNetKey` uses vertex-exact first, then `PointOnSegment` with
  `GridSize/2` tolerance for mid-segment labels.
- **Net-name priority: ground → Pin → label → auto.** A Pin component owns its net's identity — a
  net carrying a Pin is always named after the Pin, even if a user net label is also present on that
  net. The label is silently overridden (no conflict emitted); conflicts are only reported for
  label-vs-label collisions. Ground always wins over both. This ensures `Cell.Ports` (built from Pin
  names) matches the net names seen by the elaborator's port-binding step.  Implemented in
  `AssignNetNames` (`NetExtractor.cs`): ground → label loop (label-vs-label conflict detection) →
  Pin block (overrides label names; warns and skips if net is "0") → auto-name loop.
- **Terminal order is the contract:** `NetBindings[k]` = net at terminal k (symbol order). Walk
  `SymbolPortDefs.For(Symbol, PortCount)` in order. Never transpose; FetSdd is [gate, drain, source].
- **ZPort N-or-N+1 rule:** signal pins → `NetBindings`; "ref" pin → `RefNetBinding` (null if "0").
- **SDD 2N-pin rule:** the SDD schematic symbol exposes **2N pins as differential ± pairs** (pin
  order `1+,1−,2+,2−,…`), separate from ZPort's N+1 generator. Pin order is the NetExtractor
  contract matching the engine's `_v(p) = V(net[2p]) − V(net[2p+1])`. `EditableComponent.PortCount`
  for SDD remains N (signal ports); pin count is 2N. `FromRenderModel` derives SDD N as `pins/2`
  (ZPort stays `pins−1`).
- **Port special case:** schematic shows 1 pin; emits `NetBindings = [sigNet, "0"]`.
- **`ComponentTypeRegistry.EngineReference`** (new): maps SymbolKind → engine type string — differs
  from `DisplayName` for FetSdd ("FET"→"SDD"), ZPort ("Z"→"Z_Port"), ToneSource ("VTone"→"V_1Tone").
- **Ground skipped** (not emitted as instance); Open/Short honored.
- **Units glyph→ASCII normalization** applied at `EmitInstance` via `UnitNormalizer.ToEngineUnit`:
  editor glyphs (Ω, µ) are converted to ASCII engine spellings (Ohm, u) when building
  `ParameterAssignment.Unit` — the single crossing point. Editor glyphs and the engine `Units`
  table are both **unchanged**; only the emitted unit string is normalized.

Gate tests: `tests/Ui.Tests/NetExtractorLayer{1,2,3}Tests.cs` (19 tests, all green).

## Units glyph→ASCII normalization (Phase 6e Step 3 — done)

`src/Core/Expressions/UnitNormalizer.cs` — framework-free (no Avalonia, no Skia); lives in `src/Core`
so it is reachable from both `src/Core` and `src/Ui`.

**Rule:** convert at the boundary, once. The editor thinks in glyphs; the engine `Units` table is
ASCII-keyed (`Ohm`, `u`). `UnitNormalizer.ToEngineUnit(editorUnit)` is the one place the conversion
happens — called from `NetExtractor.EmitInstance` when building `ParameterAssignment` overrides.

**Substitutions (compose with any SI prefix):**
- `Ω` (U+03A9) → `Ohm`: `kΩ→kOhm`, `MΩ→MOhm`, `GΩ→GOhm`, `mΩ→mOhm`
- `µ` (U+00B5 MICRO SIGN) → `u`: `µH→uH`, `µF→uF`, `µV→uV`, `µA→uA`, `µW→uW`, `µm→um`
- `μ` (U+03BC GREEK MU) → `u`: defensive, handles alternate keyboard/font input
- Already-ASCII units (`nH`, `pF`, `Hz`, `deg`, `mil`, …) pass through unchanged
- `"None"` / empty → `""` (no unit emitted)
- Table-uncovered units (`dBm`, `V`, `A`, `W`, `kV`, `cm`, `mOhm`) emit as-is without crashing

Gate: 30 tests in `tests/Core.Tests/Expressions/UnitNormalizerTests.cs`, all green.

## Extraction oracle (Phase 6e Step 2 — done)

`src/Core/Netlist/CnlWriter.cs` — framework-free `TestBench → .cnl` text (inverse of `CnlReader`).
`tests/Ui.Tests/ExtractionOracleTests.cs` — 3 oracle tests:
- **L2 topology:** `NetExtractor.Extract` → `TestBench_extracted` topology ≡ hand-authored TestBench
  (partition-set comparison, name-agnostic). Transposition test FAILS the oracle (proves it has teeth).
- **L3 DataSet:** both extracted + authored run through `Elaborator + SParameterEngine`; DataSets match
  within 1e-9 tolerance.

The oracle is the **permanent correctness gate** for all future extraction changes.

## netlist.cnl write (Phase 6e Step 4 — done)

`WorkspaceViewModel.WriteNetlist(SchematicEditModel, string testBenchName)` — private helper;
framework-free except for `Directory.CreateDirectory` + `File.Move`.

**Destination rule:**
- Workspace open (`CurrentWorkspacePath != null`) → `Path.GetDirectoryName(CurrentWorkspacePath)/netlist.cnl`
  (workspace root directory).
- No workspace (scratch) → `RecoveryManager.SessionDir/netlist.cnl` (scratch-session dir, created lazily).

**Write flow:** `NetExtractor.Extract(model, name)` → `CnlWriter.Write(tb, header)` with provenance
header `; netlist.cnl — generated from TestBench "<name>" at <ISO-8601 UTC>` → atomic write
(temp path + `File.Move(..., overwrite: true)`).

**`RunAnalysis` command** (Step 4 wiring): resolves the active `SchematicDocument`, calls
`WriteNetlist`, posts `Messages.Success(path, path)` (clickable link) for the written path,
and posts `Messages.Warning` for each extraction conflict. **No engine run** — step 5 adds that.

Scope fence: one `netlist.cnl` overwritten each run; generated artifact, not saved-project state;
`.csch` is the source of truth.

---

## Scratch documents + New Schematic (Phase 6h Step 1 — done)

**Scratch = in-memory, no path, dirty, tree-invisible.** A scratch `SchematicDocument` is a normal
`SchematicViewModel`/`SchematicDocument` with `FilePath = null`. It is NOT in `_openDocsByPath` (which is
keyed by absolute path) and is NOT shown in the project tree (the tree reflects disk only).

### SchematicDocument scratch identity
- `FilePath: string?` — null for scratch; set to the on-disk `.csch` path for materialized docs.
  Step 2 (materialize-ancestors) will set this once at save time.
- `IsScratch => FilePath is null` — computed flag.
- `IsDirty: bool` — scratch starts `true` and stays true in step 1 (no save path yet).
  On-disk docs start `false` and flip to `true` on the first undoable edit (`CanUndo` flips).
- Tab title = `"• " + baseTitle` when `IsDirty`; plain title when clean.

### WorkspaceViewModel scratch tracking
- `_scratchDocs: List<SchematicDocument>` — open scratch docs. NOT `_openDocsByPath`.
  **NOTE (step 1):** entries are not removed when a tab is closed (no Dock close callback yet).
  The close-prompt and cleanup come in step 3. `_scratchDocs.Clear()` is called in `NewWorkspace`
  (which resets the Dock layout, so tabs are gone). `OpenWorkspace` does NOT clear it (tabs survive).
- `RebuildOpenSchematics()` iterates both `_openDocsByPath.Values` and `_scratchDocs` so scratch
  schematics re-resolve cell-ref symbols after a symbol save or Make-Primary.

### NewScratchSchematicCommand (⇧⌘N / Ctrl+Shift+N)
- Parameterless `[RelayCommand]`, always enabled — **no workspace required**.
- Creates `SchematicEditModel` → `SchematicViewModel` → `SchematicDocument(title, vm)` (null path).
- Title = next free `Untitled-Schematic-N` (lowest N not already in `_scratchDocs` or `_openDocsByPath`).
- Adds doc to `_scratchDocs`, opens via `_factory.OpenDocument(doc)`.
- Bound to File → New Schematic in the macOS NativeMenu (`Meta+Shift+N`) and in-window Menu
  (`Ctrl+Shift+N`). **New Cell (workspace-required) has no keyboard shortcut** — it was displaced.

### Launch state and action ownership

The `WorkspaceViewModel` constructor does **not** open any document. `CreateLayout()` already installs a
Welcome stub in the DocumentDock, so the app always lands on Welcome by default.

`ExecuteLaunchActionAsync(LaunchAction)` is called once at Background priority after the first window is
shown (from `App.axaml.cs ApplyLaunchSettings`). It is the **sole owner** of the initial document:

| Action         | Behavior                                                                                  |
|----------------|-------------------------------------------------------------------------------------------|
| `Welcome`      | Leave the Welcome stub; add nothing. **This is the default.**                             |
| `NewSchematic` | `RemoveWelcomeStub()` then `NewScratchSchematic()`.                                       |
| `NewWorkspace` | Show New Workspace dialog; on success, `RemoveWelcomeStub()` (from `CreateDefaultLayout`). If cancelled, Welcome stays. |
| `OpenWorkspace`| Show folder picker; on success, `RemoveWelcomeStub()` (no-op if `RestoreOpenDocuments` already removed it). If cancelled, Welcome stays. |
| `NewSymbol`    | Fall back to Welcome + `Messages.Info` (no blank-symbol path without a cell).             |
| `NewDataDisplay`| Fall back to Welcome + `Messages.Info` (not yet implemented).                            |

**Enum order:** `Welcome` is first (value 0 = default). `AppPreferences.LaunchAction` defaults to
`Welcome` in both `App.axaml.cs` and `SettingsView.LoadGeneralPrefs`.

Command-line file args still override (the `startupPaths.Length > 0` gate in `App.axaml.cs` skips
`ApplyLaunchSettings` entirely).

**macOS startup path:** On macOS (no file args), `firstWindow.Show()` and `ApplyLaunchSettings` are
called inline in `OnFrameworkInitializationCompleted` — NOT deferred to a `ShowFirstWindowIfNeeded`
helper (which has been removed; its guard on `_desktop.Windows` was always false because `firstWindow`
is added to `_desktop.Windows` by assignment to `MainWindow`, before `Show()`). The `_launchHandled`
flag (`bool`, default false) makes a startup Finder file-open take precedence: `OnActivated` sets it
true before the Background-priority `ApplyLaunchSettings` post runs, so the launch action is skipped.

### Materialize + SavePlanDialog (Phase 6h Step 2 — done)

**`SchematicDocument.Materialize(string filePath)`** — `internal` method that sets `FilePath` and clears
`IsDirty`. The one-way scratch→materialized transition. Also used to clear dirty on re-save of materialized
docs. `FilePath` now has `private set` (was readonly in step 1).

**`SavePlan` / `SavePlanBuilder`** (`src/Ui/Schematic/SavePlan.cs`) — framework-free plan model and builder.
`SavePlanBuilder(currentWorkspacePath, workspaceParentDir, scratchDocs).Build(mode, overrides)` produces
`SavePlan { WorkspaceStep?, IReadOnlyList<CellStep>, IReadOnlyList<SaveStep> }`. De-duplicates cell steps
by name. `SchematicHasAnalyses` returns false (TODO 6e hook for analysis→TestBench detection). `SaveMode`:
`EachOwnCell` (default) / `AllInOneCell`.

**`SavePlanExecutor.ExecuteFileOps(SavePlan, existingWorkspaceDir?)`** (`SavePlanExecutor.cs`) — framework-free
static method; creates workspace/.cws, cell folders/.ccell (sets IsTestBench), writes .csch files, sets
PrimarySchematic in .ccell, calls `Materialize` on each doc. Returns list of all files written.

**`WorkspaceViewModel.ExecuteSavePlan(SavePlan)`** — calls `SavePlanExecutor.ExecuteFileOps`, updates
`CurrentWorkspacePath` + `_lastWorkspaceParentDir` (if new workspace), moves docs from `_scratchDocs` to
`_openDocsByPath`, re-wires project tree, calls `Refresh()`, reports all written paths via `Messages.Success`.

**`WorkspaceViewModel.SaveAllDocumentsCommand`** (`[RelayCommand] async Task SaveAllDocuments(Window? owner)`)
— the new ⌘S/Ctrl+S handler:
1. Dirty scratch docs → `SavePlanBuilder.Build` → `SavePlanDialog.ShowDialog<SavePlan?>` → on confirm, `ExecuteSavePlan`
2. Dirty materialized docs → `SchematicPersistence.SaveToFile` + `Materialize` directly
3. `WriteWorkspaceFile` if workspace exists
- Returns "Nothing to save" info if nothing is dirty.

**`SavePlanDialog`** (`src/Ui/Views/Dialogs/SavePlanDialog.axaml(.cs)`) — HIG plan dialog:
- Title "Save your work" / subtitle "circuitRF will create the following and save your documents."
- Mode toggle (`EachOwnCellRadio` / `AllInOneCellRadio` + `SharedCellNameBox`) visible when cells will be created
- Plan table (`PlanRowsPanel` StackPanel): workspace rows (FolderOutline icon), cell rows (Folder icon + TestBench
  badge), save rows (FileOutline icon + primary badge). Rows built programmatically in code-behind.
- Inline `NameValidator` errors per editable row (OrangeRed text below each row).
- **Save All** (`SaveAllButton`, `IsDefault=True`, `HorizontalContentAlignment=Center`) / **Cancel** (`IsCancel=True`)
- Returns confirmed `SavePlan` or null on cancel via `ShowDialog<SavePlan?>`.

**⌘S/Ctrl+S routing:** `WorkspaceWindow.axaml` binds both `NativeMenuItem` and `KeyBinding` for ⌘S/Ctrl+S
to `SaveAllDocumentsCommand`. `SaveWorkspaceAsCommand` remains bound to ⌘⇧S/Ctrl+Shift+S. Menu item now
reads "Save All" (macOS NativeMenu + in-window Menu).

**Scope fence (step 2):** into-cell Save-All only. Close/quit prompts, autosave, and loose/plain-file tiers
are step 3.

### Three-tier save + close/quit prompts + autosave/recovery (Phase 6h Step 3 — done)

**Tier 2 — loose Known File (`SaveLooseSchematicCommand`, bound to File → "Save Schematic As…"):**
`SaveLooseToWorkspace(doc, owner)` shows a file picker → writes `.csch` → atomically updates `.cws`
(`WorkspacePersistence.SaveToFileAtomic`) adding the path to `CwsFile.KnownFiles` → scratch→materialized
transition (`_scratchDocs.Remove`, `_recovery.ClearDoc`, `doc.Materialize`, `_openDocsByPath[fp] = doc`).

**Tier 3 — no workspace (`SaveLooseNoWorkspace`):** `SaveChangesDialog` with "Create Workspace…" /
"Save as File" / Cancel. "Create Workspace…" routes to the full plan dialog (same as ⌘S). "Save as
File" → `SaveLoosePlainFile` (file picker, plain `.csch`, no workspace registration,
`_recovery.ClearDoc`).

**Close/quit prompts:**
- **Tab close** — `CircuitRfDockFactory.CloseDockableConfirm: Func<IDockable, Task<bool>>?` wired in
  constructor; `CloseDockable` is `async void` override: awaits hook, returns without calling base on
  cancel. `ConfirmCloseDockable` shows `SaveChangesDialog` per dirty `SchematicDocument`.
- **Window close / quit** — `WorkspaceWindow.OnClosing` async override: `e.Cancel = true`, await
  `PromptSaveBeforeClose`, then `_vm.OnCleanExit(); _closingConfirmed = true; Close()`.
- **NewWorkspace / OpenWorkspace / OpenRecentWorkspace** — `HasAnyDirtyWork()` guard; on dirty,
  `PromptSaveBeforeClose` (Save All / Don't Save / Cancel); Cancel aborts the navigation.
- **`PromptSaveBeforeClose`** — collects dirty scratch + dirty materialized; single dialog message;
  Save → plan dialog for scratch + direct write for materialized; DontSave → proceed; Cancel → false.

**`SaveChangesDialog`** (`src/Ui/Views/Dialogs/SaveChangesDialog.axaml(.cs)`) — now configurable:
constructor accepts `message`, `saveLabel`, `dontSaveLabel`, `cancelLabel`; `Close(Result)` on each
button so both `ShowDialog` and `ShowDialog<T>` return correctly. `SizeToContent="WidthAndHeight"`.

**Autosave/recovery — `RecoveryManager`** (`src/Ui/Schematic/RecoveryManager.cs`, framework-free):
- **Session dir:** `LocalApplicationData/circuitRF/recovery/<12-char-hex-guid>/` (created lazily).
- **`AutoSave(doc)`** — atomic `.csch` write (temp + `File.Move(..., overwrite: true)`); silently
  swallows I/O errors (autosave must never interrupt editing).
- **`ClearDoc(doc)`** — removes one recovery file when a doc is cleanly saved/materialized. Prunes
  empty session dir.
- **`ClearSession()`** — removes entire session dir on clean exit.
- **`FindPriorSessions(currentSessionDir)`** / **`LoadSession(sessionDir)`** / **`DeletePriorSession`**
  — discovery + deserialize for restore offer.

**Wiring in `WorkspaceViewModel`:**
- `RecoveryManager _recovery` initialized in constructor.
- `StartAutosaveTimer()` — `DispatcherTimer` (30 s interval) → `AutoSaveAll()`.
- `AutoSaveAll()` — iterates `_scratchDocs.Where(IsDirty)`, calls `_recovery.AutoSave`.
- `CheckForRecovery()` (async void) — deferred via `Dispatcher.UIThread.Post(..., Background)`;
  finds prior sessions; shows restore dialog; on accept: opens recovered docs as new scratch tabs;
  on decline: calls `DeletePriorSession`.
- `OnCleanExit()` — stops timer, calls `_recovery.ClearSession()`. Called before confirming quit.
- `_recovery.ClearDoc` at every materialization point: `ExecuteSavePlan` (per save step),
  `SaveLooseToWorkspace`, `SaveLoosePlainFile`.
- `OnDockableClosed` (subscribed to `_factory.DockableClosed`) — removes closed docs from
  `_scratchDocs` and `_openDocsByPath`.

**Scratch-only invariant:** autosave never touches materialized docs. Once a doc is materialized
(removed from `_scratchDocs`), no recovery file is created or offered for it.

---

## Scratch symbols + New Symbol on launch (ScratchSymbol — done)

**New Symbol on launch** (On-launch = New Symbol) now opens a scratch symbol immediately — no workspace or
cell required. The lifecycle mirrors scratch schematics at the document level.

### SymbolEditorDocument scratch identity
- `FilePath: string?` — null for scratch; set to the on-disk `.csym` path for materialized docs.
- `IsScratch => FilePath is null` — computed.
- `IsDirty: bool` — the VM's `IsDirty` (`[ObservableProperty]`) is the **single source of truth**;
  `SymbolEditorDocument` subscribes to `ViewModel.PropertyChanged` for `IsDirty` changes and mirrors it.
  **Do NOT double-track** — only the document subscribes to the VM, not the reverse.
- Tab title = `"• " + baseTitle` when `IsDirty`; plain title when clean.
- `Materialize(string filePath)` — sets `FilePath`, sets `ViewModel.CurrentSymbolPath`, sets `ViewModel.IsDirty = false`.
  IsDirty on the document clears via the PropertyChanged subscription.

### WorkspaceViewModel scratch symbol tracking
- `_scratchSymbols: List<SymbolEditorDocument>` — open scratch symbol docs. Not in `_openDocsByPath`.
- `NewScratchSymbol()` — creates `EditableSymbol { UserEditable = true }` → `SymbolEditorViewModel` →
  `SymbolEditorDocument(title, vm)` (null FilePath), wires `vm.SymbolSaved += OnSymbolSaved`,
  adds to `_scratchSymbols`, opens via `_factory.OpenDocument`.
- `NextScratchSymbolTitle()` — lowest free `"Untitled-Symbol-N"` across `_scratchSymbols` + open symbol docs.
- `OnDockableClosed`: removes from `_scratchSymbols` (mirrors `_scratchDocs.Remove` for schematics).
- Both workspace-reset paths (`NewWorkspace`, `OpenWorkspace`) call `_scratchSymbols.Clear()`.

### Launch action
`ExecuteLaunchActionAsync(NewSymbol)` → `_factory.RemoveWelcomeStub(); NewScratchSymbol();`
(was: fall back to Welcome + info message).

### Save-target offer dialog (⌘S with scratch symbol active)
`SaveAllDocuments` SingleDoc branch routes `SymbolEditorDocument` through `SaveSingleSymbolDocument`:
- **Scratch** → `SaveScratchSymbol(doc, window)` shows `SaveChangesDialog` with:
  - **"Save to Cell…"** (workspace open): `InputNameDialog` for cell name → `CellFolder.CreateCellFolder` +
    `SubFolderPath(ViewType.Symbol)` → `SymbolPersistence.SaveToFile` → `doc.Materialize` → move from
    `_scratchSymbols` to `_openDocsByPath` → `OnSymbolSaved` (cache invalidation) → tree refresh.
    Cell name = symbol filename (e.g., cell "MyFET" → `MyFET/symbol/MyFET.csym`).
    No workspace: routes to "Save as File" branch instead.
  - **"Save as File"** (orphan): delegates to `vm.SaveSymbolAsCommand.ExecuteAsync(window)` (file picker +
    `PerformSave`), then calls `doc.Materialize(pathAfter)` + moves to `_openDocsByPath`.
    No workspace registration, no tree entry — bare .csym.
  - **Cancel**: no-op.
- **Materialized** → `vm.SaveSymbolCommand.ExecuteAsync(window)` (existing path, already works).

### Full dirty-work coverage (Layer 5)
- `HasAnyDirtyWork()`: includes `_scratchSymbols.Any(IsDirty)` and materialized symbol docs.
- `ConfirmCloseDockable`: added branch for dirty `SymbolEditorDocument` (same Save/Don't Save/Cancel
  pattern as schematics; Save → `SaveSingleSymbolDocument`; returns `!symDoc.IsDirty` so Cancel in
  the save-target dialog also cancels the close).
- `SaveAllDocuments` AllDocs scope: iterates `dirtyScratchSymbols` (per-doc offer dialog) and
  `dirtyMaterializedSymbols` (direct VM save).
- `PromptSaveBeforeClose`: includes dirty scratch + materialized symbol docs in total count and save path.

### Recovery / autosave
**Deferred for v1.** Scratch symbols are lost on crash in v1. `AutoSaveAll` / `CheckForRecovery` cover
only `_scratchDocs`. Extending to `_scratchSymbols` is a straightforward follow-up.

### v2 deferred items
- Full `SavePlan`/cell-wizard for symbols (AllInOneCell mode, TestBench detection, plan dialog).
- "Save to Cell" when no workspace: currently routes to "Save as File"; v2 should offer workspace creation.

---

## New Workspace dialog + Open Workspace + Recent Workspaces (Phase 6g Step 5 fix 5)

**File → New Workspace uses `NewWorkspaceDialog`** (`src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml(.cs)`),
NOT a raw system folder picker. The user names a *Workspace*; circuitRF creates the folder.

Key rules:
- The system folder picker (`OpenFolderPickerAsync`) is used **only** behind the "Choose…" button to
  select the **parent location** — an existing folder is fine for that.
- The workspace folder = `parent/<name>/` — always created by us, never pre-existing (dialog gates OK
  on this + `NewWorkspace` re-checks at create time as a race guard).
- `NewWorkspaceDialog` returns `NewWorkspaceResult { ParentDir, Name }` via `ShowDialog<NewWorkspaceResult?>`,
  or null on cancel — mirrors `InputNameDialog`'s return-or-null contract.
- Name validated live via `NameValidator`; workspace name comes from the folder leaf, never the `.cws` stem.
- On open, the name field is **prefilled** with the next free `Untitled-Workspace-N` for the current Location.
  When the user changes Location via "Choose…" without having manually edited the name, the suggestion is
  recomputed for the new location. Suppression flag `_settingSuggested` prevents `OnNameChanged` from
  marking the programmatic fill as a user edit.

### Tracked Location (in-memory, not persisted)
`WorkspaceViewModel._lastWorkspaceParentDir` (initialized to Documents): seeds the Location field in
`NewWorkspaceDialog` and the `SuggestedStartLocation` of `OpenFolderPickerAsync`. Updated after every
successful New or Open to the parent of the workspace folder. **Never persisted.**

### Open Workspace = folder picker
`OpenWorkspace` uses `OpenFolderPickerAsync` (not file picker). The selected folder IS the workspace
folder; `.cws` = `Path.Combine(folder, ".cws")`. If `.cws` does not exist, the open is rejected with a
clear error message. Menu item reads "Open Workspace…" in both NativeMenu and in-window Menu.

### Recent Workspaces (persisted in AppPreferences)
- `AppPreferences.RecentWorkspaces: List<string>?` — the `.cws` paths, MRU order, capped at 10.
  Serialized as `recent_workspaces` in `preferences.json`. Null when empty (omitted from JSON).
- `WorkspaceViewModel.PushRecent(cwsPath)` — dedup (case-insensitive), insert at front, cap 10, save,
  rebuild menu items. Called after every successful `NewWorkspace` and `OpenWorkspace`.
- `WorkspaceViewModel.RecentMenuItems: ObservableCollection<Control>` — holds `MenuItem` + `Separator`
  instances rebuilt by `RebuildRecentMenuItems()`. Bound to the in-window "Open Recent" submenu via
  `ItemsSource`. `HasRecentWorkspaces` (bool property, notified on change) drives `IsEnabled`.
- `WorkspaceViewModel.RecentWorkspacesChanged: event Action?` — fired after every push/clear so
  `WorkspaceWindow.axaml.cs` can rebuild the NativeMenu.
- **NativeMenu rebuild**: `WorkspaceWindow.axaml.cs` subscribes to `RecentWorkspacesChanged` in
  `OnDataContextChanged`. `EnsureNativeRecentMenuWired()` (called once from `OnOpened`) inserts the
  "Open Recent" `NativeMenuItem` programmatically after "Open Workspace…" and populates it.
  `RebuildNativeRecentMenu()` clears and repopulates `NativeMenuItem.Menu.Items` on every change.
- **Missing entry**: if a recent workspace's `.cws` no longer exists, `OpenRecentWorkspace` removes it
  from the list, saves, rebuilds menus, and shows an error.
- `ClearRecentWorkspaces` command empties the list, saves, and rebuilds both menus.

## macOS / command gotchas (Phase 6g Step 5 fixes)

### `$parent[Window]` is null on macOS for NativeMenu and KeyBinding
`{Binding $parent[Window]}` resolves to `null` for `NativeMenuItem.CommandParameter` and
`KeyBinding.CommandParameter` on macOS — neither lives in the Avalonia visual tree where the ancestor
walk can reach a `Window`. The standard fix is a `ResolveOwner(Window? parameter)` helper in the ViewModel:

```csharp
private Window? ResolveOwner(Window? parameter) =>
    parameter
    ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
       ?.Windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
```

`desktop.MainWindow` is also null in this app (`App.axaml.cs` calls `window.Show()` but never assigns
`desktop.MainWindow`), so `.MainWindow` is the wrong fallback. Walking `ApplicationLifetime.Windows`
keyed by `ReferenceEquals(w.DataContext, this)` finds the exact host window and works correctly in
multi-window scenarios. Apply `ResolveOwner` to every command that takes `Window?` and opens a picker.

### `CreateDefaultLayout()` replaces all factory tools
`CircuitRfDockFactory.CreateDefaultLayout()` internally calls `CreateLayout()` which assigns new
instances to `ProjectTreeTool`, `PropertiesTool`, etc. Any command that resets the layout (currently
only `NewWorkspace`) must re-wire those tools **after** `Layout = newLayout`:

```csharp
Layout = newLayout;
_factory.ProjectTreeTool?.SetActions(this);
_factory.ProjectTreeTool?.SetWorkspace(workspaceDir);
```

`SetActions` before `SetWorkspace` because `SetWorkspace` → `RebuildVmTree` uses `_actions`.

### Dock `Tool.Title` PropertyChanged is not reliably picked up by Avalonia compiled bindings
Setting `Title` on a Dock `Tool` (base class) calls `SetProperty` which fires `PropertyChanged` via
the Dock library's `ObservableObject`. However, Avalonia compiled bindings (`x:DataType`) on the
tool's view do not reliably pick up this event in practice. Expose a separate `[ObservableProperty]`
for any observable header text the view needs to bind to:

```csharp
[ObservableProperty] private string _workspaceName = "No workspace";
```

`ProjectTreeTool.Title` is **static "Project"** — set once in the constructor, never updated per workspace.
The in-view header `TextBlock.Text` binds to `WorkspaceName` (set in `SetWorkspace`, reset to "No workspace"
in `ClearWorkspace`). Do NOT update `Title` per workspace — the dock-tab label is intentionally always "Project".

### `.cws` is a dotfile — derive the workspace name from the FOLDER, not the file stem
A circuitRF workspace is a **named folder containing a `.cws` file**: `…/<name>/.cws`.
`Path.GetFileNameWithoutExtension(".cws")` in .NET returns `".cws"` (dotfiles have no extension), NOT the
workspace name. Always derive the workspace name from the **folder name**:

```csharp
var dir  = Path.GetDirectoryName(cwsPath);       // …/<name>
var name = Path.GetFileName(dir);                 // <name>
```

Apply this everywhere the workspace name must be displayed (window title, `WorkspaceName`, messages).

---

## Cell reference model + live update (Phase 6g Step 5 — done)

### Entry points (L1)
`NewWorkspace` creates a real `.cws` + workspace folder on disk (was only resetting dock state).
`File → New Cell` menu item (`NewCellInWorkspaceCommand` on `WorkspaceViewModel`, greyed via
`CanExecute = CurrentWorkspacePath is not null`; `NotifyCanExecuteChanged` in `OnCurrentWorkspacePathChanged`).
Tree-header **New Cell** button in `ProjectTreeView` (`IsVisible="{Binding HasWorkspace}"`).
`ITreeActions.NewCellInWorkspaceAsync()` is the shared implementation: prompts with `InputNameDialog`,
validates with `NameValidator`, calls `CellFolder.CreateCellFolder(workspaceDir, name)` + `Refresh()`.

### Cell-ref data model (L2)
**`EditableComponent.CellRef: string?`** — relative path from the schematic directory to the referenced
cell folder.  Null for built-in components.  Round-tripped through `CschComponent.CellRef` (nullable,
`WhenWritingNull`; omitted from file when null).

**`SchematicEditModel.SchematicDirectory: string?`** — absolute directory of the containing `.csch` file.
Set by `SchematicPersistence.FromFileModel` from the directory argument passed by `LoadFromFile`.
Used as the base for resolving `CellRef` relative paths.

**`CellSymbolResolver`** (`src/Ui/Schematic/CellSymbolResolver.cs`) — framework-free static resolver.
`Resolve(cellRef, baseDir) → CellSymbolResolution { State: CellSymbolState, Symbol? }`.
`CellSymbolState` enum: `Resolved / NotFound / PrimaryMissing` (kept distinct — do NOT collapse).
Cache keyed by `(cellAbsDir, primaryFilename, symFileMtime)`; invalidated by `Invalidate(cellAbsDir)`
or `InvalidateAll()`.  Resolution chain: relative path → `Directory.Exists` → `CellFolder.ResolvePrimary`
(single primacy source) → `SymbolPersistence.LoadFromFile`.

### Three-state rendering (L3)
**`SchematicComponent`** gained `CellRefState: CellSymbolState?` and
`CellRefPrimitives: IReadOnlyList<SymbolPrimitive>?` (both null for built-ins).

**`BuildRenderModel`** pre-resolves all `CellRef` values via `ResolveAllCellRefs()` before the
connectivity pass.  Resolved symbol pins supply port world-coords (not `SymbolPortDefs`).
`ToRenderComponent(isConnected, cellRefResolution?)` uses resolved pins for ports and resolved
primitives for the glyph BB.

**`SchematicRenderer`** dispatches on `CellRefState` before the built-in draw path:
- `Resolved` → `DrawSymbol(c.CellRefPrimitives, ...)` — same path as built-ins, no `DrawVariadicPortLeads`
- `NotFound` → `DrawCellRefNotFoundGlyph` — warning fill+stroke box, "Not Found" centred label
- `PrimaryMissing` → `DrawCellRefPrimaryMissingGlyph` — plain stroke rectangle stand-in
- `null` (built-in) → existing `BuiltInSymbols.Primitives` + `DrawVariadicPortLeads` path, unchanged

### Live update (L4)
**`SymbolEditorViewModel.SymbolSaved: event Action<string>?`** fires from `PerformSave` with the
absolute `.csym` path.  Both Save and Save-As go through `PerformSave`.

**`SchematicViewModel.TriggerRebuild()`** calls `EditModel.NotifyChanged()` — reuses the same
`Changed → RebuildRenderModel` pipeline used by all mutation commands.

**`WorkspaceViewModel`** wiring:
- `OnSymbolSaved(savedSymPath)` — derives `cellDir` (two `GetDirectoryName` calls up), calls
  `CellSymbolResolver.Invalidate(cellDir)`, then `RebuildOpenSchematics()`.
- `RebuildOpenSchematics()` — iterates `_openDocsByPath.Values`, calls `TriggerRebuild()` on every
  `SchematicDocument`.
- `MakePrimary` — after writing a new primary symbol (`subFolderName == CellFolder.SymbolSubFolder`),
  calls `Invalidate(cellDir)` + `RebuildOpenSchematics()`.
- `OpenOrActivateSymbol` and `NewSymbolAsync` both subscribe `vm.SymbolSaved += OnSymbolSaved` when
  the `SymbolEditorViewModel` is created.

**Dangling wires on pin-count change:** wires to ports that no longer exist in the new symbol show
as unconnected (dangling). No auto-rewire (Option B still deferred).

---

## Project Tree interactions (Phase 6g Step 4 — done)

**ITreeActions** (`src/Ui/ViewModels/ProjectTree/ITreeActions.cs`) — callback interface implemented by
WorkspaceViewModel, injected into `ProjectTreeNodeViewModel` via `ProjectTreeTool.SetActions(ITreeActions)`.
Every tree-node command delegates to this interface so all open/create/reveal operations live in WorkspaceViewModel.

**Commands on ProjectTreeNodeViewModel** — `ActivateCommand` (double-click open/activate),
`MakePrimaryCommand` (view files), `RevealCommand` (all file/folder nodes), `NewCellCommand`
(workspace/library nodes), `NewSymbolCommand` / `NewSchematicCommand` (cell nodes).  Context-menu
`IsVisible` driven by `IsViewFile`, `IsCell`, `IsWorkspaceOrLibrary`, `CanReveal`.

**Open/activate dedup** — `WorkspaceViewModel._openDocsByPath` (`Dictionary<string, IDockable>`,
OrdinalIgnoreCase) tracks open docs by absolute path. `ActivateIfOpen(absPath)` checks before opening;
activates the existing tab if found. Users can close a tab and reopen from the tree without issue.

**Double-click open paths:**
- `.csym` → `SymbolPersistence.LoadFromFile` + `EditableSymbol.FromSymbol` + `SymbolEditorDocument` (real)
- `.csch` → `SchematicPersistence.LoadFromFile` + `SchematicViewModel` + `SchematicDocument` (real)
- cell node → `CellParameterEditorDocument` (real, step 6 — see below)
- `.clay`, other view-file types, data displays, color themes → no-op

**Make Primary** — reads `.ccell` from `../..` of the view file path, sets the correct `PrimarySchematic` /
`PrimarySymbol` / `PrimaryLayout` field (discriminated by sub-folder name), writes back, calls `Refresh()`.
When the changed view is a symbol, also calls `CellSymbolResolver.Invalidate(cellDir)` + `RebuildOpenSchematics()`
so open schematics re-render with the new primary (Step 5 live-update).

**Reveal** — `Process.Start`: macOS `open -R <path>`, Windows `explorer /select,"<path>"`,
Linux `xdg-open <parent-dir>`.  Platform detected via `RuntimeInformation.IsOSPlatform`.

**Creation actions** — `InputNameDialog` (`src/Ui/Views/Dialogs/`) prompts for name, validated with
`NameValidator`.  On confirm:
- New Cell → `CellFolder.CreateCellFolder(parentDir, name)` + `Refresh()`
- New Symbol → write empty `.csym` via `SymbolPersistence.SaveToFile` + open `SymbolEditorDocument` with
  fresh `EditableSymbol { UserEditable=true, CurrentSymbolPath=path }` + `Refresh()`
- New Schematic → write empty `.csch` via `SchematicPersistence.SaveToFile` + open `SchematicDocument` with
  new `SchematicViewModel(emptyModel)` + `Refresh()`
- New Layout → `IsEnabled=False` (greyed, v2)

`RevealLabel` on the VM is platform-aware ("Reveal in Finder" / "Reveal in Explorer" / "Reveal in File Manager").

## Cell-parameter editor (Phase 6g Step 6 — done)

**Purpose:** edits the cell's **declared parameter interface** in its `.ccell` — add / remove / rename rows +
defaults (Name / Default / Unit / Dimension / ShowOnSchematic). NOT instance values. The delta vs. the instance
editor (`ParameterEditorViewModel`): rows are add/remove/rename-able; Name is editable.

### Edit model (framework-free)
**`CellParameterEditModel`** (`src/Ui/Schematic/CellParameterEditModel.cs`) — wraps a `CcellFile` + `.ccell`
path. `IReadOnlyList<CcellParameter> Parameters` (read view); `internal List<CcellParameter> MutableParameters`
(command access); `Save()` writes `.ccell`; `NotifyChanged()` fires `Changed` event so the VM rebuilds rows.

### Commands (framework-free, `src/Ui/Commands/Cell/`)
- **`AddCellParameterCommand`** — appends; Undo removes by reference.
- **`RemoveCellParameterCommand`** — records insertion index; Undo re-inserts at saved index.
- **`SetCellParameterCommand`** — stores full old/new snapshot for Name, Default, Unit, Dimension, Show.
  Covers rename, default edit, unit/dimension/show changes. Both Execute and Undo persist + notify.

### ViewModel (`src/Ui/ViewModels/CellParameterEditorViewModel.cs`)
Owns its own `UndoRedoStack` (per the per-document-undo rule). Subscribes to `_editModel.Changed`;
`RebuildRows()` clears + recreates `ObservableCollection<CellParameterRowViewModel>` on every mutation or undo.
`AddParameterCommand` generates a unique `ParamN` name. `UndoCommand`/`RedoCommand` delegate to own stack.

### Row VM (`src/Ui/ViewModels/CellParameterRowViewModel.cs`)
Staged Name (editable), Default, Unit, Dimension, ShowOnSchematic. Commit methods called from code-behind
(LostFocus/Enter for TextBoxes; SelectionChanged for ComboBoxes). `partial void OnStagedNameChanged`:
shows `RenameWarning` while name diverges from model. `partial void OnShowOnSchematicChanged`: auto-commits.
`CommitName` validates `[A-Za-z_][A-Za-z0-9_]*`; reverts on invalid. `CommitDimension` resets unit to first
valid option for the new dimension. `AllDimensions` = `Enum.GetValues<UnitDimension>()` (static array).

### Document (`src/Ui/Schematic/CellParameterEditorDocument.cs`)
`Document + IUndoableDocument` — `UndoRedo => ViewModel.UndoRedo`. Keyed by cell folder path in
`WorkspaceViewModel._openDocsByPath`. Workspace undo routing routes to its stack while active.

### View (`src/Ui/Views/Content/CellParameterEditorView.axaml(.cs)`)
`x:DataType="CellParameterEditorDocument"`. Layout: header (cell name + "Parameters" label), column-header
row, scrollable `ItemsControl` (rows), footer (Add Parameter button). `Grid.IsSharedSizeScope="True"` on the
outer container aligns header columns with row columns (`CpName`, `CpUnit`, `CpDim`, `CpShow`, `CpRemove`).
Code-behind: `_suppressUnitCommit` + `_suppressDimCommit` flags prevent re-entrant SelectionChanged commits.
DataTemplate registered in `App.axaml`.

### Wiring
`WorkspaceViewModel.OpenOrActivateCellPlaceholder` (step 4 stub) replaced: loads `.ccell` via
`CellPersistence`, creates `CellParameterEditModel` + `CellParameterEditorViewModel` + `CellParameterEditorDocument`,
opens via `_factory.OpenDocument`, keyed by cell folder path in `_openDocsByPath`. Dedup/activate already open.

**Scope fence:** cell-parameter editor only — no instance-value migration, no `.cws` work (step 7).

## .cws lifecycle (Phase 6g Step 7 — done)

**Atomic writes everywhere:** All `.cws` writes use `WorkspacePersistence.SaveToFileAtomic` (temp-rename).
Callers: `NewWorkspace`, `WriteWorkspaceFile`, `SavePlanExecutor.ExecuteFileOps`, `SaveLooseToWorkspace`.
`SaveToFile` (non-atomic) remains only in test helpers.

**Corruption-tolerant open:** `TryLoadCws(string cwsPath)` — loads `.cws`, logs `Messages.Warning` on any
exception, returns `new CwsFile()`. Both `OpenWorkspace` and `OpenRecentWorkspace` call it. Tree content
is always populated by `WorkspaceScanner` (filesystem is truth, scanner's own `TryLoadCws` handles corruption).
"No `.cws` → not a workspace" gate (file-exists check) retained; a corrupt-but-present `.cws` degrades to
defaults, never rejects the open.

**Real `WriteWorkspaceFile`:** Loads existing `.cws` to preserve `KnownFiles` + `LibraryRefs` (authoritative
on disk), updates `ColorSchemeName` + `TreeViewState`, writes atomically. `DockLayout` stays null in v1
(`Dock.Serializer` not referenced). `silent` param suppresses success message on debounce/exit flush.

**Debounced autosave:** `ScheduleCwsSave()` resets a 3-second `DispatcherTimer`; on tick, writes silently.
`SubscribeToFilterState()` hooks `ProjectTreeFilterState.PropertyChanged → ScheduleCwsSave()`, tracking the
current tool instance across `CreateDefaultLayout()` replacements (called in constructor, `NewWorkspace`,
`ResetLayout`, `ExecuteSavePlan`). Pan/zoom never touches a `.cws` field — no write there.
`OnCleanExit` stops timers, flushes `.cws` synchronously, then clears recovery cache.

**Tree view-state in `.cws` (`CwsTreeViewState`):** 7 bool filter flags; `Ordering` string (v1 = null,
reserved for future ordering UI). Written by `WriteWorkspaceFile`; restored on open via `ApplyTreeViewState`
(unsubscribes debounce handler during restore to avoid spurious write). `TreeViewState` is
`JsonIgnore(WhenWritingNull)` — null written as nothing; missing on read → `null` → defaults applied.

**`MemberFiles` removed:** Deleted from `CwsFile`. Old `.cws` files with `member_files` still load (System.Text.Json
ignores unknown fields by default with `PropertyNameCaseInsensitive = true`).

## Project Tree VIEW (Phase 6g Step 3 — done)

Three new files in `src/Ui/ViewModels/ProjectTree/`:
- **`ProjectTreeFilterState`** (`ProjectTreeFilterState.cs`) — `ObservableObject` with 7 independently
  togglable bool properties (`Cells`, `Libraries`, `TestBenches`, `DataDisplays`, `ColorThemes`,
  `KnownFiles`, `WorkspaceFileSystem`); `IsAllOn` and `SetAll(bool)` helpers.
- **`ProjectTreeNodeViewModel`** (`ProjectTreeItemViewModel.cs` — old stub replaced) — wraps a
  `ProjectTreeNode`; exposes `IconKind` (`MaterialIconKind` switch on `Kind` + `IsTestBench`),
  `IsWarning`, `IsBold`, `IsItalic`, `IsExpanded` (two-way, for refresh-state preservation),
  `Children` (all), `FilteredChildren` (category-filtered, reactive).  Bottom-up `ApplyFilter()`
  preserves ancestors when a descendant's category is on.  `MissingNamedPrimary` → `IsWarning = true`.
- **`ProjectTreeTool`** (rewritten, deletes 6b stub) — `FilterState`, `RootItems`, `HasWorkspace`,
  `SetWorkspace(dir)` / `ClearWorkspace()`, `[RelayCommand] Refresh()` (re-scans + preserves expand
  state), `RebuildVmTree(expandedPaths)`.

Views:
- **`ProjectTreeView.axaml`** (rewritten) — toolbar (Refresh button + Filter flyout with 7 checkboxes),
  no-workspace placeholder, `TreeView` with `TreeDataTemplate ItemsSource="{Binding FilteredChildren}"`;
  styles: `TreeViewItem` `IsExpanded TwoWay`, `.pt-bold` / `.pt-italic` / `.pt-warning`
  (`.pt-warning` uses `{DynamicResource CrfWarningBrush}`); per-kind Material icon + name `TextBlock`
  with conditional classes; tooltip = WarningReason (if warning) + RelativePath.
- **`ProjectTreeView.axaml.cs`** (rewritten) — on-focus refresh via `Window.Activated`, debounced with
  `_refreshPending` bool + `Dispatcher.UIThread.Post` at `Background` priority.

`WorkspaceViewModel` updated: `SetWorkspace(dir)` / `ClearWorkspace()` wired to `CurrentWorkspacePath`
change; `OpenTreeItem` and its coupling to the 6b stub removed.

`CrfWarningBrush` (`SolidColorBrush`) declared in `App.axaml`; updated from `ThemeService.Active`
`ColorRole.SystemWarning` in `App.axaml.cs` `UpdateCrfWarningBrush()`, subscribed to `ThemeChanged`.

10 new VM tests in `ProjectTreeNodeViewModelTests.cs`; 372 total green.

## Workspace scan + project tree model (Phase 6g Step 2 — done)

Three framework-free files in `src/Ui/Schematic/` (no Avalonia / Skia):
- **`ProjectTreeNode`** / **`NodeKind`** (`ProjectTreeNode.cs`) — the in-memory tree node.
  `NodeKind` covers all §3.3 filter categories: `Workspace`, `Cell`, `Library`, `LibrariesGroup`,
  `CellViewFolder`, `ViewFile`, `UserFolder`, `DataDisplayFile`, `ColorThemeFile`, `OtherFile`,
  `KnownFile`, `KnownFilesGroup`.  Per-node flags: `IsPrimary`, `IsTestBench`, `WarningReason`
  (non-null = System.Warning tooltip text).  Children are mutable only via `internal AddChild`
  (called only by the scanner).  The tree is transient — rebuilt on every refresh.
- **`WorkspaceScanner`** (`WorkspaceScanner.cs`) — `static Scan(rootDir)` walks the workspace folder
  into that model.  Filesystem is truth (no membership list consulted).  Delegates primacy entirely
  to `CellFolder.ResolvePrimary` (never re-derived).  Empty view sub-folders produce no node.
  Stable ordering: alphabetical (OrdinalIgnoreCase) within every level.  Tolerates missing/corrupt
  `.cws`.  Warning states recorded as DATA (MissingNamedPrimary, broken library paths, broken Known
  Files) so step 3 renders System.Warning + tooltip without the scanner caring about rendering.
- **`WorkspaceModel`** (`WorkspaceModel.cs`) — thin wrapper: `WorkspaceRootDir`, `RootNode`,
  `Rescan()`.  The step-3 view binds to this and calls `Rescan()` on focus/Refresh.
  No `FileSystemWatcher` (deferred per §9).

`CwsFile.KnownFiles` (`List<string>`) added to `WorkspacePersistence.cs` — additive v1 field;
old `.cws` files load with an empty list.  `LibraryRefs` now accepts folder paths or `.clib` file
paths (scanner takes the parent dir for `.clib`).  `MemberFiles` is retained for round-trip but
is ignored by the scanner (membership is the filesystem, not a manifest).

41 new tests in `WorkspaceScannerTests.cs` (L1 node model, L2 scanner, L3 refresh); 362 total green.

## Workspace cell-level building blocks (Phase 6g Step 1 — done)

Three framework-free files in `src/Ui/Schematic/` (no Avalonia / Skia):
- **`NameValidator`** — cross-platform-safe name validation (§1.4 charset; `IsValid`/`Validate`).
- **`CellPersistence`** / **`CcellFile`** / **`CcellParameter`** — `.ccell` read/write mirroring `SymbolPersistence`
  conventions (System.Text.Json, enum-as-string, format_version reject, `Id` never persisted).
  `CcellParameter` mirrors `EditableParameter`'s shape but holds the **default** expression (the cell's
  declared interface, not an instance override).
- **`CellFolder`** — `CreateCellFolder(parentDir, cellName)` creates `cellName/schematic/symbol/layout/` + initial
  `.ccell`.  **`ResolvePrimary(cellFolder, viewType)`** is the single source of primacy truth implementing
  the five-branch rule (workspace-and-project-tree.md §2): SoleFile → NamedPresent → MissingNamedPrimary →
  NoPrimary → NoView.  The **MissingNamedPrimary** contradiction is kept distinct (drives System.Warning).
  The filesystem is truth; no membership list is maintained here.

## Symbol Editor (Phase 6f steps 4a + 4b — editing shell + drawing tools)

The Symbol Editor is a **smaller sibling of the schematic stack**. Mirror its patterns; do not reinvent.

### Stack structure
- `EditableSymbol` (`src/Ui/Schematic/EditableSymbol.cs`) — mutable working copy of a `Symbol`. Commands
  hold a reference and call `NotifyChanged()` after mutation.  Framework-free.
- `SymbolGeometry` (`src/Ui/Schematic/SymbolGeometry.cs`) — `BboxOf(prim)`, `HitTest(prim, x, y, tol)`,
  `TranslateBy(prim, dx, dy)`, `ComputeBb(list)`. Framework-free.
- `SymbolEditorViewModel` (`src/Ui/ViewModels/SymbolEditorViewModel.cs`) — all 15 tools (Select + 14
  drawing tools), selection, move-drag, rubber-band, gesture state, current style (`ColorRole`,
  `StrokeTier`, `FontSize`, `FontStyle`), `Execute(IUiCommand)` on the VM's **own** `UndoRedoStack`.
  `SetActiveToolCommand` and `SetCurrentStrokeTierCommand` are `RelayCommand<string>` (parse enum from
  CommandParameter) so every toolbar button needs zero code-behind.
  `FontStyleOptions` is a public static array exposed as ComboBox `ItemsSource`.
- `Commands/Symbol/PlaceSymbolPrimitiveCommand` — appends primitive (topmost Z); Undo removes it. Both
  directions call `NotifyChanged()`. Mirrors Move/Delete commands.
- `Commands/Symbol/MoveSymbolPrimitivesCommand` + `DeleteSymbolPrimitivesCommand` — both directions call
  `EditableSymbol.NotifyChanged()`.
- `SymbolEditorCanvas` (`src/Ui/Controls/SymbolEditorCanvas.cs`) — Skia control with pan/zoom,
  delegates pointer/keyboard/TextInput to VM, cursor = Cross for drawing tools / Default for Select.
- `SymbolEditorRenderer` (`src/Ui/Renderers/SymbolEditorRenderer.cs`) — draws fine-grid (`p=5`),
  calls `SchematicRenderer.DrawSymbol` (reused, not duplicated), selection bboxes, rubber-band;
  renders `InProgressPrimitive` from overlay as dashed ghost via `DrawSymbol(overridePaint: ghostPaint)`.
- `SymbolEditorOverlay` — carries `InProgressPrimitive` (nullable), selection, rubber-band, drag offset.
- `SchematicRenderer.DrawSymbol` — now renders `TextPrimitive` for real (IBM Plex Sans, align, style).
- `EnumEqualsToBoolConverter` (`src/Ui/Converters/`) — `(enum, string param) → bool`; drives
  `Classes.ToolActive` binding on toolbar buttons.
- `SymbolEditorDocument` / `SymbolEditorView` / `SymbolEditorWindow` — dockable document + tear-off window.
  Both host the same `SymbolEditorView`; only the chrome differs.

### Drawing gestures
- **Two-point drag** (click + drag to release): Line, Rect, RoundedRect, Circle, Ellipse, Arc, Sine, HalfWave.
- **Multi-point click** (click per point; Enter or double-click to commit):
  Polyline (≥2 pts), Polygon (≥3 pts, auto-closes), Triangle (exactly 3, auto-commits),
  QuadCurve (exactly 3, auto-commits), CubicCurve (exactly 4, auto-commits).
- **Text**: click anchor → type → Enter commits; Backspace erases; Escape cancels; live cursor shows `|`.
  Uses Avalonia `TextInput` event (IME-safe), not raw `KeyDown`.
- **Escape** during any gesture: cancel (nothing placed). **Escape** with nothing in progress: clear selection.
- All snapped to `p = 5` local units via `SnapToP`.

### Pins (4c — done)
- **Two separate snap grids:** art snaps to `p = 5` (`SnapToP` in the VM); pins snap to `P = 100`
  (`SnapToConnectionGrid`, `PinGrid = 100.0`). Never use `SnapToP` for a pin; never use `SnapToConnectionGrid`
  for art.
- **Pin tool owns pin interaction.** The Select tool only touches primitives. Pin select/move/remap all
  live in `PinToolPress` / `PinToolMove` / `PinToolRelease` paths.
- **Unmapped port = open circuit, never an error.** Surface informally via `DrawUnmappedPortPanel` (soft-
  yellow overlay); do not block editing or flag as an error.
- **PortCount** is the number of ports the symbol maps pins to. Persisted in `.csym`. A `.csym` with
  `PortCount = 0` infers `PortCount = pins.Count` for backward compatibility (old files).
- **Locked gate:** `EditableSymbol.UserEditable = false` → `SymbolEditorViewModel.IsLocked = true` →
  all `Execute()` calls are no-ops; Pin / drawing tools disabled in toolbar; cross cursor reverts to arrow;
  "Read-only" shown in metadata bar. Built-in symbols opened via View menu are always locked.

### .csym I/O (4c — done)
- **Save/Save-As:** `SaveSymbolCommand`/`SaveSymbolAsCommand` on the VM; delegate to
  `SymbolPersistence.SaveToFile`. Both are `IAsyncRelayCommand<Window?>`.
- **Open:** File → "Open Symbol…" in the Workspace window → `WorkspaceViewModel.OpenSymbolFileCommand` →
  `SymbolPersistence.LoadFromFile` → `EditableSymbol.FromSymbol(symbol)` with `UserEditable = true`.
- **Built-in symbols opened from the View menu** are loaded with `UserEditable = false`.
- `.csym` format: JSON with `format_version`; reject-on-mismatch; alpha policy (no migration in v1).

### Deferred (do NOT build without discussion)
- Live schematic update — requires cell model / project-tree / workspace design (later phase).
- Cell-driven open — same dependency.
- Rewiring `SymbolKind → BuiltInSymbols` — same dependency.
- If a task seems to need the cell model, STOP and report (it's the project-tree design).

### Key invariants
- **`DrawSymbol` is shared** — `SchematicRenderer.DrawSymbol` is `internal static`; the editor calls it
  directly. Do NOT write a second symbol renderer.
- **All mutations undoable** on the document's own `UndoRedoStack`; `NotifyChanged()` in both Execute and Undo.
- **Art snaps to `p = 5` local units** (`SnapToP` in the VM). Pins snap to `P = 100` (`SnapToConnectionGrid`).
- **Color is a role** (`SymbolColorRole`), never literal RGB. No color picker in the editor.

### Opening the editor
`WorkspaceViewModel.OpenSymbolEditorDockedCommand` (View menu) opens on Resistor (docked).
`WorkspaceViewModel.OpenSymbolEditorWindowCommand` opens on Inductor (tear-off window).

---

## Symbol orientation (Phase 6f step 3 — standard library art)

**2-terminal symbols are VERTICAL** (R, L, C, V, Tone, Port/Term, GND): local pin 1 at `(0,-200)` (top),
pin 2 at `(0,+200)` (bottom). FET, ZPort, Sdd, Generic stay **horizontal** (ports left/right).

Schematic layout code consequence: place 2-terminal passives at `SymbolRotation.R90` in a horizontal signal
path (pin 1 right, pin 2 left at R90) — same `cx ± 200` wire coordinates as before. Bias-path vertical
components (at R0) need no rotation. See `docs/design/standard-library-symbols.md` for geometry.

---

## Color theming (Phase 6 — three-layer separation)

`src/Ui/Theming/` holds the **framework-free L1 theme model** (no SKColor, no Avalonia):
- `ColorRole` — string role constants (`Schematic.Background`, `System.Warning`, …); add a constant here to introduce a new role.
- `Rgba` — plain `record struct`, serializable via System.Text.Json.
- `ColorVariant` — `{ Light, Dark }` enum; independent of Avalonia's `ThemeVariant`.
- `ColorTheme` — role → RGBA maps for both variants; `Resolve(role, variant)` falls back to `BuiltIn` for absent roles. `ColorTheme.BuiltIn` is the single source of truth for default colors.
- `ColorThemeIo` — `.ccolor` read/write (System.Text.Json, `format_version` reject-on-mismatch, no Id persisted, keys sorted for stable diffs).

**Firewall note:** `src/Ui/Theming/` carries no Avalonia or SkiaSharp types and could migrate to `src/Core` without changes if another assembly ever needs it. Keep it that way.

**L2 projection** (`SchematicRenderTheme.FromTheme`): translates L1 roles to SKColor tokens for the renderer. L3 (not yet built): active-theme preference, workspace tracking, resolution order, Settings UI.

**Rule-of-role:** adding a new themable color = new `ColorRole` constant (L1) + read it in the relevant `*RenderTheme` token struct (L2). L1 and L3 never change for new colors.

## Phase 6d schematic editor — key rules (from 6d-fix)

### Per-segment wire drag convention (Phase 6d wire editing)
Wire segments are **independently selectable and draggable**. A direct click on a wire segment (not near an endpoint) returns `HitKind.WireSegment` with `SubIndex = segmentIndex`, and highlights only that segment. The drag convention is **perpendicular-only**:
- A **horizontal** segment can be dragged **vertically** only (dx zeroed out).
- A **vertical** segment can be dragged **horizontally** only (dy zeroed out).
- Dragging along the segment's own axis is not offered (delta constrained in `HandleSegmentDragLive`, not as a fixup after).

This preserves orthogonality automatically — moving a segment perpendicular to itself only lengthens/shortens its adjacent neighbors.

**Rubber-band: a segment move NEVER breaks a connection.** The dragged segment always translates by the perpendicular delta; whatever is connected is held in place by adding jog segments (`OrthogonalRoute`) rather than detaching:
- An **outer endpoint connected to anything** is held connected — but *how* depends on the drag axis (`ShouldPinDraggedEndpoint`):
  - a **port** or a coincident **wire vertex/corner** (a fixed point) is *pinned*: held in place with a jog bridging it to the moved segment;
  - an endpoint on a wire **body** is pinned only if that body is **perpendicular** to the drag (it would move off). If the body is **parallel** to the drag, the endpoint **slides along it** (no jog) — e.g. a horizontal wire joining two vertical wires slides down them as one straight segment, staying exactly 2 junction dots (a jog there would run along the verticals and spawn bogus extra dots).
- **Sliding is clamped** (`ComputeSlideClamp`): the perpendicular delta is bounded so a sliding endpoint can never run off the end of the wire it rides — the connection is never lost. Ranges from all sliding endpoints (and every wire each touches) are intersected, so the drag **stops at the shorter wire's end** when two parallel wires differ in length.

### Collinear overlap simplification
Two **collinear** wires (same line) whose spans overlap or abut are redundant and merge into their **union** (`WireGeometry.TryMergeCollinearOverlap`, tried in `TryBuildMergeCommand` **before** the endpoint-coincidence merge — the endpoint merge builds a back-tracking path that `NormalizePoints` would collapse, dropping part of the span when two collinear wires share an endpoint; guarded by `OverlapMergeBuriesT` so a merge can't bury a T-junction with a third wire). A connector wire dragged until both its ends coincide collapses to a zero-length wire; `DotRevalidationCommand` (the central post-edit cleanup wrapping every `Execute`) removes any wire that normalizes to &lt; 2 distinct points — undoably — so no stray zero-length wire (and its bogus junction dot) is left behind. A junction **dot** is only drawn where incident segments form a real *branch* — the auto-dot rule requires incident segments spanning **both axes** (a horizontal AND a vertical one), so 3+ *collinear* segments meeting (overlapping wires) draws no dot. Connectivity (`autoDotKeys`, endpoint-connected state) still counts any incident ≥ 3, so a collinear-overlap endpoint reads connected (no false red dot) even though no junction dot shows.
- When **both** outer ends are pinned (a single connected segment), jogs are added at **both** ends so the wire **bows out** — it is no longer frozen.
- Wires connected **on** the dragged segment follow it by the same delta so their junction stays attached: a **stem** T-ed onto its interior, a wire joined at one of its **moving vertices**, and a **user crossing-dot** on its interior (the dot slides along the stationary crossed wire). All folded into the one undoable `MoveCommand` (incl. `DotMoveSnapshot`), so a single Undo restores the wire, its followers, and the dots together. If a drag carries the wire entirely off a crossing, the now-invalid dot is removed by re-validation at commit (per the §5.1 dot invariant).

Each segment drag commits as a single `MoveCommand` with a `WireMoveSnapshot` (old points → new points), undoable. Live preview uses `SchematicOverlay.WireDragPoints` — no `BuildRenderModel()` per tick.

**Selection model**: `_selectedSegment: (string WireId, int SegmentIndex)?` in `SchematicViewModel` tracks the segment selection separately from `SchematicSelection` (which holds whole-object IDs). `SchematicOverlay.SelectedWireSegment` carries it to the renderer. Rubber-band selection still selects whole wires (`HitKind.Wire` from `TestRect`), not individual segments.

### Esc-key contract (both schematic and symbol editors)
**Rule:** Esc cancels whatever is in progress and returns to the Select tool. With nothing in progress (idle in Select), Esc clears the selection. Same semantics in both editors.

**Schematic (`SchematicViewModel.OnKeyDown` + `SchematicView.axaml.cs.OnKeyDown`):**
- `HasActiveOperation` is true when any non-Select tool is active, or a drag/rubber-band/segment-drag/inline-edit is in progress in Select mode.
- `if (HasActiveOperation) SetSelectTool(); else Selection.Clear();`
- `SetSelectTool()` sets `ActiveTool = Select`, calls `CancelCurrentOp()` (clears ghost/wire/drag state), then calls `_placementService?.Disarm()` if the previous tool was `Tool.Place`.
- **ARM-lives-in-PlacementService gotcha:** `ActiveTool = Select` alone does NOT disarm an ARMed palette item. The arm state lives in `PlacementService.Pending`. Always call `Disarm()` when leaving `Tool.Place` — `SetSelectTool()` does this automatically since the fix.
- Inline-edit Esc: `OnInlineEditKeyDown` calls `CancelInlineEdit()` + `DismissInlineEditBox()` + `SetSelectTool()`.

**Symbol editor (`SymbolEditorViewModel.OnKeyDown`):**
- Text-typing Esc → `CancelOp(); ActiveTool = Select`.
- Pin Esc → `ActiveTool = Select` (triggers `OnActiveToolChanged` which resets pin state).
- Any other in-progress op Esc → `CancelOp(); ActiveTool = Select`.
- Idle Esc → `ClearSelection()`.
- No `PlacementService` in the symbol editor — no Disarm needed.

### Wire draw-mode cursor and finish gestures (Phase 6d)
- Wire tool cursor: `StandardCursorType.Cross` (reverts to Default when tool changes away from Wire).
- **Enter** or **double-click** finishes the in-progress wire (keeps what was drawn) and returns to Select tool.
- **Esc** discards the in-progress wire (via `CancelCurrentOp`) and returns to Select.
- `< 2` distinct points: discarded, nothing placed.

### Incremental render rule (Item 1 / perf)
During an **active drag or nudge**, do NOT call `BuildRenderModel()` per tick. Update
`SchematicOverlay.ComponentDragPositions` / `WireDragPoints` only (O(k) for k moved objects).
`BuildRenderModel()` is deferred to drag-END. The connectivity pass inside `BuildRenderModel()`
is O(N) via spatial hash — never revert to the O(N²) linear scan.

### Display name registry (Item 8)
`ComponentTypeRegistry` in `src/Ui/Schematic/ComponentTypeRegistry.cs` maps `SymbolKind` →
`(DisplayName, InstancePrefix)`. **Always** read `ComponentTypeRegistry.DisplayName(kind)` for
the on-schematic type label — **never** call `kind.ToString()` or hard-code abbreviations in the
renderer. When the component model gains a richer type system, re-key the registry off that type.

### Id not persisted (Item 2)
`Id` on `EditableComponent`, `EditableWire`, `EditableNetLabel`, `EditableDot`, `EditableCanvasObject`
is **runtime identity only** — it must NOT appear in any persisted file (`.csch`, `.cws`, `.csym`,
`.cdd`). Fresh Ids are auto-generated on import. Tests compare content, never Ids.

### Move-Labels op (Item 14)
**F5** or the right-click "Move Labels" context menu entry enters Move-Labels mode.
- Phase `Picking` (nothing selected): next click picks which component to move.
- Phase `WaitFirstClick` (components already selected): next click sets the drag reference point.
- Phase `Moving`: mouse moves show live preview via `SchematicOverlay.LabelDragOffsets`; second click commits as a `MoveLabelsCommand`. **Esc always returns to Select.**
- Label offsets persist in `.csch` as `LabelOffsets: [[dx,dy],…]` on each component (omitted when all zero).
- `SchematicOverlay.LabelDragOffsets` carries `{compId → (DX,DY)}` during the drag. The renderer applies the delta on top of any existing per-label offset stored in `SchematicComponent.LabelOffsets`.

### Clipboard (Item 15)
`SchematicClipboard.CopyAsync()` places four formats on the clipboard simultaneously via Avalonia's `DataTransfer` API (richest first):
- `PdfNativeMacFormat` (`com.adobe.pdf` UTI, macOS) / `PdfNativeWinFormat` (`application/pdf`, Windows) — PDF bytes via `SKDocument.CreatePdf()`; recognised by Keynote, Preview, Pages, etc.
- `SvgNativeFormat` (`public.svg-image` UTI) — SVG bytes for macOS/Linux vector apps (Illustrator, Inkscape). Omitted on Windows (no well-known SVG clipboard format; EMF would be the Windows vector path — needs System.Drawing.Imaging + Svg.NET, follow splotRF's `WindowsClipboard.cs`).
- `DataFormat.Bitmap` — Avalonia `Bitmap` (PNG-backed raster; universal fallback for Keynote, Pages, Word, etc.).
- `DataFormat.Text` — JSON text (primary for Paste; cross-session portable). Always present even if rich formats fail.
- **Paste** reads `DataFormat.Text` (JSON) and wraps in `SchematicPasteCommand` (undoable).
- **Ctrl/Cmd+C / +X / +V** in the canvas raise events on `SchematicCanvas` (`ClipboardCopyRequested` / `ClipboardCutRequested` / `ClipboardPasteRequested`) handled async in `SchematicView.axaml.cs`.
- The JSON payload carries **`GridSize`** (= `P_src`). On paste, `SchematicPasteCommand` compares it to the destination `GridSize`; cross-grid content is snapped to `P_dst` and a warning is posted (see Grid & Connectivity below).

### Grid & connectivity (Phase 6 — standing rules)

**Authority:** `docs/design/grid-and-connectivity.md`. This section is a quick-reference; the design doc is the source of truth.

**Two grids, two jobs — never conflated:**
- **Connection grid `P`** (`GridSize`, default 100): every pin-in-world, wire endpoint, wire bend, junction dot lands on it **exactly** (integer multiple — equality not tolerance). Connection = coordinate equality, not proximity.
- **Authoring grid `p = P/k`** (`AuthorGridSize`, default `k=20` → `p=5`): label offsets, net-label positions, canvas objects. Use `SnapToAuthorGrid` for these. **Never** use `SnapToAuthorGrid` for electrical connection points.

**On-grid invariant (R7):** after any edit, every pin world-coordinate, wire vertex, and junction dot is an exact `P` multiple. `OnGridInvariantTests` guards this. Do not introduce any edit path that bypasses `SnapToGrid`.

**`SnapToGrid` vs `SnapToAuthorGrid`:**
- `EditModel.SnapToGrid(v)` → snaps to `P` — use for all electrical points (component origin placement, wire endpoints, wire bends, segment drag).
- `EditModel.SnapToAuthorGrid(v)` → snaps to `p` — use for label drag (`ComputeLabelDelta`), canvas-object drag. **Never** use this for wires or component origins.

**`ConnectTolerance = 0.5`** (float-dust guard only): connectivity is established at *input* by snapping to `P`, not by tolerance afterward. Do not raise `ConnectTolerance` or use it to bridge real gaps.

**Net labels are NOT on any grid:** a net label's position carries no electrical meaning. `EditableNetLabel.X/Y` may be any value. Do not snap net-label positions to `P` or assert them in R7.

**Cross-grid paste (§5):** `SchematicPasteCommand` accepts `sourceGridSize` and `messageSink`. When `P_src ≠ P_dst`, it snaps component origins and wire vertices to `P_dst` (using `Math.Round(v/P)*P`), canvas objects to `p_dst`, posts a `Warning` to `IMessageSink`, and validates R7 post-snap — all in the constructor so Execute/Undo/Redo are clean. `CopyAsync` embeds `model.GridSize` in the JSON; `PasteAsync` returns it; `SchematicView.axaml.cs` threads both through to the command.

## Phase 6b shell conventions (locked in — do not deviate without discussion)

### Dock library
- **Dock.Avalonia 12.0.0.2** + **Dock.Model.Mvvm 12.0.0.2** + **Dock.Avalonia.Themes.Fluent 12.0.0.2** —
  all three are required. Theme is loaded via `StyleInclude Source="avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml"`.

#### Dock color system — how it works and how to override it
The theme's accent colors are defined in `avares://Dock.Avalonia.Themes.Fluent/Accents/Fluent.axaml`
(source: `src/Dock.Avalonia.Themes.Fluent/Accents/Fluent.axaml` in the wieslawsoltes/Dock repo).
Two tiers of resources exist:

**Tier 1 — primary accent family (hardcoded VS blue):**
```
DockApplicationAccentBrushLow      #007ACC  ← active tab background, splitter drag
DockApplicationAccentBrushMed      #1C97EA  ← hover tab background, splitter hover
DockApplicationAccentBrushHigh     #52B0EF  ← close-button hover
DockApplicationAccentForegroundBrush #F0F0F0 ← text on active (accent) tabs
DockApplicationAccentBrushIndicator #007ACC ← dock target indicators
```

**Tier 2 — StaticResource aliases** (resolved at theme load time, pointing into Tier 1):
```
DockTabActiveBackgroundBrush  → DockApplicationAccentBrushLow
DockTabActiveIndicatorBrush   → DockApplicationAccentBrushLow
DockTabHoverBackgroundBrush   → DockApplicationAccentBrushMed
DockTabCloseHoverBackgroundBrush → DockApplicationAccentBrushHigh
DockSplitterHoverBrush        → DockApplicationAccentBrushMed
DockSplitterDragBrush         → DockApplicationAccentBrushLow
DockSurfaceHeaderActiveBrush  → DockThemeAccentBrush → {DynamicResource SystemAccentColor} ✓
```

**Key rule:** `Application.Resources` wins over `StyleInclude` resources for `{DynamicResource}` lookups.
Overriding a Tier-1 key in `Application.Resources` fixes places that reference it directly.
BUT StaticResource aliases (Tier 2) are resolved at load time — their VALUE is baked in as the
original brush object. To fix those, you must ALSO override the Tier-2 alias keys directly in
`Application.Resources`. Both tiers are overridden in `App.axaml`'s `Application.Resources` block.

**Note:** `DockSurfaceHeaderActiveBrush` (tool panel active title bar) already chains to
`SystemAccentColor` via `DockThemeAccentBrush`, so it was correct without any override.

**What controls which UI element:**
- Active document tab strip item background: `DockTabActiveBackgroundBrush`
- Separator line between tab strip and content: `DockTabActiveIndicatorBrush`
- Tool panel active title bar (Project/Properties/Messages): `DockSurfaceHeaderActiveBrush`
- Splitter bar on hover/drag: `DockSplitterHoverBrush` / `DockSplitterDragBrush`
- **`CircuitRfDockFactory`** extends `Factory`; owns the layout tree. It exposes `MessagesTool?`,
  `ProjectTreeTool?`, `DocumentDock?`. Use `factory.OpenDocument(stub)` to add tabs in 6c+.
- Dock Tool/Document subclasses live in `src/Ui/ViewModels/Dock/`. Their views are wired via
  **`DataTemplate`** in `App.axaml` (NOT ViewLocator — Dock resolves its own templates from the
  `Application.DataTemplates` list).
- `SetFocusedDockable(IDock, IDockable)` requires the parent `IDock` container, not the tool itself.
  When programmatically activating a tool, use `SetActiveDockable(tool)` only unless you hold a
  reference to the container.
- **GOTCHA — document bodies must use the CACHED (non-deferred) content template.** Dock's default
  `DocumentControl` template (`DockDocumentControlSingleContentTemplate`, Fluent theme) wraps each
  document body in a **`DeferredContentControl`** that realizes content on a *background-priority
  dispatcher timeline*. The DataTemplate resolves correctly and the right view IS built — but its
  first **paint is deferred** and (in our app) does not flush until the next layout pass, so a
  newly-activated document stays unpainted (the previous tab's stale view lingers) until something
  forces a relayout (e.g. toggling a panel). The `IDeferredContentPresentation { DeferContentPresentation
  => false }` opt-out did NOT help (its readiness gate fails at the instant content swaps). **Fix
  (in `App.axaml`):** override `DocumentControl.Template` to Dock's other built-in template,
  `DockDocumentControlCachedContentTemplate`, which hosts each dockable in a plain `ContentControl`
  (no `DeferredContentControl`, no timeline) and paints on the normal layout pass:
  `<Style Selector="dockCtrl|DocumentControl"><Setter Property="Template"
  Value="{DynamicResource DockDocumentControlCachedContentTemplate}"/></Style>`. Trade-off: all open
  document bodies stay realized (cached) instead of lazily built — negligible at our tab counts, and
  tab switching becomes instant. Document views still resolve via the `App.axaml` DataTemplates.

### Command / undo-redo infrastructure — per-document stacks, focused-window routing

**The rule (do not violate):** Undo/Redo is per-document, resolved by the focused window.
- Each editable document VM (`SchematicViewModel`, `SymbolEditorViewModel`) owns its **own**
  `UndoRedoStack` (created internally, exposed as `public UndoRedoStack UndoRedo`).
- Each document wrapper (`SchematicDocument`, `SymbolEditorDocument`) implements `IUndoableDocument`
  (in `src/Ui/Commands/IUndoableDocument.cs`) and forwards `UndoRedo` to its VM.
- **Focused-window rule:** every window's Undo targets the document it is showing, never another.
  - **Main `WorkspaceWindow`:** `WorkspaceViewModel` tracks `_factory.DocumentDock.ActiveDockable`
    via `OnDocumentDockPropertyChanged`; its `Undo`/`Redo` commands route to that document's stack.
    Switching tabs re-subscribes `PropertyChanged` on the new stack so enable-state updates correctly.
  - **Tear-off windows** (`SymbolEditorWindow`): `Window.KeyBindings` bind to the document's own
    `ViewModel.UndoCommand`/`RedoCommand` directly — fully independent of `WorkspaceViewModel`.
  - **Dock-floated dockables** (user drags a tab into its own floating window): `WorkspaceViewModel.
    TryWireHostWindowsUndo` detects the new `HostWindow` via `ApplicationLifetime.Windows` scan
    (deferred one frame) and injects matching `KeyBindings` pointing at the floated document's stack.
    The host window's `Closed` event cleans up the subscription and removes it from `_wiredHostWindows`.
- **One keystroke owner:** do NOT handle undo/redo keys both at the canvas level and at the window
  level — choose one. The window `KeyBindings` are the authoritative path; canvas `OnKeyDown`
  handlers must **not** call `_undoRedo.Undo()` directly (and must set `e.Handled = true` for any
  keys they DO consume, so they don't bubble to the window binding and fire a second time).
- **Cross-document undo is impossible:** undoing in a symbol can never revert a schematic edit.
- **The Parameter Editor has NO independent stack.** It commits through `_schematicVm.Execute(...)`,
  so parameter edits live in the owning schematic's history and are undoable from that schematic.
  `ParameterEditorViewModel.UndoCommand`/`RedoCommand` delegate to `_schematicVm.UndoRedo` — they
  are only an affordance so the user can undo a parameter edit while the dialog is focused; they do
  not own a separate stack. Never give an inspector/properties panel its own undo stack.
- All user mutations still route through `IUiCommand` → the document's own `UndoRedoStack.Execute(cmd)`.
  Do not wire mutations that bypass the stack (except global file ops: New/Open/Save).
- Future document types (data display etc.) implement `IUndoableDocument` to participate automatically.
  Their windows follow the same focused-window rule: tear-off → own `KeyBindings`; Dock-floated →
  `TryWireHostWindowsUndo` picks them up automatically.

### Messages / IMessageSink
- **`IMessageSink`** lives in `src/Ui/Messages/` — not in Core/Engine (firewall respected).
  `MessagesTool` implements it. Obtain via `WorkspaceViewModel.Messages`.
- Always post from any thread: `MessagesTool.Post()` dispatches to the UI thread internally.
- Level semantics: Info = neutral status; Success = operation completed; Warning = degraded or
  unexpected but non-fatal; Error = operation failed, user must act.
- Icon + color both carry the level (never color alone — accessibility requirement from ui-design.md §2).

### Toolbar
- Avalonia 12 has **no built-in `ToolBar` control**. Use `Border` + `StackPanel Orientation="Horizontal"`
  with `Background="Transparent" BorderThickness="0"` buttons and `Border Width="1"` separators.

### Icons (Material.Icons.Avalonia 3.0.2)
- Always verify enum names against the `Material.Icons` 3.0.2 DLL before using them —
  many intuitively-named icons don't exist (e.g. `Chip`, `PanelLeftOpen`, `Undo`, `PlusBox`).
  Valid substitutes used in 6b: `IntegratedCircuitChip`, `PageLayoutSidebarLeft`, `UndoVariant`/`RedoVariant`, `FilePlus`.
- Context menus on `TreeView` items: place `<Grid.ContextMenu>` inside the `TreeDataTemplate` Grid
  so the DataContext is the item VM, not the tool VM.

### Fonts
Two font families are embedded in `Assets/Fonts/` and registered as `Application.Resources` in `App.axaml`:

| Resource key   | Family          | Files                                      | License                  |
|----------------|-----------------|--------------------------------------------|--------------------------|
| `DejaVuSans`   | DejaVu Sans     | `Assets/Fonts/DejaVuSans*.ttf`             | Bitstream Vera Fonts     |
| `IBMPlexSans`  | IBM Plex Sans   | `Assets/Fonts/IBM_Plex_Sans/static/*.ttf`  | SIL Open Font License 1.1 |

**IBM Plex Sans is static-only** (no variable fonts) because SkiaSharp does not support variable fonts.

**Avalonia controls** reference them via `{DynamicResource IBMPlexSans}` / `{DynamicResource DejaVuSans}`.

**SkiaSharp renderers** must use `SkiaFonts` (`src/Ui/Renderers/SkiaFonts.cs`) — lazy-loaded
`SKTypeface` instances sourced from the same embedded assets via `AssetLoader.Open()`. **Default
to `IBMPlexSans` for all renderer text** (labels, tick marks, annotations, tables). Fall back to
`DejaVuSans` only when broader Unicode coverage is needed (e.g. non-Latin axis labels).

```csharp
// Preferred — clean, modern, designed for screen
var tf = SkiaFonts.PlexRegular;    // Regular
var tf = SkiaFonts.PlexBold;       // Bold
var tf = SkiaFonts.PlexSemiBold;   // SemiBold
var tf = SkiaFonts.PlexItalic;     // Italic
var tf = SkiaFonts.PlexLight;      // Light

// Fallback — wide Unicode range
var tf = SkiaFonts.DejaVuRegular;
var tf = SkiaFonts.DejaVuBold;
```

Add additional weights by copying the `Lazy<SKTypeface>` pattern in `SkiaFonts.cs` — every static
file in `Assets/Fonts/IBM_Plex_Sans/static/` is embedded and loadable. Never call
`SKTypeface.Default` or `SKTypeface.FromFamilyName(...)` in renderers; that pulls from the host OS
and produces inconsistent cross-platform output.

**Firewall (load-bearing):** all UI-framework code lives here in `src/Ui`. `RfCore`/`Core`/`Engine`/`Cli`
reference **no Avalonia** — an enforced CI check fails the build otherwise (`ui-architecture.md` §3). Keep
Skia *rendering* separable from Avalonia *control hosting* so a re-skin keeps the renderers. The display
layer is circuitRF's own, `DataCube`-native (C1); splotRF is reference material, not a dependency.

## Framework & patterns
- **Avalonia 12**, single codebase for Windows/macOS/Linux. Mirror splotRF's structure, controls,
  and packaging recipes wherever possible — it's our proven sibling app (same stack, MIT).
- **MVVM via CommunityToolkit.MVVM.** Views are thin; logic lives in view models. No simulation
  logic in code-behind.
- **The GUI never simulates the design layer directly.** It always builds/edits the design layer,
  then asks the engine to elaborate and run. Results come back as a **`DataSet`** (named
  single-kind `DataCube`s).


### System Specific Differences Between Window, Linux, macOS
- macOS uses ⌘ symbol instead of Ctrl on Windows and Linux. Ensure this is respected in menus and tooltips text.
- The System file manager in macOS is called the "Finder", while Windows has an "Explorer". Respect this convention in menus and tooltip text when referring to the System File Manager.

## Schematic canvas — the performance-critical control (6c pattern, locked in)

### Render pipeline (do not change)
- **Control.Render → ICustomDrawOperation → ISkiaSharpApiLease → SchematicRenderer.Draw**
  - `SchematicCanvas : Control` — Avalonia host; owns pan/zoom state, pointer handling, DirectProperty bindings
  - `SchematicDrawOperation : ICustomDrawOperation` — captures a snapshot of viewport state; leases the Skia canvas
  - `SchematicRenderer` (static class) — pure Skia; no Avalonia types; re-skinnable
  - **NOT SKCanvasView** — that path is not composited correctly in Avalonia 11+
- See `splotRF/src/Controls/PlotControl.cs` for the proven pattern this is adapted from.

### World ↔ pixel transform
- `panX, panY` = world coords at the top-left corner of the canvas (in world units)
- `zoom` = pixels per world unit
- `px = (wx - panX) * zoom`; `wy = py / zoom + panY`
- Scroll-zoom: record world point under cursor *before* zoom change, adjust pan to keep it fixed after.
- Symbol local coords: 100 units = 1 grid square; standard component width = 300 units (body + leads)

### Performance
- **Viewport virtualization**: `SchematicSpatialIndex` (uniform grid, cell size 1500 world units, `Dictionary<(int,int),List<int>>`)
  — `QueryViewport(vpMinX,Y,vpMaxX,Y)` returns conservative candidate sets for components and wires.
  Never draw everything; the index prunes invisible items before the render loop.
- **LOD (level of detail)**: based on `compPixW = zoom × 300`
  - `< 6px`  → single filled rect (`LodRect` colour); skip all else
  - `< 22px` → body symbol lines only; skip port markers and text
  - `≥ 22px` → full: symbol + port markers (red box for unconnected) + labels
- **Grid**: adaptive — fine grid drawn when spacing ≥ 4px × 3; coarse-only (×10) when spacing ≥ 4px; skipped below 4px.
  Cap at 600 lines per axis.
- **FPS**: `static volatile long SchematicRenderer.LastFrameTicks` written by the renderer each frame.
  `SchematicView.axaml.cs` reads it every 333 ms via `DispatcherTimer` to update the toolbar readout.
  The renderer also draws an overlay in the top-right corner of the canvas (enabled by `ShowFps=True`).

### DirectProperty pattern
```csharp
public static readonly DirectProperty<SchematicCanvas, SchematicModel?> ModelProperty =
    AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicModel?>(
        nameof(Model), o => o.Model, (o, v) => o.Model = v);
```
Model setter rebuilds `SchematicSpatialIndex`, sets `_needsInitialFit = true`, calls `InvalidateVisual()`.

### Initial fit
`LayoutUpdated` event fires when `Bounds` is valid. A `_needsInitialFit` flag triggers `ZoomToFitInternal`
exactly once. The handler unsubscribes itself. Do NOT call `InvalidateVisual()` inside `Render()`.

### Adding a new canvas type (6d Data Display, etc.)
1. Create `MyRenderer` (static, no Avalonia — parallel to `SchematicRenderer`)
2. Create `MyCanvas : Control` with a `DirectProperty<MyCanvas, MyModel?>` and an `ICustomDrawOperation`
   nested class that leases Skia and calls `MyRenderer.Draw`
3. Create `MyView.axaml` with toolbar + `MyCanvas`; wire buttons in `MyView.axaml.cs`
4. Add `DataTemplate DataType="{x:Type ...}"` in `App.axaml`

- **Do NOT render components as individual styled controls.** A 10,000-component schematic will die
  that way. Render the canvas yourself via a custom control (`DrawingContext`, dropping to
  **SkiaSharp** for hot paths), with **viewport virtualization** and a **spatial index** (e.g.
  quadtree/grid) for hit-testing and pan/zoom.
- Lean on Avalonia 12's rendering work (deferred composition, dirty-rect tracking) but verify with
  a large stress schematic in `testdata/`, not a toy one.

## Wiring & auto-routing
- Obstacle-aware auto-routing must avoid drawing wires over placed symbols. Start with **orthogonal
  A\*** over a coarse grid using the spatial index for obstacles; refine later. Keep the router
  separable and testable headless.

## Editing model
- **Undo/redo via the command pattern** across all editors (schematic, symbol). Every mutation is a
  reversible command; nothing mutates model state directly.
- **Copy/paste via the system clipboard**, all or a selectable subset of a schematic, to/from other
  schematics.
- **Hierarchy navigation:** push into a sub-cell's schematic, edit, pop back. Editing a cell affects
  every instance; instance-level parameter overrides (root `CLAUDE.md` → expressions) stay per-instance.

## Data Display
The data-display layer is **circuitRF's own**, built fresh and **`DataCube`-native** (a trace is a slice of
a cube), living under `src/Ui` (see `docs/design/ui-design.md` / `ui-architecture.md`). It is **NOT** taken
from splotRF as a dependency and is not in RfCore. splotRF is **reference material only** — mine its proven
techniques (the three-coordinate-space transform pipeline, Smith/polar/rectangular rendering math, the
placeable-plot canvas, plot/trace/table rendering, MarkerInfoBox, autoscale-with-marker-preservation,
tick snapping, pan/zoom/hit-test) and **re-implement them cleanly against `DataCube`** — do not reimplement
from scratch ignoring splotRF's solved problems, and do not depend on splotRF's code. Keep it
UI-framework-light (clean Skia-render vs thin Avalonia-host split) so it stays re-skinnable and could be
lifted into a shared lib later if ever needed (not now). Support measured-vs-simulated overlay (a lab
Touchstone over a simulated result cube from the `DataSet`).

## "Easy" budgets (testable)
Honor the PRD §12 click budgets (e.g. placed FET → running HB power sweep in ≤ 8 actions). Advanced
settings must remain reachable but must not clutter the default path.

# circuitRF UI Coding Context

These are design-quality standards; the architectural rules above are non-negotiable structural constraints.

## Abstract

circuitRF UI development should prioritize accessibility, clarity, consistency, and professional presentation. Interfaces should follow modern Human Interface Guidelines where practical, using system fonts, semantic styling, adaptive layouts, intuitive interactions, and restrained motion. UI text and workflows should target RF engineers, researchers, technical managers, and advanced hobbyists. The overall goal is to create a polished, trustworthy, efficient engineering application suitable for advanced technical workflows.

---

# 1. Core Design Principles

* Prioritize usability, clarity, readability, and consistency.
* Follow Apple Human Interface Guidelines where practical, but treat them as preferred guidance rather than strict rules.
* Favor clean, minimal, engineering-oriented interfaces.
* Avoid visual clutter and unnecessary animation.
* Design for desktop workflows and technically sophisticated users.

---

# 2. Accessibility

## Text and Typography

* Default font size: 12–13 pt.
* Minimum font size: 9–10 pt.
* Support scaling text up to at least 200%.
* Avoid ultra-light font weights.
* Prefer:

  * Regular
  * Medium
  * Semibold
  * Bold

## Contrast

Use WCAG AA contrast guidance:

* Normal text under 18 pt: minimum 4.5:1 contrast.
* Large text (18+ pt): minimum 3:1 contrast.
* Bold text: minimum 3:1 contrast.

## Visual Communication

* Never rely on color alone to convey meaning.
* Use icons, shapes, labels, or patterns alongside color.
* Support color-blind accessibility.

## Layout Accessibility

* Ensure layouts adapt cleanly to large font sizes.
* Reduce truncation wherever possible.
* Prefer stacked layouts when horizontal space becomes constrained.

---

# 3. Colors

* Use system colors whenever possible.
* Avoid hardcoded colors.
* Support light and dark modes automatically.
* Use colors consistently across the application.
* Do not reuse the same color for conflicting meanings.

---

# 4. Icons and Symbols

## UI Icons

* Use Material.Icons.Avalonia for normal UI icons.
* Maintain consistency in:

  * stroke weight
  * scale
  * detail level
  * perspective

## Cell Symbols

Cell symbols are NOT UI icons.

They serve two purposes:

1. Visual identification of RF/electrical components.
2. Port connection geometry for schematic editing.

Do NOT use Material.Icons.Avalonia for schematic cell symbols.

---

# 5. Layout and Visual Hierarchy

## General Layout

* Group related content visually.
* Use spacing and alignment consistently.
* Prioritize important information near the top-left (LTR layouts).
* Avoid overcrowding controls.
* Ensure controls remain visually distinct from content.

## Content Flow

* Use progressive disclosure for advanced or hidden information.
* Avoid overwhelming the user with excessive detail at once.

## Window and Screen Usage

* Extend backgrounds fully to edges.
* Scrollable regions should fill available space.
* Respect safe areas and window chrome.
* Avoid placing critical controls at the bottom edge of windows.

---

# 6. Motion and Animation

* Use animation only when it improves clarity.
* Keep animations subtle, brief, and purposeful.
* Avoid decorative or excessive motion.
* Use motion to:

  * reinforce navigation
  * indicate scrolling
  * communicate state transitions

---

# 7. Localization and RTL Support

* Support localization using ResX.
* Consider right-to-left layout support where practical.
* Flip directional icons in RTL layouts.
* Do NOT flip icons representing real-world objects.

---

# 8. Typography and Semantic Styles

## Fonts

* Avalonia controls use the system UI font by default — do not override it globally.
* For custom Skia renderers, use **IBM Plex Sans** (`SkiaFonts.PlexRegular` etc.) as the default.
  Fall back to **DejaVu Sans** (`SkiaFonts.DejaVuRegular` etc.) only for wide Unicode coverage.
* Never call `SKTypeface.Default` or `SKTypeface.FromFamilyName(...)` in renderers.
* Create centralized semantic Avalonia styles for any explicit font overrides.
* Avoid excessive typeface variation.

## Hierarchy

Use typography consistently to indicate hierarchy:

* weight
* size
* spacing
* leading

## Leading

* Loose leading improves readability in large text blocks.
* Tight leading may be used in compact lists.
* Avoid tight leading for 3+ line text blocks.

---

# 9. UI Writing Style

## Tone

UI language should be:

* professional
* concise
* direct
* trustworthy
* technically appropriate

Target audience:

* RF engineers
* researchers
* technical managers
* advanced hobbyists

## Writing Rules

* Prefer active voice.
* Use clear verbs for actions.
* Avoid overly clever or playful wording.
* Use consistent terminology.
* Prefer concise labels.
* Avoid unnecessary possessive pronouns.
* Avoid vague “we” language in errors.

## Error Messages

* Make errors actionable and specific.
* Clearly explain:

  * what failed
  * why
  * how to fix it

---

# 10. Data Entry

* Dynamically validate fields while editing.
* Give immediate feedback on invalid input.
* Use numeric formatters for numeric fields.
* Do not request data that can be inferred automatically.
* Never prepopulate password fields.
* Disable Continue/Next actions until required data is valid.

---

# 11. Drag and Drop

## Behavior

* Support drag-and-drop broadly where practical.
* Support both move and copy semantics correctly.
* Support undo for drag operations whenever possible.

## Visual Feedback

Provide:

* drag previews
* insertion indicators
* valid/invalid destination feedback
* progress indicators for long transfers

## UX Expectations

* Keep dragged items selected after drop.
* Support multi-item drag where appropriate.
* Preserve styling when dropping rich text/content.

---

# 12. User Feedback

Feedback should communicate:

* status
* progress
* success/failure
* warnings
* recoverable problems

## Best Practices

* Keep feedback contextual and near related UI.
* Use passive feedback for status updates.
* Use interruptive alerts only for serious or destructive actions.
* Ensure all feedback mechanisms are accessible.

---

# 13. Engineering Expectations for AI UI Code Generation

When generating UI code for circuitRF:

* Prefer Avalonia UI conventions and semantic styles.
* Reuse centralized styling resources.
* Use adaptive/responsive layouts.
* Maintain accessibility standards by default.
* Favor readability and clarity over visual novelty.
* Avoid hardcoded dimensions/colors unless required.
* Use consistent spacing and alignment.
* Keep interfaces efficient for technical workflows.
* Ensure keyboard accessibility where practical.
* Minimize unnecessary dependencies and visual complexity.
* Treat schematic symbols separately from standard UI iconography.

---

# 14. Parameter Editor (Phase 6 / ParameterEditorView)

## One view, two hosts
`ParameterEditorView` (`UserControl`) + `ParameterEditorViewModel` are built once and hosted two ways:
1. **Embedded inspector** — in the Properties region (`PropertiesView`), bound to the active schematic's
   selection via `ParameterEditorViewModel.SetContext(vm)`. Activated by `PropertiesTool.SetActiveSchematic`.
2. **Dialog** — opened on component double-click (`SchematicView.axaml.cs :: OnComponentDoubleTapped`),
   configured via `ParameterEditorViewModel.SetTargetDirect(vm, comp, showClose: true)`. Uses `ParameterEditorDialog` Window.

Neither host decision (coexistence mode, modality) touches `ParameterEditorView` internals — both are
single swappable flags.

## Chosen defaults (owner experiments — change these single points)
- **Coexistence = content-switch** (parameter editor shown when one non-Ground component is selected,
  palette placeholder otherwise). Flag location: `PropertiesView.axaml` `<!-- COEXISTENCE_FLAG -->` comment.
  To switch to stack: replace the outer `Grid` with a `StackPanel` and remove the `IsVisible` toggles.
- **Modality = non-modal** (lets the user see schematic labels update live as they edit). Flag location:
  `SchematicView.axaml.cs :: OnComponentDoubleTapped` `const bool isModal = false;`.

## Units keyed by dimension
Unit ComboBox options come from `ComponentTypeRegistry.UnitOptions(UnitDimension)`, not per-`SymbolKind`.
Each `EditableParameter` carries a `Dimension` field (seeded at placement, type-change, and `FromRenderModel`).
`NumPorts` is never shown as a row. The single Ground/null guard is in `ParameterEditorViewModel.SetTarget`.

## Active-schematic wiring
`WorkspaceViewModel` subscribes to `DocumentDock.PropertyChanged` and calls
`CircuitRfDockFactory.PropertiesTool.SetActiveSchematic(vm)` when `ActiveDockable` changes.
`PropertiesTool` delegates to `EditorVm.SetContext(vm)`.

## Command stack discipline
All edits (expression, unit, ShowOnSchematic, instance name, label visibility) go through `SchematicViewModel.Execute`,
which wraps in `DotRevalidationCommand`. No-change guard in every commit method. `SetParameterVisibilityCommand`
(new in Phase 6) notifies in both Execute and Undo, consistent with all other mutation commands.

---

## Phase 7.0 deliverable — COMPLETE

**Per-run `.npy` results writer** — writes each run's `DataSet`s to disk so a future Data Display UI can
address them without re-running the simulation.

**New files:**
- `src/Ui/Schematic/RunResultsWriter.cs` — framework-free static class (no Avalonia); `SchematicKey`,
  `OwnerIdentity`, `WriteResults`.
- `tests/Ui.Tests/RunResultsWriterTests.cs` — 9 tests covering key derivation (4 cases) and `WriteResults`
  (5 cases: happy path, stale-clear, collision warning, same-owner re-run, empty skip).
- `tests/Engine.Tests/Export/NpyRoundTripAllAnalysesTests.cs` — 7 round-trip tests (S-param Hero 1 × 3,
  Loadpull Hero 3 × 4) ensuring `DataSetExporter → DataSetImporter` is lossless for every analysis type.

**Naming rule (LOCKED):** `<baseDir>/results/<schematicKey>/<analysisName>.npy`
- Cell-homed sole view: `<cellName>` (e.g. `Amp`)
- Cell-homed multi-view: `<cellName>.<viewStem>` (e.g. `Amp.tb2`)
- Loose file: file stem
- Scratch: `Sanitize(scratchId)`

**Collision guard:** `.source` marker file in the results dir; warns and skips if owned by a different cell.
The marker stores the owner identity **relative to `baseDir`** (the workspace root) when the owner lives
inside the workspace (`RunResultsWriter.NormalizeOwnerIdentity`), so **moving the whole workspace anywhere on
disk is NOT a collision** — baseDir, `results/`, and the cells move together and the relative path is stable.
`OwnerIdentity` still computes an absolute path; `WriteRun` relativizes it before storing/comparing. Migration:
a legacy ABSOLUTE marker that mismatches an inside-workspace (relative) owner is treated as a moved workspace
(`SameOwner` adopts + rewrites the marker to the relative form), not a collision. Genuinely different owners
(two cells with the same key, or an outside-workspace owner that keeps an absolute identity) still warn. Tests:
`WriteRun_WorkspaceMoved_AdoptsResultsWithoutCollision`, `WriteRun_DifferentInWorkspaceOwners_StillCollide`,
`WriteRun_DifferentOwner_PostsWarningWritesNothing`. **Do NOT revert the marker to an absolute path.**

**Within-run dedup:** `_2`, `_3`, … suffix appended when multiple analyses share a name in one run.

**`RunResult` change:** `IReadOnlyList<DataSet>?` replaced by `IReadOnlyList<AnalysisResult>?` (record
`AnalysisResult(string Name, DataSet Data)`). `RunResult.DataSets` convenience property preserves existing callers.

**`WorkspaceViewModel` hook:** calls `RunResultsWriter.WriteResults` in the `RunStatus.Success` branch of
`RunAnalysis`, using `baseDir = Path.GetDirectoryName(netlistPath)`.

Gate: Firewall 4/4 · Core 254/254 · Ui 721/721 · Engine 225/225 — all green.
