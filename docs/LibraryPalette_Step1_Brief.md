# Library Palette — Step 1: catalog metadata + the LibraryCatalog projection (headless) (Claude Code / Sonnet)

The foundation of the Library Palette and **the developer-contribution point**: extend `ComponentTypeRegistry`
with **Palette metadata** (category, search terms, Common flag) and add a **framework-free `LibraryCatalog`**
that projects the registry into an ordered, filterable/searchable list of palette items. **This brief is ONLY
step 1** — metadata + the catalog projection + tests. **No tiles, no grid, no header UI, no placement state
machine, no drag-and-drop** — those are steps 2+. Read `library-palette.md` §2 + §8 first. Sub-gated; **report
and stop between every layer.** Firewall green.

> Read first: `docs/design/library-palette.md` §1 (Palette sources the built-in registry, NOT the cell
> scanner), §2 (the library data model — category, search, Common, virtual categories, Recently-Used), §8
> (the registry is the single contribution point; the recipe). Context code:
> `src/Ui/Schematic/ComponentTypeRegistry.cs` (the **target** — `ComponentTypeInfo` record, the `Registry`
> dictionary keyed by `SymbolKind`, `Get`/`DisplayName`/`InstancePrefix`/`EngineReference`/`DefaultParameters`/
> `TryParseCode`; **Avalonia-free** — its header comment already says it's "shared by the renderer, palette,
> and auto-naming"), `src/Ui/Schematic/SymbolModel.cs` (`SymbolKind` enum — the full set of built-ins). Design
> docs win on any conflict.

## The spine (do not violate)
- **The Palette sources the component-type registry, NOT the project-tree cell scanner** (§1). v1 = built-in
  compiled primitives keyed by `SymbolKind`. (Library *cells* in the Palette are deferred.)
- **One library + category metadata** (§2) — not many libraries. Filtering by category + search realizes the
  groupings; **Common** and **Recently Used** are **virtual categories** (filters), not separate stores.
- **The registry is the single contribution point** (§8) — adding a component = adding one registry entry
  (+ its `BuiltInSymbols` geometry + engine type, both already required). The catalog derives entirely from
  registry metadata, so a new entry appears in the Palette automatically.
- **Bind the Palette to the catalog projection, not `SymbolKind` directly** — so the anticipated v2 "richer
  component type" re-key (the registry's own header comment) is a catalog-internal change, not a Palette
  rewrite.
- **Framework-free, headless** — metadata + catalog + filter/search live in the registry's Avalonia-free home;
  unit-tested with no GUI.
- **Scope fence (step 1):** metadata + `LibraryCatalog` projection + filter/search helpers + tests. NO tiles,
  NO grid, NO header UI, NO placement, NO drag-and-drop, NO Recently-Used persistence wiring (the catalog
  exposes the Common/category data; the MRU *store* is wired with the UI in a later step).

---

## LAYER 1 — Palette metadata on the registry

Extend `ComponentTypeRegistry` (Avalonia-free) so each component type carries Palette metadata:
1. **A `ComponentCategory` enum** (§2): `Lumped` (R, L, C, …), `TransmissionLine`, `Microstrip`, `Sources`
   (V, VTone, …), `DataFiles` (SNP, .npy — future entries), `Terminals` (Ground, Port), and a sensible
   default/`Other`. (Only categories the current built-ins need + the near-term ones the design names; the
   enum is extensible.)
2. **Extend `ComponentTypeInfo`** (or a parallel metadata structure) with: **`Category`**, **`SearchTerms`**
   (a small set — display name, type code, aliases like "cap"/"res"/"ind"; the existing `DisplayName` +
   `TryParseCode` codes are natural seeds), and **`IsCommon`** (marks the curated everyday subset).
3. **Populate** the metadata for the existing built-ins (R/L/C → Lumped+Common; V/VTone → Sources; Ground/Port
   → Terminals; FET/SDD/ZPort/Generic → sensible categories). Keep it additive — don't change existing
   `DisplayName`/`InstancePrefix`/`EngineReference`/`DefaultParameters` behavior.

**Layer 1 gate:** the registry compiles with category/search/Common metadata on every entry; existing
registry behavior (display names, engine refs, default params, code parsing) is unchanged (existing tests
green). Report.

---

## LAYER 2 — the `LibraryCatalog` projection + filter/search

A framework-free **`LibraryCatalog`** (in the registry's Avalonia-free home):
1. **`PaletteItem`** record: `{ SymbolKind Kind, int PortCount, string DisplayName, ComponentCategory
   Category, IReadOnlyList<string> SearchTerms, bool IsCommon }` — the single shape the Palette VM will bind
   to (binding to *this*, not `SymbolKind`, per the spine).
2. **`AllItems`** — the ordered list of palette items projected from the registry (stable order: by category
   then display name, or a curated order — pick one and keep it deterministic).
3. **Filter by category** — given a `ComponentCategory` *or* a **virtual category** (`All` / `Common` /
   `RecentlyUsed`), return the matching items. For this step: `All` = everything, `Common` = `IsCommon` items,
   real categories = exact match. **Recently-Used** takes an externally-supplied MRU list of `SymbolKind`s and
   returns those items in MRU order (the catalog provides the *projection*; the persistent MRU *store* is
   wired with the UI later — the catalog just accepts a list and orders by it).
4. **Search** — given a query string, return items whose `SearchTerms`/display name/category match
   (case-insensitive substring). Search composes with category (search within a category, or search-all —
   keep the helper simple: a `Search(query, category?)`).

**Layer 2 gate:** headless tests — `AllItems` lists every built-in with correct category/Common flags;
filter by `Lumped` returns R/L/C; `Common` returns the curated subset; `Search("cap")` finds the capacitor;
`Search("res")` finds the resistor; a Recently-Used projection orders by a supplied MRU list. Report.

## Acceptance (step 1)
1. `ComponentTypeRegistry` carries Palette metadata (category, search terms, Common flag) on every built-in,
   additively (existing behavior unchanged).
2. A framework-free `LibraryCatalog` projects the registry into ordered `PaletteItem`s and supports
   filter-by-category (incl. virtual All/Common/RecentlyUsed) + case-insensitive search — the single source
   the Palette VM will bind to.
3. Adding a registry entry (with metadata) makes it appear in the catalog automatically (verify with a test
   or a note — the contribution point works).
4. `dotnet build`/`dotnet test` green; firewall green (all in the registry's Avalonia-free home); **no tiles,
   grid, header UI, placement, drag-and-drop, or MRU persistence** (steps 2+); nothing else regresses.

## Guardrails
- **Source = the registry**, not the cell scanner; v1 built-ins only.
- **One library + categories**; Common/Recently-Used are virtual categories (filters).
- **Registry is the single contribution point** — the catalog derives from metadata; a new entry appears
  automatically.
- **Bind to the catalog projection, not `SymbolKind`** — keeps the v2 re-key catalog-internal.
- **Additive** — don't change existing registry behavior; **framework-free + headless**.
- **Scope fence:** metadata + catalog + filter/search + tests only.
- Sub-gate the two layers; report and stop between each.
- Update `library-palette.md` §10 status (step 1 done) and `src/Ui/CLAUDE.md` (the registry carries Palette
  metadata; `LibraryCatalog` is the projection the Palette binds to; adding a component = one registry entry).

*Exit: the registry carries Palette metadata and a framework-free `LibraryCatalog` projects it into a
filterable/searchable item list — establishing the single developer-contribution point and the data source the
Palette tiles + grid (steps 2–3) and placement (step 4) build on.*
