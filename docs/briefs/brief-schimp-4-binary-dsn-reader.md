# Brief SI-4 — The binary `.DSN` design reader

**Series:** `brief-schimp-0-overview.md` (D7, D8) · **Tag:** `R-si4-<m>`
**Depends on:** SI-1; SI-3 (its cross-check)
**Area:** new `src/Design/Schematic/Import/Cfb/CompoundFile.cs` (read-only CFBF), new
`src/Design/Schematic/Import/BinaryDsn/` (stream readers).

---

## 0. Risk — read first

The container is publicly specified. **The streams inside it are not.** The only open description is an MIT-licensed
C++ open-source parser, archived since 2024, that parses many record types for some file versions and
documents its own gaps. This phase is therefore **investigative**: it may end with a reader that handles the
versions in the owner's corpus and refuses others by version, and that is an acceptable outcome. Users of the
commercial editor always have SI-3's EDIF export as the reliable route, and every refusal here says so.

**Stop and report** (rather than push on) if, after R-si4-2, fewer than half of the local corpus's files yield
pages, parts and wires whose SI-1 check against the SAME design's EDIF export (SI-3) is `Matched`.

## 1. Goal

Read the binary `.DSN` directly — no export step — into SI-1's `ForeignSchematic`, cross-checked against the same
design read through SI-3.

## 2. Requirements

**R-si4-1 — `CompoundFile`, a read-only CFBF reader** written from the published MS-CFB specification: the header,
FAT / mini-FAT / DIFAT chains, the directory red-black tree read as a flat list with parent paths, stream read by
path. Versions 3 (512-byte sectors) and 4 (4096). Hostile-input hardened: every chain bounded by the sector count,
cycles detected, a truncated file a refusal — never an exception escaping, never an unbounded loop. No write
support. Gate: a CFBF file **built by the test itself** (a tiny writer in the test project) with a nested storage and
a mini-stream stream; and a truncated/cyclic one refused.

**R-si4-2 — Survey before parsing.** A `tools/`-style scratch probe (not shipped) that lists every stream path and
size in each corpus file, and the first bytes of each. Record in `src/Design/RESOLVED.md` the stream layout found
(the library, cache, per-schematic per-page, symbol and package streams), how the file version is stated, and which
versions the corpus contains. **This survey decides the scope of R-si4-3; hand it back to the owner before going on.**

**R-si4-3 — Stream readers, by version.** Readers for the records that carry a page's content — placed instances
(reference, symbol, position, rotation, mirror, unit, value and properties), wires, junctions, net aliases, power and
off-page symbols, ports, sheet instances — and for the symbol cache that holds each placed symbol's pins and graphics.
Each record reader states the versions it was confirmed on. An unknown record type inside a known version is skipped
by its declared length with a note; an unknown VERSION is a refusal: "this design's version (n) is not readable;
export it as EDIF 2.0.0 from the editor and import that file."

**R-si4-4 — Text.** Strings are length-prefixed in a single-byte code page; decode as SI-3 does (`--codepage`), and
report the code page used.

**R-si4-5 — `DeclaredNets`.** Where the page streams carry explicit connectivity, populate it; otherwise null and
SI-1's geometric check stands alone. Say which, per file, in the notes.

**R-si4-6 — The cross-check.** For every corpus design that has its EDIF export beside it, SI-1's check between the
two `ForeignSchematic`s' connectivity (as pin sets) must be `Matched`; also the instance count, the reference set,
and each instance's position and orientation.

## 3. Fixtures
**Real designs only exist locally** (`testdata/schematic-sources/dsn/`, git-ignored — the owner supplies them,
ideally each with its EDIF export and netlist export beside it). Committed tests use R-si4-1's self-built containers;
committing any real design file is out of bounds. Ask the owner for the corpus before R-si4-2.

## 4. Hand-back
The survey (R-si4-2), the versions read and refused, the R-si4-6 table per corpus file, and an honest statement of
what this reader does not read.
