# Brief IM-13 — Photographs of boards (optional, late)

**Series:** `brief-img-0-overview.md` (D6, D7, D18) · **Tag:** `R-im13-<m>`
**Depends on:** IM-3 (and IM-12's fixtures pattern)
**Area:** `src/Design/Imaging/` (`Perspective.cs`, `WhiteBalance.cs`), `src/Design/Layout/Recognition/Image/
BoardPhoto.cs`, `src/Ui/Recognition/` (the corner tool), design note §14

**Start only if the owner asks after IM-12.** Everything before this works on drawn pictures; a phone photo of a real
board is a different problem, and this phase says how far the same pipeline can be pushed without a trained model.

---

## 1. Goal

A reasonably taken photograph of a board's top side becomes traced copper good enough to start a schematic from —
lines and pads, not fine detail.

## 2. Requirements

**R-im13-1 — Kind.** IM-2 gains *Board photo*: many colours, smooth gradients, **and** large near-rectilinear regions in
a mask-like hue. It no longer reads *None* for such a picture; it reads *Board photo* with lower confidence.

**R-im13-2 — Rectify.** A four-corner tool on the dialog canvas (drag four handles onto the board's corners, or onto
any rectangle of known size) → a homography (direct linear transform, in-house) → a rectified raster. The rectangle's
size, typed with units, **is** the scale (D6 evidence 2 in another form). The CLI spells it
`--corners x,y;x,y;x,y;x,y=<w>x<h>`.

**R-im13-3 — Light.** Grey-world white balance, then a flat-field correction (a large-radius blur divided out) so one
side of the board is not read as a different colour from the other.

**R-im13-4 — What is copper.** Exposed copper and plated pads (bright, metallic, low saturation or gold hue) and copper
**under the mask** (the mask hue, lighter) are separate clusters: both map to the top copper by default, the mask itself
to *Background*, the silkscreen to *Silkscreen*. Specular highlights (saturated white blobs) are filled from their
neighbourhood before tracing. Parts on the board cover their pads; the pads around a part's body are inferred from the
body's footprint only when a placement or BOM is given — otherwise the body is reported as an *unread part*.

**R-im13-5 — Honesty in the report.** Resolution line (R-im3-3) plus the rectification's residual at the four corners,
in µm, and a one-line warning that photographed widths are not good enough for impedance (line widths are read; Z0 from
them is marked *estimated*, which is the readout's normal word).

## 3. Not in this phase
Bottom-side photographs aligned to the top, X-ray pictures, anything needing a trained model.

## 4. Gates (minimal tests, run only these classes)
In-memory synthetic photographs (a drawn board warped by a known homography, a lighting gradient, noise and two
highlights):
- `PerspectiveTests` — four picked corners recover the board to within 1 px at its edges.
- `BoardPhotoTraceTests` — a 50 Ω line under mask and its exposed pads trace onto the top copper; the highlight does
  not leave a hole.
