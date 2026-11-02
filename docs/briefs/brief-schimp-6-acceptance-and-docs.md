# Brief SI-6 — Acceptance, example and documentation

**Series:** `brief-schimp-0-overview.md` · **Tag:** `R-si6-<m>`
**Depends on:** SI-1 … SI-5 (SI-4 to the extent it landed)

---

## 1. Goal

Prove the series on real designs, give users a worked example, and document it.

## 2. Requirements

**R-si6-1 — Field acceptance.** Over the local corpus (`testdata/schematic-sources/`, git-ignored, owner-supplied —
all three formats where available), a `FixtureTheory` that imports each design and asserts: connectivity `Matched`
(SI-1 D8); every source reference present; no exception; the import's notes recorded. A table per file (format,
version, parts, native/symbol-cell split, nets, result, notes) goes in `src/Design/RESOLVED.md`. Expectation, stated so
it can be checked: 100 % `Matched` for `.kicad_sch` and EDIF; SI-4's hand-back sets the binary `.DSN` expectation.

**R-si6-2 — Round trip to simulation.** One committed authored fixture — a small RF matching network drawn as a
foreign schematic (R, L, C, ground, two connectors marked as ports) — is imported by the CLI, `check`ed (0 errors)
and run (`sparam`); its S-parameters equal the same circuit written natively as a `.cnl` to 1e-9. This is the claim
that an imported schematic of native parts is a real circuit, not a picture of one.

**R-si6-3 — Example.** Add the fixture and its imported cell to an existing example workspace under `examples/` (or a
new one if the owner prefers — the six-example rule: folder + manifest entry), with a short README step: "the
schematic was imported from `matching.kicad_sch` with File ▸ Import ▸ Schematic…".

**R-si6-4 — User docs (sources only; DocGen is the owner's).** A page "Importing a schematic": what the three formats
are (by extension), the two entry points and the drop, what becomes a native part and what becomes a symbol cell and
why that cell does not simulate until given a model, ports, the connectivity check and what a mismatch means, the
EDIF route when a binary `.DSN` is refused, the CLI noun. No change history, no explanatory notes beyond what the user
needs, no tool or vendor names.

**R-si6-5 — The design note** `docs/design/schematic-import.md` brought up to date with every phase's findings.

**R-si6-6 — Vendor-name sweep.** Grep the whole diff of the series for commercial editor, vendor and tool names (and
the two reference projects' names) before handing back; report what was removed.
