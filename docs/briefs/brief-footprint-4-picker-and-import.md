# Brief 4 — one picker over built-ins, imported cells and Custom; and how it meets Component Import

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp4-n` · **Phase:** P2
**Area:** `src/Design/Layout/Footprints/FootprintCatalog.cs` (new),
`src/Ui/ViewModels/ParameterEditorViewModel*`, `src/Ui/Layout/LayoutEditorViewModel.Instances.cs`,
`src/Design/Layout/Interchange/BomFile.cs` (read only)
**Depends on:** [3](brief-footprint-3-update-layout.md) · **Blocks:** [5](brief-footprint-5-power-rail-example.md)

---

## 0. What this brief delivers

The middle section of the combobox, and the answer to the question the owner asked when the feature
was scoped: **we already have a Component Import that creates cells with custom footprints — how do
these two work together?**

They work together by not being two things. The overview's rule:

> **A footprint IS a layout view of a cell. There is no second artifact kind.**

Component Import writes a cell folder with a symbol plus one or more land patterns as sibling
`.clay` views, a `.ccell` naming the primary, and the source bytes kept beside it
(`src/Design/Layout/ComponentImport.cs:183`). That is already a footprint. This brief makes the
picker **find** it, and changes nothing about the import.

---

## 1. `R-fp4-1` — the catalog: three sections, one list

| section | what | where it comes from |
|---|---|---|
| **None** | no artwork | — |
| **Built-in** | the ~25 case codes | brief 1's table, generated on demand |
| **In this workspace** | every cell with a layout view and a matching pad count | a walk of the workspace's cells |
| **Custom…** | a `.clay` anywhere | a file picker |

**`R-fp4-1a`** `FootprintCatalog` lives in `src/Design/Layout/Footprints/` and is framework-free, so
`circuitrf explain` can list what a design could have chosen and `circuitrf check` can say that a
stored footprint no longer resolves. Both are cheap once it is below the firewall and impossible
once it is not.

**`R-fp4-1b`** The workspace section is filtered by **pad count equal to the component's port
count** (brief 3 R-fp3-5). A picker that offers a four-pad cell to a two-terminal capacitor offers a
refusal.

**`R-fp4-1c`** The walk is **bounded and cached**, and says when it stopped short — `circuitrf find`'s
own rule. A workspace with a large imported library must not make the parameter editor pause on
every selection, and a directory symlink is never followed.

**`R-fp4-1d`** A cell with **several** layout views — which is exactly what Component Import writes
for a part with density variants — appears as ONE row per view, labelled with its variant, not one
row for the cell. The variants are different artwork and the primary is not automatically the one
wanted.

---

## 2. `R-fp4-2` — Component Import is not touched

**`R-fp4-2a`** Gated by a comment-stripped source scan: this series adds no code to
`src/Design/Layout/ComponentImport.cs` beyond a call site, writes no second land-pattern format, and
registers no second footprint index. The overview's corollary, enforced.

**`R-fp4-2b`** The one thing worth connecting: `ComponentImport.Import` returns a `CellDir`, and the
importer's completion report should say **"this part is now available as a footprint"** with the
pad count, so a user who imported a part knows the picker will offer it. One sentence in an existing
report; no new mechanism.

**`R-fp4-2c`** The density-variant spelling must AGREE. `BuiltLayout.Variant` is R-PL1-25's suffix
and brief 1's `@M`/`@N`/`@L` is the same idea; if the two spell a density differently, an imported
nominal pattern and a generated nominal pattern sort into two different rows reading the same word.
One constant, referenced by both.

---

## 3. `R-fp4-3` — the BOM's footprint column

`BomFile` already recognises `footprint`, `package`, `pattern`, `land pattern`, `fp`, `decal`,
`pkg`, `case`, `footprint name` (`src/Design/Layout/Interchange/BomFile.cs:144`), and
`PartLibraryRow.Footprint` is a `string?` that exists today
(`src/Design/RailRf/PartLibrary.cs:80`). Neither feeds anything.

**`R-fp4-3a`** A BOM token is matched against the catalog by a small, EXPLICIT normalisation:
strip a leading vendor-style prefix up to the last separator, uppercase, compare against the case
codes. A real BOM writes `SM/C_0402`, `C0402`, `CAP-0402-X7R`, `0402` — all of which reduce to
`0402`, and the normalisation is a function with a table of examples as its test.

**`R-fp4-3b`** **A token that matches nothing is reported, never guessed.** It appears in the
parts table as an unmatched footprint with the token shown, and it is not turned into the nearest
code. The whole point of the column is that it tells you something; a fuzzy match tells you what
the matcher believed.

**`R-fp4-3c` — the ambiguity refusal.** A bare numeric token of four digits that names a valid case
in BOTH schemes is **ambiguous and is reported as ambiguous**: `0201` is 0.6 x 0.3 mm imperial and
0.25 x 0.125 mm metric, a factor of 2.4 either way. The report names both readings and asks. This
is the overview's §1e, and it is the same shape as `convert`'s Excellon suppression refusal —
leading vs trailing differ by four orders of magnitude on identical text, so it prints the
inference and the flags that answer it.

The codes that collide: `0201`/`008004`, `0402`/`01005`, `0603`/`0201`, `1005`/`0402`,
`1608`/`0603`, `2012`/`0805`, `3216`/`1206`, `3225`/`1210`. A token in that set with no scheme
stated is ambiguous. A token outside it (`2512`, `7343-31`) is not.

**`R-fp4-3d`** Nothing about this ASSIGNS a footprint from a BOM. It reports what the BOM says and
what it resolved to; a user applies it. A BOM column that silently set artwork would be the same
class of error as the ambiguity it is trying to avoid.

---

## 4. `R-fp4-4` — `check` and `explain` learn about footprints

Both verbs are read-only and neither runs an analysis. Both gain one thing each, and neither writes
a rule of its own — every finding comes from `FootprintCatalog`, which the GUI uses too.

**`R-fp4-4a`** `circuitrf check` reports a stored `Footprint` that does not resolve, and one whose
pad count disagrees with its component's port count. Warning, not error: the design is still
simulable and the artwork is what is missing. Warnings are always reported and still exit 0.

**`R-fp4-4b`** `circuitrf explain --footprints` lists, per component, what it states, what that
resolved to, how many pads, and — for a built-in — the technology it would be generated against.
Resolution here is a walk like every other in `explain`, and the walk is reported as well as the
answer.

---

## 5. Gate

`tests/Ui.Tests/Footprints/FootprintCatalogTests.cs` and
`tests/Ui.Tests/Cli/FootprintCliTests.cs` — new.

1. **An imported part appears in the picker.** Import a synthetic two-pad part through
   `ComponentImport.Import`, then build the catalog for a two-terminal component in that workspace,
   and assert the imported cell is offered. **This is the reconciliation, as a test.**
2. **Density variants are separate rows.** A part imported with three variants offers three rows,
   each labelled, and the primary is not privileged (R-fp4-1d).
3. **Pad count filters.** The same imported two-pad part is NOT offered to an `S4P` (R-fp4-1b).
4. **Component Import is untouched.** Comment-stripped source scan over this series' diff:
   no additions to `ComponentImport.cs` beyond a call site (R-fp4-2a).
5. **One density spelling.** Assert `BuiltLayout.Variant` for a nominal pattern and brief 1's
   nominal suffix are the same string, from the same constant (R-fp4-2c).
6. **BOM normalisation.** A table: `SM/C_0402`, `C0402`, `CAP-0402-X7R`, `0402`, `0402M`,
   `WIDGET-77` -> four resolve to `0402`, one is ambiguous, one is unmatched-and-reported
   (R-fp4-3a/b).
7. **The ambiguity refusal fires on the colliding set and not outside it.** Eight ambiguous tokens,
   two unambiguous ones (R-fp4-3c).
8. **The walk is bounded.** A workspace with a deep tree and a directory symlink: the catalog
   returns, says it stopped short, and did not follow the link (R-fp4-1c).
9. **`check` warns and exits 0.** A design with an unresolvable footprint: one warning naming it,
   exit code 0 (R-fp4-4a).
10. **`explain --footprints` reports the walk.** Output names the stored value, the resolved cell
    and the technology (R-fp4-4b).

## 6. Scope

- **No automatic assignment from a BOM.** R-fp4-3d.
- **No fuzzy matching.** R-fp4-3b.
- **No change to Component Import.** R-fp4-2a.
- **No footprint library document.** The workspace's cells ARE the library; a `.cfplib` would be the
  second artifact kind the overview's rule forbids.
