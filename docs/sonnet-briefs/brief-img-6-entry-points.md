# Brief IM-6 — Every way in: menus, paste, a placed picture, the Project Tree

**Series:** `brief-img-0-overview.md` (§4, D4, D6, D14) · **Tag:** `R-im6-<m>`
**Depends on:** IM-5
**Area:** `src/Ui/Views/WorkspaceWindow.axaml[.cs]` (Design and Edit menus, native and in-window),
`src/Ui/ViewModels/WorkspaceViewModel.ImageRecognition.cs` (new; the commands), `src/Ui/Clipboard/ImageClipboard.cs`
(read), `src/Ui/Controls/LayoutCanvas.cs` and `SchematicCanvas.cs` (bitmap context rows),
`src/Ui/Views/ProjectTree/ProjectTreeView.axaml` + `src/Ui/ViewModels/ProjectTree/` (image rows), `docs/user/src`

---

## 1. Goal

Wherever a user has a picture, the command is one gesture away, and it is never offered where it cannot work.

## 2. Requirements

**R-im6-1 — Design menu.** Below *Create Schematic from Artwork…*: **Create Schematic from Image…** and **Create
Layout from Image…**, enabled whenever a workspace is open (no document needs to be focused). Each opens the IM-5
dialog in its empty state with *Make* preset. In both the native (macOS) and in-window menus, with the tooltip
convention the neighbouring rows use. No default gesture (the L5 rule: nothing runs except from the user's
invocation).

**R-im6-2 — Edit ▸ Paste Image as Schematic… / Paste Image as Layout…** Below *Paste*. Each reads the clipboard's
picture, encodes it to PNG bytes (an `ImageSource` of origin *Bytes*, suggested name `pasted_image_<n>`), and opens
the dialog already reading it with *Make* preset.
- **Enabled only when the clipboard holds a picture**: `DataFormat.Bitmap` among `GetDataFormatsAsync`, or a single
  file whose extension IM-1 reads (a picture copied in a file manager). Read with `ClipboardExtensions.TryGetBitmapAsync`
  / `TryGetFileAsync` (Avalonia 12).
- **When it is re-evaluated**: just before the menu shows (`NativeMenu.NeedsUpdate` on macOS, `SubmenuOpened`
  elsewhere — the pair `EnsureWindowNativeItem` already documents, because `SubmenuOpened` never fires on macOS) and when
  the window is activated (the clipboard changes while the user is in another application). **Never polled.** A
  failed or slow clipboard read leaves the rows disabled, never throws.
- The read lives in `ImageClipboard.TryReadAsync` beside the existing write, on the same two routes: Avalonia's
  clipboard everywhere, plus a Windows P/Invoke read of the registered `PNG` format first when present (the format
  `WindowsClipboard` writes, and the one browsers and screenshot tools put alongside the device-independent bitmap).
- **Cmd/Ctrl+V inside the dialog** pastes into its source pane (R-im5-2). Plain *Paste* on a canvas is unchanged.

**R-im6-3 — A placed picture, right-clicked.**
- **Layout canvas, on a `BitmapShape`** (beside *Resolve Path…* / *Refresh Cache*): **Trace Image into This Layout…**
  and **Create Schematic from Image…**. Tracing into this layout opens the dialog with scale = *As placed*, target =
  *This layout, over the picture* (D14), technology = this layout's. *Create Schematic from Image…* targets a new cell
  and offers *As placed* as its scale evidence.
- **Schematic canvas, on an `EditableBitmap`**: **Create Schematic from Image…** (target: *This schematic, over the
  picture*, D14 — or a new cell) and **Create Layout from Image…** (a new cell; *As placed* is not offered, a schematic
  has no physical scale).
- Rows are absent, not disabled, on a bitmap whose path does not resolve; *Resolve Path…* is the row that is there.
- A **locked** bitmap is offered too — locking stops dragging, not reading.

**R-im6-4 — Project Tree.** A Known File (and an `OtherFile` inside the workspace folder) whose extension IM-1 reads
gets **Create Schematic from Image…** and **Create Layout from Image…**, next to the rows *Copy to Workspace as Cell…*
sits beside — gated by a new `IsImageFile` exactly as `IsKnownFileCopyableAsCell` is gated (extension only, so the
tree never decodes a file to build a menu). A broken Known File (`IsWarning`) gets no row. Each opens the dialog
already reading that file.

**R-im6-5 — Dropping a picture.** Dropping a picture file onto a **canvas** keeps placing a bitmap — unchanged; the
context rows above are the next gesture. Dropping one onto the **dialog** reads it. Dropping one onto the **Project
Tree** keeps adding a Known File. No new drop meaning is introduced anywhere a drop already means something.

**R-im6-6 — Discoverability without prose.** After a bitmap is placed on a layout or schematic, the Messages note the
placement already posts (if any) is unchanged; no tip, no banner. The user page and the menu tooltips are where the
command is explained.

**R-im6-7 — User page.** The IM-5 page gains *Ways in* (the four above, with Paste's enablement rule), and the
Edit-menu, Design-menu and Project Tree tables gain their rows. Edit sources only; **do not run DocGen**.

## 3. Not in this phase
Any change to what a canvas drop does; the CLI (IM-11).

## 4. Gates (minimal tests, run only these classes)
- `PasteImageCommandTests` — with a fake clipboard: a bitmap enables both rows, text alone disables them, a single
  `.png` file enables them, two files disable them, a throwing read disables them; the rows re-evaluate on window
  activation and on menu open, not otherwise (a counter on the fake).
- `BitmapContextRowsTests` — a layout bitmap offers *Trace Image into This Layout…* with *As placed* and this
  layout's technology; an unresolved bitmap offers neither row; a schematic bitmap's layout row offers no *As placed*.
- `ProjectTreeImageRowsTests` — `.png`/`.jpg`/`.webp` Known Files and in-folder `OtherFile`s get the rows; a `.tif`
  does not; a broken Known File does not.
