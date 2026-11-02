# Brief AS-11 — Swap Line Type: MLIN ↔ CPWG ↔ SLIN ↔ TLIN, geometry held

**Series:** `brief-artsch-0-overview.md` (D19) · **Tag:** `R-as11-<m>`
**Depends on:** AS-1, AS-2 (and AS-6 for the measured gap on recognised components; the command works without it)
**Area:** new `src/Design/Schematic/LineTypeSwap.cs`; `src/Ui/ViewModels/SchematicViewModel*.cs` (the command and
its context-menu row), `src/Ui/ViewModels/ParameterEditorViewModel*.cs` (the type combo), `docs/user/src`

---

## 1. Goal

A trace is sometimes read as grounded coplanar waveguide when the designer meant microstrip — a top-side ground
pour that comes close to the line is enough — and sometimes the other way. More generally, a designer wants to ask
"what if this line were stripline?" without retyping it. **Swap Line Type** replaces a line component with another
line type **in place**: same instance name, same wiring, same **physical geometry** (W and L), and Z0 left to
follow from the new model. Useful on any schematic, not only a recognised one (the owner's request).

## 2. Requirements

**R-as11-1 — The swap is one function.** `LineTypeSwap.Swap(component, toType, context)` returns the new component
(or a refusal) — framework-free, in `src/Design`, so `circuitrf` and any later batch use can reach it. It changes
the component's type and parameters and nothing else: instance name, position, rotation, wiring, `FromArtwork`,
`ArtworkAnchor` and `ArtworkMeasured` are carried over. One undo step in the GUI.

**R-as11-2 — Which parameters carry, and where a missing one comes from.**
| From → To | W | L | G (CPWG) | H1/H2 (SLIN) | Z, Eeff (TLIN) |
|---|---|---|---|---|---|
| MLIN → CPWG | kept | kept | `ArtworkMeasured` gaps (mean) → else the last G this component had (R-as11-3) → else **asked** (GUI) / refusal naming `G` (headless) | — | — |
| CPWG → MLIN | kept | kept | remembered (R-as11-3) | — | — |
| MLIN/CPWG → SLIN | kept | kept | remembered | from the technology (SLIN's injection); a layer with one plane is the injection's own refusal | — |
| any → TLIN (physical) | remembered | kept | remembered | — | **computed** from the source model at its current geometry at `F` = the bench's top frequency (the model's own `LineParameters`, not the cross-section) |
| TLIN → MLIN/CPWG/SLIN | remembered W → else **synthesised** from Z at `F` through the Line Calculator (the one synthesis path), and the status line says it was synthesised | `L` if physical form, else E/F converted with the TLIN's `Eeff` | as above | as above | — |
Substrate values are never copied: each type takes its own from the technology, so a stated per-instance override
(`H`, `Er`, …) is carried only where the target type has that parameter, and listed when it is dropped.

**R-as11-3 — Nothing measured is lost.** A parameter the target type does not have (CPWG's `G` on a swap to MLIN, a
TLIN's `Z` on a swap to MLIN) is **kept in `ArtworkMeasured`** under a `Swap.` prefix, so swapping back restores it
exactly. A swap followed by its inverse is the identity on the component (a gate).

**R-as11-4 — Discontinuities.** MBEND, MTEE, MCROSS and MTAPER have no CPWG/SLIN counterparts (D18). Swapping a
**line** next to one is allowed and leaves the discontinuity in place, with one status-line note (*"MBEND B3 stays
a microstrip bend"*). The command is offered on line types only: MLIN, CPWG, SLIN, TLIN.

**R-as11-5 — The GUI.**
- Context menu on a line component: **Swap Line Type ▸ Microstrip (MLIN) / Grounded Coplanar (CPWG) / Stripline
  (SLIN) / Ideal Line (TLIN)**, the current type disabled. Works on a **multi-selection** of line components (all
  swapped in one undo step; any that refuse are listed and the rest still swap).
- The parameter editor's type field becomes a combo among the four for a line component, doing the same swap.
- After a swap, the parameter editor shows the new type's computed Z0 (the existing MLIN impedance readout,
  extended to CPWG and SLIN by AS-1) — the "Z0 left free" the owner described.

**R-as11-6 — Headless.** An agent writes the file (the format is the contract) and can call nothing else; the
function is exercised by tests only in this phase. If a batch-edit surface later exists, it calls this function.

## 3. Gates (minimal tests, run only these classes)
- `LineTypeSwapTests` — MLIN → CPWG with `ArtworkMeasured` gaps 0.20/0.24 mm gets G = 0.22 mm and the same W, L;
  CPWG → MLIN → CPWG restores G exactly; MLIN → TLIN computes Z and Eeff equal to the MLIN model's at `F`; TLIN with
  no remembered W → MLIN synthesises W whose Z0 at `F` equals the TLIN's Z within the synthesis tolerance; MLIN →
  SLIN on a top layer is the injection's refusal and nothing changes; instance name and nets are unchanged in every
  case (the extracted netlist differs only in that instance's type line).
- `SwapLineTypeCommandTests` — a multi-selection of three lines, one of which refuses, swaps two in one undo step
  and lists the third; undo restores all three.
