# Brief LCP-2 — The CLI and MCP accept a layout's `.cem`

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp2-<n>` · **Depends on:** LCP-1
**Area:** `src/Cli/CliEntry.cs`, `Explain.cs`, `ExplainComponentPorts.cs`, `Check.cs`, `CliDiagnostics.cs`, `DocumentSchema.cs`,
`Serve/ToolCatalog.cs`, `docs/design/cli.md` §8.11.

## The gap

`em --component-ports` refuses a layout's `.cem` (`em.component-ports.refused`), `explain --component-ports` answers only for a 3D
view (`Explain.cs:313`), `check` walks them only for a `.c3d` (`Check.cs:953`), and `ToolCatalog` and `DocumentSchema` describe them
as a `.c3d`'s.

## Requirements

**R-lcp2-1 — `em`.** `--component-ports` applies to a `.cem` on a layout for one run, in memory, as it does for a `.c3d`. A planar
setup builds strips (LCP-1). A 3D `.cem` keeps refusing until LCP-5 lands, and then that refusal goes. Keep the diagnostic id for what
still cannot apply: a thermal or static setup.

**R-lcp2-2 — `explain --component-ports`** walks a `.cem` as it walks a `.c3d`, one line per port: refdes, pads, conductors, gap, width,
verdict. A planar line says "delta gap across a strip", since this is where a user or agent sees what was built. It solves nothing and,
on a planar setup, does not mesh.

**R-lcp2-3 — `check`** reports each port and each left-out part on a `.cem` as `CheckComponentPorts` does on a `.c3d`, at the same
severities. A part with no model on the schematic stays the warning it is. New ids go in `CliStructuredOutputTests.ExpectedIds`.

**R-lcp2-4 — MCP and the format reference.** The `run` tool's `componentPorts`, the `explain` tool's, and `create`'s text stop saying "a
`.c3d`'s". `DocumentSchema` gains `ComponentPorts` in the `.cem` section. The `plot` test's approach applies: a test reads the flag from
the parser, so a tool cannot lag behind its verb.

**R-lcp2-5 — Design doc.** `docs/design/cli.md` §8.11 is rewritten for both containers.

## Gates

No solves beyond LCP-1's 2 s fixture. Process-level, in the existing CLI test style:
1. `explain` and `check` on the fixture `.cem` report the same parts the in-process builder reports.
2. `em --dry-run --component-ports C1,C2` on a `.cem` reports the ports and writes nothing to the `.cem` or the `.clay`.
3. `em --component-ports` on the fixture runs and writes the `.s2p`, with the part names in its header.
4. A thermal setup is refused by the kept id.
