# Sonnet Brief — Layout labels: invisible on PCB technologies, and Flatten-to-Polygon for text

Owner report: placing a label with the toolbar button appears to do nothing. Plus a request: a way to
flatten a label to polygons.

**The label machinery is not broken.** The tool, the typing buffer, the ghost caret, Enter/Escape/Backspace,
`CommitLabel` and `DrawLabelText` are all present and correct. The label is created — it is just **too small
to see**.

---

## 1. Bug: the default label height is a hardcoded absolute size

`LayoutEditorViewModel.cs` line 1260:

```csharp
private long _labelHeightDbu = 5_000;   // arbitrary reasonable default (5 um at 1000 dbu/um)
```

5 µm is a sensible label on a GaAs die. On the **PCB starter technology** it is not: L0c seeds a new layout's
snap from the technology (1 mil = 25,400 DBU), and the default viewport frames roughly 200 snap steps ≈
**20 mm**. A 5 µm label in a 20 mm viewport is **1/4000 of the view width** — comfortably sub-pixel. The
shape lands in the model and renders at a size no display can show. `DrawLabelText` even clamps the font to
`Math.Max(0.001f, ...)`, so it draws something infinitesimal rather than nothing.

This is the same failure mode as the L1a default-zoom bug: **an absolute constant chosen at one scale, in a
database whose scales span six orders of magnitude.**

The user also gets no feedback that anything happened, because the in-progress ghost — including its `"|"`
caret — is drawn at the same invisible height. From the outside it looks like a dead button.

### R-lbl-1. The default label height comes from the technology

Add `DefaultLabelHeightDbu` to `Technology`, seeded in both starter technologies (suggest **40 mil** for
PCB 2-Layer, **5 µm** for MMIC GaAs), and initialise `_labelHeightDbu` from the resolved technology exactly
as L0c seeds `DisplayUnit` and `SnapDbu`. Fall back to today's 5 µm only when no technology resolves.

**Why technology-provided and not viewport-relative** — note the deliberate contrast with the bitmap brief's
R-bmp-4, which *is* viewport-relative. A bitmap is a reference image the user resizes immediately, so
"sensible on screen right now" is the right default. A label height is a **drafting convention of the
process or board**, wanted consistent across a design and across sessions; deriving it from the current zoom
would make the same command produce different persistent geometry depending on how far the user happened to
be zoomed in. Different defaults for different reasons — say so in the code comment, because the two look
inconsistent otherwise.

### R-lbl-2. Typing mode must be visible

Even at a correct height, the user currently has no indication that clicking with the Label tool armed a
typing mode. Add a status-bar hint while `_isTypingLabel` — *"Typing label — Enter to commit, Esc to
cancel"* — matching the `n of m` readout convention from L1c.

**Additionally**, if the ghost would render below ~4 device pixels, draw the caret and text at a **minimum
on-screen size** so the user can see that typing is happening, and note in the status hint that the label is
smaller than the current zoom can show. A silent invisible ghost is what made this look like a dead button
rather than a sizing problem.

### R-lbl-3. Space must not arm the pan modifier while typing a label

`LayoutCanvas.OnKeyDown` line 502 intercepts Space unconditionally:

```csharp
if (e.Key == Key.Space) { _spaceHeld = true; UpdateCursor(); return; }
```

It does not set `e.Handled`, so `TextInput` still delivers the space to the label buffer — spaces in label
text most likely work. But `_spaceHeld` is now **true while the user types**, so the canvas is in
space-pan-modifier state mid-label and a subsequent left-drag pans instead of doing what it should. Guard the
Space branch with `if (_viewModel?.IsTypingLabel != true)`. **Verify the space-in-text behaviour empirically
before changing anything else** — if spaces are in fact dropped, that is a second defect in the same line.

---

## 2. Feature: flatten a label to polygons

Not implemented. §3.1 already specifies it: *"If a fab needs a text marking as real copper, that is an
explicit 'convert text to polygons' command using a stroked vector font."*

### R-lbl-4. Extend the existing "Flatten to Polygon…" command to labels

Do **not** add a second flatten entry. L1h deliberately collapsed the menu to one entry that always prompts;
a separate "Convert Text to Polygons" would reintroduce exactly the confusion that cleanup removed. Instead:

- **Enablement** widens: enabled when the selection contains a curved primitive **or** a non-port `Label`.
- The dialog **names what will happen**: *"3 curves and 1 label will become polygons."*
- The same tolerance governs both, since glyph outlines are Bézier curves and need flattening too.

### R-lbl-5. Port labels are refused

A `Label` with `IsPort = true` is a terminal marker that §9 and §10.6 key on. Flattening one silently
destroys a port. **Exclude port labels from the operation**, count them in the dialog (*"1 port label will be
skipped"*), and never flatten one even when explicitly selected.

### Implementation

1. **Glyph outlines** from SkiaSharp — `SKFont.GetTextPath` (or `SKPaint.GetTextPath`, whichever this
   SkiaSharp version exposes) using **`SkiaFonts.PlexRegular`, the same typeface `DrawLabelText` renders
   with**. Same font in both paths is what makes the result WYSIWYG, and a bundled font makes it
   deterministic across machines.
2. **Transform** the path into DBU: scale by the label's `Height`, translate to `X`/`Y`, apply `Rotation`.
   Mirror `DrawLabelText`'s existing transform exactly rather than re-deriving it — including its Y-down
   path-space rotation-sign comment, which is easy to get backwards.
3. **Flatten** the glyph curves at the dialog's tolerance, reusing `LayoutFlattener`. No second flattener.
4. **Resolve nesting with Clipper2** (L1e): run the flattened contours through a `Union` into a
   `PolyTree64`, which yields outer rings and holes directly. **Do not** try to infer outer-vs-hole from
   contour winding by hand — glyph fill rules vary by font and it is a classic source of filled-in letters.
5. **Emit** one `PolygonShape` per outer ring, **with its holes**, on the label's layer, carrying its net.
   Holes are not optional here: `o`, `e`, `A`, `8` are all counters. This is a good end-to-end exercise of
   §3.1a — without hole support every rounded letter would come out as a blob.
6. **One `ReplaceShapesCommand`** (1→N) per label, so undo restores the `LabelShape` at its original index.

**After flattening the text is geometry, not text** — it is no longer editable as a string and will now be
exported. Say so in the dialog and the tooltip; unlike flattening a circle, this changes *what kind of thing*
the object is.

---

## 3. Scope guardrails

- No new menu entries; extend the existing Flatten command (R-lbl-4).
- No text-editing features: no font choice per label, no bold/italic, no multi-line, no alignment.
- No changes to `DrawLabelText`'s rendering beyond the minimum-size ghost in R-lbl-2.
- No changes to port semantics — port labels are simply excluded (R-lbl-5).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, or the symbol editor.

## 4. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **The headline** — on the **PCB starter technology**, place a label at the default viewport and assert
   its rendered height is **at least a few device pixels**. This test fails against today's hardcoded 5 µm
   and is the regression guard for the whole class of bug.
3. **Both technologies** — a new layout's `_labelHeightDbu` equals its technology's `DefaultLabelHeightDbu`;
   with no technology resolved it falls back to 5 µm.
4. **Round-trip** — `DefaultLabelHeightDbu` persists in `.ctech` and an existing `.ctech` without it still
   loads (additive, **no `FormatVersion` bump**).
5. **Typing feedback (R-lbl-2)** — the status hint appears while typing and clears on commit or cancel; a
   ghost that would fall below the pixel threshold still renders visibly.
6. **Space (R-lbl-3)** — typing a space into a label inserts a space **and** leaves `_spaceHeld` false, so a
   following left-drag does not pan.
7. **Flatten a label** — `O` produces one polygon with **one hole**; `8` produces one with **two**; `i`
   produces two separate polygons. Flattened outlines match the rendered glyphs within tolerance.
8. **Port labels are skipped (R-lbl-5)** — a selection of one port label leaves Flatten **disabled with a
   reason**; a mixed selection flattens the others and reports the skip.
9. **Enablement (R13a)** — Flatten is enabled for a label, enabled for a circle, and disabled for a `Rect`
   with the existing reason string.
10. **Undo** — flattening a label is one entry restoring the `LabelShape` at its original index
    (`LayoutPersistence.Serialize` equality).
11. **Same font** — assert the flatten path and `DrawLabelText` resolve the same typeface.

## 5. On completion

Add a "Layout labels" entry at the top of `src/Ui/CLAUDE.md`. Call out: that the label pipeline was correct
and the defect was **a hardcoded 5 µm default that is sub-pixel on a PCB technology**, the same failure mode
as the L1a default-zoom bug; **R-lbl-1** and the deliberate contrast with the bitmap brief's viewport-relative
sizing, with the reason; **R-lbl-5** (port labels never flatten); and that text flattening **relies on
§3.1a holes** and on Clipper2 nesting rather than winding heuristics. Plus the test file names.
