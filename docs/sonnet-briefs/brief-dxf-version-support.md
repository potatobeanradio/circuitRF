# Sonnet Brief — DXF version support: decide it, document it, and make the reader's tolerance real

Owner questions after L4b landed: is there value in also writing R2018? Does the reader handle newer
versions? And in either case, **the supported versions must be documented.**

The short answers are in §1; the work is §§2–4.

---

## 1. The version decision — keep writing R2000, and record why

**No, do not add R2018 output.** Every entity this exporter emits — `LWPOLYLINE` (with bulge), `LINE`,
`ARC`, `CIRCLE`, `ELLIPSE`, `SPLINE`, `HATCH`, `TEXT`, `INSERT`, `BLOCK` — exists unchanged in R2000. Nothing
added between R2000 (AC1015) and R2018 (AC1032) improves 2D geometry interchange, so a newer header would
buy nothing while **narrowing** compatibility: older PCB, CAM and mechanical tools routinely read R12 and
R2000 and refuse newer files. R2000 is close to the most universally readable DXF version in existence, which
is exactly what an export format should optimise for.

**The one real post-R2000 improvement is text encoding**, and it is worth handling on its own merits rather
than by bumping the version — see §2.

**If a version option is ever added, R12 (AC1009) is the one with genuine demand**, not R2018 — some legacy
CAM and tooling reads only R12. But R12 has no `LWPOLYLINE`, no `ELLIPSE`, no `SPLINE` and no `HATCH`, so it
would mean heavy `POLYLINE` output, flattened splines and ellipses, and losing hole fills — a real fidelity
loss that contradicts R-L4b-1's "never flatten an arc on DXF export." **Do not build it speculatively.**
Wait for a user who actually needs it, then weigh it against that cost.

**R-dxf-1. Record this decision and its reasoning** in the design doc (§4) so it is not revisited from
scratch every time someone notices the header says 2000.

## 2. Encoding — the one thing that is genuinely version-dependent

`DxfGroupReader` and `DxfGroupWriter` take a `TextReader`/`TextWriter`, so **the encoding decision lives at
the call sites and appears to be implicit today.** That is a latent bug in both directions:

- **R2007 (AC1021) and later are UTF-8.**
- **R2006 (AC1018) and earlier use the drawing's code page**, named in `$DWGCODEPAGE`, with `\U+XXXX`
  escapes for characters outside it.

Read an R2018 file as ANSI and non-ASCII layer names mangle; read an R2000 file as UTF-8 and the same happens
in reverse. Neither throws — they produce wrong text, silently.

**R-dxf-2. Make the encoding policy explicit and version-driven.**

- **Import**: read `$ACADVER` from the `HEADER` section first, then decode accordingly — UTF-8 for AC1021 and
  later; `$DWGCODEPAGE` (falling back to a documented default, and reporting the fallback) for earlier.
  Since `$ACADVER` appears near the start of the file, this stays compatible with the streaming discipline —
  but confirm that rather than assuming, and if it forces a two-pass read, say so.
- **Export**: R2000 output means the code page path. **Emit ASCII where possible and `\U+XXXX` escapes
  otherwise**, so a layer or label containing non-ASCII survives into AutoCAD rather than becoming mojibake.
  Report when any escaping occurred.

This is worth doing properly now: layer names in particular come from user technologies and may well contain
non-ASCII, and a mangled layer name is the kind of defect that gets blamed on the receiving tool.

## 3. Reader version tolerance — verify what is currently only implied

`DxfReader.cs` contains **no `$ACADVER` check anywhere**, and that is the right design: it reads the group
codes it understands and reports what it does not, which makes it naturally version-tolerant across the whole
family for our supported entity set. But "should work" is not "tested."

**R-dxf-3. Test import against real files from at least R12, R2000 and R2018**, produced by a tool that is
not this one (L4b's gate 12 already established that principle). Specifically confirm:

- R12 files parse — they use old-style `POLYLINE`/`VERTEX` rather than `LWPOLYLINE`, which is in the
  supported set but is the path least likely to have been exercised.
- R2018 files parse, with their larger `CLASSES` and `OBJECTS` sections skipped cleanly.
- Non-ASCII text round-trips correctly in each (§2).
- **Nothing rejects a file merely for its version.** If any version gate exists or gets added, it must warn
  and continue, never refuse — a reader that rejects a file it could have read is worse than one that reads
  it imperfectly and says so.

## 4. Document it — three places, one source of truth

`DxfWriter` already exposes `AcadVersionCode` and `FormatDescription` ("AutoCAD 2000/R2000 (AC1015)"),
explicitly so a UI surface can state the format without hardcoding the raw code. Build on that rather than
duplicating strings.

**R-dxf-4. State the support matrix in all three places, sourced from those constants where they are
strings:**

1. **Design doc §8**, in the DXF row or immediately below it: what is written (R2000/AC1015, and why), what
   is read (the version range, the supported entity set, and what is reported rather than imported), and the
   encoding rule from §2.
2. **Root or `src/Ui/CLAUDE.md`**, so the next person to touch the exporter finds the decision before
   changing the header.
3. **The UI** — the export dialog states the version it writes; the import path reports the `$ACADVER` it
   found and any fallback it applied. A user who knows their tool needs R12 should learn that from the
   dialog, not from a failed round-trip.

Also update the out-of-scope list already in the L4b brief to be a *documented* limitation rather than an
internal note: DWG, binary DXF, dimensions, leaders, xrefs, paper space, 3D.

## 5. Guardrails

- **Do not add R2018 or R12 output.** §1 is a decision, not a starting point for discussion.
- No changes to the entity mapping, the bulge identity, `SPLINE` export, or the array handling.
- No new dependency.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, GDSII, or anything outside DXF and the documentation.

## 6. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Encoding (R-dxf-2)** — a layer name and a label containing non-ASCII survive export→import intact;
   an imported R2018 file with UTF-8 text and an imported R2000 file with code-page text both decode
   correctly; any escaping or fallback is reported.
3. **Version tolerance (R-dxf-3)** — real R12, R2000 and R2018 files from another tool all import, with a
   report of what came through; no file is refused for its version. Record which tool produced each.
4. **R12 `POLYLINE`/`VERTEX`** geometry imports with bulges intact (R-L4b-3 applies to the old-style entity
   too, and this is the least-exercised path).
5. **Documentation (R-dxf-4)** — design doc §8, the CLAUDE.md note, and both dialogs state the matrix, with
   the version string sourced from `DxfWriter`'s constants rather than duplicated.

## 7. On completion

Record in `src/Ui/CLAUDE.md`: the **R2000-not-R2018 decision with its reasoning** and that R12 is the only
version with a plausible future case (plus what it would cost); the encoding policy and where the
`$ACADVER` sniff happens; and the actual import results per version, naming the producing tool — that list is
the honest statement of what "we support DXF" means.
