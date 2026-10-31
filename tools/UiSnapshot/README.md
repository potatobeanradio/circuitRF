# UiSnapshot

Hosts circuitRF's **real** Avalonia views in a headless window and writes a PNG of what they paint. It exists
because the GUI cannot be started from a shell with no window-server session (Avalonia.Native fails at its
render timer), so without it a UI change can be compiled and unit-tested but never looked at.

It is not `circuitrf render`. That verb draws a document through the Skia renderers below the UI firewall —
the picture an export makes. This tool draws the **Avalonia** views: the trace cards, the marker info boxes as
controls, the label strips, the inspector's own layout. Use `render` for what a plot draws and this for what a
window shows.

## Use

```
dotnet build tools/UiSnapshot
dotnet run --project tools/UiSnapshot --no-build -- <file.cdd> -o out.png [--view canvas|inspector|display] \
    [--plot n] [--size WxH] [--results <dir>] [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...
```

| Option | Means |
|---|---|
| `--view canvas` | The active tab's plot canvas (`PlotCanvasView`): plots, legends, label strips, marker info boxes. The default. |
| `--view inspector` | Plot *n*'s Plot Inspector (`PlotInspectorView`): the trace cards. |
| `--view display` | The whole Data Display document view (`DataDisplayView`): toolbar, canvas and tab strip, and its **key bindings** — the view to use for `--key`. |
| `--plot n` | Which plot, 1-based, for `--view inspector`. Default 1. |
| `--size WxH` | The headless window, in DIPs. Default 1000x700. The inspector scrolls inside its own region, so a taller window does not show more cards. |
| `--results <dir>` | Where a relative trace source resolves — a workspace's `results/` folder in the application. Default: the `.cdd`'s own folder, which is where `circuitrf plot --write-cdd` puts a document beside its result. |

**The Signal Designer** is the other view it opens. It takes no `.cdd`:

```
dotnet run --project tools/UiSnapshot --no-build -- --signal "FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0" \
    -o out.png [--signal "<another carrier>"] [--tab 1] [--fc <expr>] [--size WxH] [steps]
```

| Option | Means |
|---|---|
| `--signal "<carrier>"` | One `Carrier[i]` of the MOD_Source the Designer opens on, in the netlist form. Repeat for several. |
| `--tab n` | The preview tab, 0-based: 1 is EVM, Pulse or Frequency. Default 0. |
| `--fc <expr>` | The source's Fc, in GHz. Default 2. |
| `--standalone` | Open it as **Tools ▸ Signal Designer** does, on no MOD_Source, instead of `--signal`. |
| `--iq <file>` | With `--standalone`: read an I/Q file through **Load custom**. |

The window is the real `SignalDesignerWindow`, opened on its view model's `SetTarget` as the inspector's
**Design signal…** opens it, after its preview has finished. Without `--size` it takes the window's own size
(1199x800), and the carrier cards may then be below the fold. Use `--size 1199x1060` to see them.

**The S-Parameter Viewer** opens on one Touchstone file, as a placed SnP's **Preview** opens it:

```
dotnet run --project tools/UiSnapshot --no-build -- --snp <file.sNp> -o out.png [--size WxH] [steps]
```

The status line prints after the file is read and drawn.

**The Messages panel** opens on one row of each kind — plain text, a file link, the Relaunch button, an
expandable row and a live progress bar:

```
dotnet run --project tools/UiSnapshot --no-build -- --messages -o out.png [--dark] [--size WxH]
```

It prints every row's height, its text's top and centre line, and the same for each control hosted inline in it.
A row taller than a plain one, or a control whose centre is off its text's, reads straight off that output.
Default size 700x260.

**The Tuning window** opens on a schematic's ticked Tune rows, the way the shell hosts it:

```
dotnet run --project tools/UiSnapshot --no-build -- --tuning <file.csch> -o out.png [--size WxH] [steps]
```

After every step it prints which control holds the keyboard and each row's value, so what a key did is read
rather than guessed from pixels. Default size 420x500.

**The Optimizer window** opens on a schematic the way the shell hosts it, its circuit prepared as the workspace prepares
it (the extraction, its netlist and the extraction's vendor parts):

```
dotnet run --project tools/UiSnapshot --no-build -- --optimizer <file.csch> -o out.png [--optimize] [--size WxH] [steps]
```

`--optimize` presses Run and waits for the run to finish before the capture. Every run prints the algorithm menu (a
greyed entry, and why), each variable row's discrete text, range and best value, and the status line. Default size
520x640.

**The Model Composer** opens on a `.cmc` as File ▸ Open does, or on a new document of a chosen kind:

```
dotnet run --project tools/UiSnapshot --no-build -- --cmc <file.cmc|new:Passive|new:Active> -o out.png \
    [--step <name>] [--run <key>] [--marker]... [--import <data>] [--size WxH] [steps]
```

| Option | Means |
|---|---|
| `--step <name>` | Selects a step (`Topology`, `Fit`, ...). A step that is not enabled yet stays unselected. |
| `--run <key>` | Runs one of the step's tasks (`propose`, `analyse`, `fit`, ...) to the end, then prints the status line, any proposal note and the proposals. |
| `--marker` | Adds a marker on the S plot's first trace, its info box placed as a new marker's is. Repeat for several. |
| `--import <data>` | Photographs the Import Data dialog instead, with `<data>` chosen as its source. On a `new:` document the importer works against its provisional `.cmc` beside the data, and prints it. |

Without `--size` it takes the window's own size; `--size 1400x866` shows the whole window.

**The EM Setup editor** opens on a `.cem`, as the workspace opens one, and prints every visible named check box's state,
position and tooltip, so a setting is read as well as seen:

```
dotnet run --project tools/UiSnapshot --no-build -- --cem <file.cem> -o out.png [--size WxH] [steps]
```

Default size 900x1200.

**The technology editor** opens on a `.ctech` as the workspace opens one (the file's own content, its libraries
resolved by the editor — copy a `.cmat` it names beside it). `--tab n` picks the section (0 Layers, 1 Stackup, 2 DRC
Rules, 3 Interchange, 4 Materials). Two steps drive its real controls: `--unit <unit>` picks "Unit for new layouts",
`--rename-stackup old=new` types into a stackup card's name box and commits it with Enter, and `--rename-tech <name>`
double-clicks the header's technology name and types into the box that opens (follow it with `--key Enter`, or
`--shot` the open box first). After opening and after every step it prints the name, each thickness box with the unit
label drawn beside it, every via's span, and the editor's problems:

```
dotnet run --project tools/UiSnapshot --no-build -- --ctech <file.ctech> -o out.png [--tab n] [--unit um] \
    [--rename-stackup "Top Copper (1 oz)=Top Cu"] [--rename-tech "My Board"] [--size WxH] [steps]
```

Default size 1100x1400.

**The gdsfactory import's views** (brief-gdf-2, brief-gdf-3), each opened as File ▸ Import PDK opens it, with
`--python` the interpreter that has gdsfactory and `--kit` a kit source tree (the synthetic one is
`tests/Ui.Tests/Gdsfactory/Fixtures/synthkit`): `prompt`, `pick`, `interpreter`, `trust`, `report` (with the
"Also build the kit's technology" footer when the kit states a stack), `report-open`, `tech-overwrite` (the merge
question asked when the kit's `.ctech` already exists, against a copy with one layer recoloured and one
conductivity changed, so its list of colliding items is real), `cell` (with `--clay`) and `symbols` (brief-gdf-4:
the kit's own glyphs made into symbols in a fresh workspace, the kit's palette heading beside a schematic with one
of each placed; every pin prints). `report` also lists those symbols. `palette` (brief-gdf-5, with `--workspace <dir>`, optional
`--category <heading>` and `--cell <query>`) composes the workspace's kit section of the Library palette as a
workspace open does — every referenced kit's parts, every kit generator with its declared model, the parts made from
a kit's own drawings, then `KitPaletteMerge` — prints per kit how many tiles carry a symbol and artwork, a symbol
only, or artwork only, and draws the palette; with `--cell` naming a part that has a symbol, that part is also placed
on a schematic beside it (several, comma-separated, are placed in a row on their own). Every kit is allowed to run. To look at the built
technology itself, build it with `circuitrf import pdk … --technology` and open the `.ctech` with `--ctech`:

```
dotnet run --project tools/UiSnapshot --no-build -- --gdsfactory <step> --python <interp> --kit <tree> -o out.png
```

`pdk-report` opens the ordinary Import PDK report for the folder `--kit` names, imported through
`PdkWorkspaceImport.Import` — the call both File ▸ Import PDK and `circuitrf import pdk` make — into a scratch
workspace. On a kit tree that also holds a gdsfactory package the report says that both are imported, and prints
the gdsfactory half it found. `--python` is not used.

`permissions` opens Settings on Security & Permissions, on a throwaway per-user state directory seeded with three
recorded kit answers (a gdsfactory package allowed, with its interpreter; a script kit refused; a kit no longer on
disk). `permissions-many` adds two hundred more, for the list's own scrolling and the filter. `--kit-row
allow:<kit>`, `deny:<kit>` or `clear:<kit>` clicks that row's Allowed box or Clear button and prints what is now
recorded; `--type FilterBox=<text>` types into the filter. `--kit` is optional here.

**The 3D editor** draws a `.c3d` on the platform's own GPU backend (Metal, D3D11 or Vulkan — what the application
picks), through the editor's view model and Export Picture's own `CapturePicture`. No window is opened, so this mode
starts no Avalonia platform at all:

```
dotnet run --project tools/UiSnapshot --no-build -- --c3d <file.c3d> -o out.png [--size WxH] [--realistic] \
    [--fit] [--ortho|--perspective] [--yaw deg] [--pitch deg] [--distance mm] [--target x,y,z] \
    [--only a+b] [--hide a+b] [--list] [--hover name] [--live] [--select row]
```

| Option | Means |
|---|---|
| (none) | The camera the workspace's `.cwsuser` stored for this view — the one its owner left it at, projection included. |
| `--fit` | The editor's opening fit instead of the stored camera. |
| `--yaw`, `--pitch` | Degrees, as the camera holds them (pitch > 0 is above the target). Applied after the stored camera or the fit. |
| `--distance` | Millimetres from the target; in orthographic it is also the zoom (half the view's height is distance × tan(0.35)). |
| `--target x,y,z` | Millimetres, in the scene-local frame `--list` prints. |
| `--realistic` | The realistic view, once its environment is made. |
| `--only`, `--hide` | Objects by substring of their name, `+` between several — what unticking them in the tree does. |
| `--list` | Every object's name, kind and scene-local bounds (mm). Alone, it writes no picture. |
| `--hover` | The first object whose name contains this is drawn hovered, as the pointer over it draws it. |
| `--live` | Plan the picture as the live view is planned rather than as Export Picture: the realistic view then draws the hover and the selection too. |
| `--select` | Select the Object Tree row of this name (a field plot, an object) first, and lay the 2D overlay over the picture as Copy and Export Picture do: the selection's chrome and a selected clip-plane plot's plane. Only this option starts the headless Avalonia platform. A plot that draws nothing prints why. |

A field plot draws from the workspace's `results/` folder beside its `.cws`, as the application reads it.

**A 3D view's setup cards beside its Properties panel** (the real `C3dSetupAnalysesView` and `C3dPropertiesView` on one
editor view model, results from the workspace's `results/` folder):

```
dotnet run --project tools/UiSnapshot --no-build -- --c3d-setups <file.c3d> -o out.png [--delete-fields <setup>] \
    [--c3d-row <tree row>] [--size WxH] [steps]
```

`--delete-fields` runs that card's Delete Saved Fields, moving the field folders into a `trash` folder beside `-o`
(never the system Trash), and prints its sentence. `--c3d-row` selects an Object Tree row (a field plot) for the
Properties panel, and prints why the plot draws nothing. `--rclick` on a card opens its menu.

Every run prints the camera it drew from, so a picture can be reproduced and nudged. The default size is 1200x800.

**Input steps run in the order given**, as real pointer and key events through the window, before the final
capture. Coordinates are window DIPs — take them from a first capture.

| Step | Does |
|---|---|
| `--click x,y` | Left press and release. |
| `--drag x0,y0:x1,y1` | Left press, ten moves (so it crosses a click threshold), release. |
| `--key K` | Press and release a key: `Back`, `Delete`, `Escape`, or with modifiers, `Meta+Z`, `Ctrl+Shift+A`. |
| `--shot out.png` | Capture an intermediate frame. |

Set `CRF_STATE_DIR` to a scratch folder so the run neither reads nor writes your own preferences.

A quick document to look at: `circuitrf plot run.npy -o x.png --trace … --write-cdd x.cdd`, then snapshot `x.cdd`.

## The layout editor

```
dotnet run --project tools/UiSnapshot --no-build -- --clay <file.clay> -o out.png [--hide <layer>] [--lock <layer>] \
    [--size WxH] [steps]
```

The real `LayoutEditorView` on a `LayoutDocument`, the technology resolved from the document's `TechRef`, fitted.
Messages print as they are posted. Besides the steps below:

| Option | |
|---|---|
| `--hide`, `--lock` | Untick a layer's row in the layer list (its own `Visible`/`Selectable`). Repeat for several. |
| `@x,y` | Any step's point in the layout's micrometres instead of window DIPs, through the canvas's viewport. |
| `--rclick x,y` | Right press and release: the canvas's context menu opens. It is drawn in the window's own frame. |
| `--menu <header>` | Clicks the open context menu's item whose header starts with this (submenus searched), then closes it. A miss lists the headers. |
| `--type <text>` | Types into the open dialog's first text box, replacing what is there. `--type Name=text` sets the text box of that `x:Name` instead (an inline box such as `SizeInput`); with `--cem` and no dialog open it types into the EM Setup editor (`--type MemoryLimitBox=8 --key Enter`), and `--button Mesh` presses a toolbar button by its label. |
| `--button <label>` | A real click on the open dialog's visible button with this label. A miss lists the buttons. |
| `--impedance <freq\|none>` | Runs the editor's own Impedance Analysis with that highest frequency (`6GHz`, or `none`) before the steps, and prints each trace's findings. The canvas shows its overlay. |
| `--finding <kind>` | With `--impedance`: selects the first finding of that kind (`pad-shunt`, `return-broken`, …) as the panel's row does, and zooms to it. The selection status prints. |
| `--impedance-layer <name>` | With `--impedance`: run on this copper layer only. Repeat for several. |
| `--impedance-panel` | With `--impedance`: the real Impedance panel on the editor, waited until its export page counts are in. The export choice and its note print, and `<out>.impedance.png` is the panel scrolled to **Export PDF…**. |
| `--review` | With `--clay`: runs the editor's own Layout Review and draws its markers on the canvas, as the open Review panel does. Each finding prints. |
| `--review-panel <seed>` | The real Review panel on a layout with no artwork, its result handed in: `checks` (the checks list with one threshold edited through its row), `result` (a not-run check, an accepted finding and a check with no findings) or `many` (5,000 findings in one check, its layers opened) or `mmic` (the switch set to MMIC, the checks of a die). Prints the rows the list holds against the row containers it realised — the proof that it virtualises — and each check's line. `-o` is the panel. |
| `--symbol <stem>` | Writes a component's documentation symbol, light and dark (`<stem>.svg`, `<stem>-dark.svg`), through the generator DocGen uses, into `-o`'s directory (default `docs/user/assets/symbols`). Opens no window. |

A capture while a dialog is open also writes `<out>.dialog.png`, the dialog's own window. A commanded "open" of
another document (Crop to New Cell's new cell) prints `opens <path>`; there is no workspace to open it in.

A drag that STARTS on a shape moves it, as in the editor: begin a marquee off the board.

## The picture dialog

```
dotnet run --project tools/UiSnapshot --no-build -- --image <picture> -o out.png [--tech <file.ctech>] [--digitise] \
    [--make-schematic] [--size WxH] [steps]
```

The real `CreateSchematicFromArtworkDialog` on an `ImageSourceViewModel`, as **Create Layout from Image…** opens it
(**Create Schematic from Image…** with `--make-schematic`; **Tools ▸ Digitise Image…** with `--digitise`, which opens
empty and is handed the picture as a drop is). The workspace is the picture's folder and `--tech` its one technology —
copy the technology's material library (`.cmat`) beside it, or the technology does not load and the dialog says there
is none. Each step waits for the reading to settle, then prints one line: the title, the kind read, what Make makes,
the scale, the armed tool and the canvas prompt. `--type`, `--button`, `--click` and `--drag` act on the dialog itself;
`--hover x,y` moves the pointer there, which shows the line saying what the item under it is.
`--scale-points x0,y0,x1,y1=<distance>` sets the scale by Two points… in the picture's pixels before any step.
`--strip-geometry` checks that the layout tool strip never moves: it reads the bounds of every tool, the legend and the
options slot with nothing armed, each tool armed by a click, a hand via selected and Other… chosen, prints them, and
exits 1 if any differ.

## The symbol editor

```
dotnet run --project tools/UiSnapshot --no-build -- --csym <file.csym> [--pick x,y] [--dark] -o out.png [steps]
```

The symbol editor beside its Properties inspector. `--pick x,y` (symbol coordinates) clicks there through the
editor's own pointer handling, so whatever a user's click would select is selected; without it, the centre of the
first partly filled shape is clicked. After every step the Fill box, the slider and the selected shape's stored fill
print, with the refusal when a typed fill was refused (`--type FillText=abc --key Enter`). `--dark` renders in the dark theme
here and in every mode that opens through this section's and the vendor-library section's views.

## Vendor libraries

```
dotnet run --project tools/UiSnapshot --no-build -- --palette <workspace> [--category <name>] [--search <q>] -o out.png [steps]
dotnet run --project tools/UiSnapshot --no-build -- --schematic <file.csch> [--select <instance>] -o out.png [steps]
dotnet run --project tools/UiSnapshot --no-build -- --archive <workspace> -o out.png
dotnet run --project tools/UiSnapshot --no-build -- --manage-libraries <workspace> -o out.png
dotnet run --project tools/UiSnapshot --no-build -- --add-vendor-library <folder> -o out.png
```

Each first indexes the workspace's referenced vendor libraries, as a workspace open starts them, so captions, counts
and lists are filled in. `--palette` is the Library palette with a placement service (so a row arms), and prints the
focus and the armed row after every step. `--schematic` is the schematic view beside the Parameter Editor on
`--select`'s instance, the session given Tuning, Yield and Optimizer panels as the shell gives every session (so
the Inspector shows its Tune, Tolerance and Optimize toggles); `--type VendorPartBox=<text>` types into the Part
field, and a vendor part's two toggle states print after every step. `--manage-libraries` and
`--add-vendor-library` open their dialog over an empty window (`<out>.dialog.png`). A list drop-down opened by a
popup is drawn in the window's own frame, and so is a button's menu flyout opened by a real `--click` on the button
(the palette's Type menu and its sub-menus). `--palette` prints the Type menu's state, item count and the selection
after every step — a menu that opens with no items shows up as a near-zero presenter.

## How it is faithful

- **The display is opened by `DisplayWindowViewModel.LoadAllAsync`** — the GUI's own File ▸ Open — so the plots,
  traces, markers and inspectors are built by the code the application builds them with.
- **`SnapshotApp` merges the application's own styles, resources and view templates** (taken from a constructed
  `App`, whose `Initialize` loads XAML and starts nothing). Unlike `tools/DocGen`'s `DocsApp` it merges no
  docs-only paint remap, so the colours are the application's.

## Traps

- **`UseHeadlessDrawing = false` is required.** The default stubs drawing out and every capture is blank, with no
  error.
- **Never block on a `Task` on the UI thread** (`GetResult()`, `.Wait()`): its continuations are posted to the
  same dispatcher and the process hangs. Pump `Dispatcher.UIThread.RunJobs()` until it completes — `Await`
  in `Program.cs`.
- **A frame needs pumping**: `RunJobs()` + `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` + `UpdateLayout()`,
  a few times, before `CaptureRenderedFrame()`.
- **A relative source with no results root loads no traces, silently** — an empty plot, not an error. That is
  what `--results` is for.
- **Two clicks on one spot in quick succession are a double-click** — a `--click` followed by a `--drag` from the
  same point opens a plot's inspector. Start the drag first, or click somewhere else in between.
- **A key binding fires only while focus is inside the view that declares it.** Deleting the box that had focus
  leaves it nowhere, so a following `--key Meta+Z` does nothing here; in the application the shell's Edit menu
  serves that key. Click the toolbar's undo button instead.

## Adding a view

Build the view model the way the application does (its own load or factory, never a hand-assembled copy),
put the real view in the `Window`, pump, capture. To drive an edit, raise the real event on the real control —
`new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box }` commits a text box
through its own handler — and capture again.
