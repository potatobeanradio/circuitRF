# `src/Ui` — the Avalonia UI layer

Standing instructions for `src/Ui` (schematic editor, layout editor, Harmonica RF-tuning UI, EM/layout
view, Data Display, dialogs, theming — the whole Avalonia application). Read with the root `CLAUDE.md`.

> ### Where the history went
> This file was an append-only phase log that reached **21,417 lines / 1.8MB** — by far the largest
> `CLAUDE.md` in the repo. The full text is preserved verbatim at **`src/Ui/HISTORY.md`**, unchanged.
>
> **Unlike `src/Core`, `src/Engine`, and `src/Engine/Mom`, this file was archived WITHOUT a hand-curated
> living-reference rewrite** — it has no separable architecture preamble to keep (content starts
> directly with dated `§`-numbered brief write-ups on line 1 and never establishes a standing
> "what lives here" section), and condensing ~400 sections faithfully requires reading the file, which
> was deliberately not done here for cost reasons. **Grep `HISTORY.md`** — `grep -n "^## " src/Ui/HISTORY.md`
> for a table of contents, `grep -n "<topic or R-code>" src/Ui/HISTORY.md` for a specific area,
> `grep -n "2026-0[78]" src/Ui/HISTORY.md` for a date range — rather than reading it whole.
>
> **Anyone doing focused work in one area of `src/Ui`** (schematic, layout, Harmonica, Data Display,
> theming, a specific dialog) is encouraged to grep out that area's sections from `HISTORY.md` into a
> short scoped note here, under a heading, the next time they're already reading that history for a
> task — that is the cheap, incremental way this file should regrow into a real reference over time,
> rather than another single expensive pass.
>
> **Maintenance rule, or this stays permanently thin.** A completed phase's narrative belongs in
> `HISTORY.md`, not here. This file should only ever gain durable, still-true content: an invariant, a
> current default, a refusal, a trap with a name — never a phase-by-phase write-up.
