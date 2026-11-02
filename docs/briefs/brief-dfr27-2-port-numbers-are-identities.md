# Brief DFR27-2 — A port's number is its identity: no silent renumbering

**Series:** designer feedback round 27 (2026-10-25). · **Tag:** `R-dfr27-2-<n>`
**Area:** `src/Core/Elaboration/Elaborator.cs` (the Term `Num` check, ~line 2720), the S-parameter analysis that builds
the `S` cube's `i`/`j` axes (`src/Engine`), `src/Render/DataDisplay/SliceTokenParser.cs` (port lookup, ~line 150),
`src/RfCore/Export/TouchstoneExporter.cs` and every other Touchstone writer (`TouchstoneIO`, the CLI's `sparam -o
x.sNp`, the Data Display's export). Findings go in `src/Core/RESOLVED.md` and `src/Render/RESOLVED.md`.
**Tests:** a Core/Engine test for the axes, a Render test for the trace, a CLI test for the refusal.

---

## 0. What happened

The designer's bench had Terms numbered up to 12. After deleting two Terms and simulating again, a trace that
named one of the deleted ports still showed a value (about −35 dB) instead of reading as invalid.

Reproduced: Terms numbered 1, 2, 9, 10 produce a 4×4 `S` cube whose ports are **1, 2, 3, 4**. The elaborator warns
once per missing number, then the gaps are closed up, so `S(4,3)` now means Terms 10 and 9. Every trace keyed by
number, every goal and every measurement silently points at a different pair after an edit, and a trace naming a
number past the end is only invalid when the compacted matrix happens to be smaller than that number.

## 1. Owner decisions

- **A trace, goal or measurement naming a port that is not there reads as INVALID**, with the message naming the
  port and the ports the result has.
- **A Touchstone export of a result whose port numbers have gaps is REFUSED**, naming the missing numbers and saying
  to renumber the Terms. Touchstone numbers its ports 1…N by position, so writing one would renumber them silently,
  which is the defect this brief removes.

## 2. What to do

### R-dfr27-2-1 — The `i`/`j` axes carry the Terms' own numbers

The `S` (and `Z`, `Y`, `Z0`, any per-port) cube's port axes hold each Term's `Num` as the coordinate: 1, 2, 9, 10, not
1…4. Port lookup (`SliceTokenParser`, `S(i,j)` in expressions, goals, `plot --trace i=…`) resolves by coordinate,
never by position. A number with no Term is invalid with a sentence like the one today's range check gives, listing
the ports present. Contiguous numbering (the common case) must produce identical files and identical plots.

### R-dfr27-2-2 — One warning, not one per gap

Replace the per-number warning with one line: the numbers present and the numbers missing, and that a Touchstone
export will be refused until they are contiguous.

### R-dfr27-2-3 — Touchstone writers refuse a gapped result

Every path that writes `.sNp` from a result checks the port coordinates are 1…N and refuses otherwise, with the
sentence above; the CLI exits 1. `.npy`/`.mat` exports keep the real numbers and are not refused. An S-parameter
block (`SnP`) reading a file is unaffected.

### R-dfr27-2-4 — The Data Display re-reads, it does not remember

After a re-run, a trace whose port vanished must show invalid on the next draw, not its last good data. Check the
cached-result path as well as a fresh open.

Gate: Terms 1, 2, 9, 10 → the cube's axes are [1, 2, 9, 10]; `S(10,9)` reads the 9→10 pair; `S(4,3)` is invalid
naming the ports present; `sparam -o out.s4p` exits 1 naming 3–8; Terms 1…4 produce byte-identical `.s4p` and `.npy`
to today.
