# Sonnet Brief — Fast test loop: keep the slow engine simulations out of the inner loop

Small, self-contained, and worth landing before the next feature brief. The suite has grown slow enough that
it discourages running it, which is the failure mode that matters.

## 1. The immediate win needs no code change

The heavy work is in **`tests/Engine.Tests/Loadpull/`** — `Hero3LoadpullTests`,
`FreqSweptLoadpullTests`, `LoadpullCnlWriterRunTests` and friends actually run the simulation engine. The
`Loadpull*` files in `tests/Ui.Tests` are UI-level (recognition, metric lists, group pickers) and are not the
problem.

**Every layout brief since L0a has carried the guardrail "don't touch `src/Core`, `src/Engine`, `RfCore`."**
So for layout work the relevant projects are exactly two:

```
dotnet test tests/Ui.Tests tests/Firewall.Tests
```

- **`Ui.Tests`** — where all layout tests live.
- **`Firewall.Tests`** — cheap, and it is the one thing layout work *can* plausibly break from outside
  `src/Ui`, since a stray `using Avalonia` below the UI boundary is exactly what it exists to catch.
- **`Core.Tests` / `Engine.Tests`** — untouched by layout work by construction.

Add `--no-build` after the first build of a session.

## 2. Make it durable: tag the slow tests

**R-tst-1. Tag genuinely slow tests with a trait, then filter.**

```csharp
[Trait("Category", "Slow")]
```

on the engine-simulation test classes, enabling:

```
dotnet test --filter "Category!=Slow"
```

Attribute-plus-filter is about as low-risk as a change gets: no test logic moves, nothing is deleted, and the
full suite still runs by default when no filter is passed.

**Measure before tagging.** Run with `--logger "console;verbosity=detailed"`, sort by duration, and tag on
evidence — the assumption that loadpull is the whole cost may be wrong, and a couple of other tests may
dominate. Report the top offenders with their times.

**Tag conservatively.** A test is `Slow` only if it is genuinely long-running *and* covers engine numerics
rather than a code path feature work touches. **When in doubt, leave it untagged** — a slow test that still
runs costs seconds; a fast test wrongly excluded costs a regression nobody notices.

## 3. Write it down where it will be found

**R-tst-2. Document the fast loop in the root `CLAUDE.md` testing section**, so every future brief inherits
it rather than each one re-deciding. State plainly:

- the fast loop for UI/layout work (§1),
- the `Category!=Slow` filter and what qualifies,
- and **the rule that the full unfiltered suite runs at phase boundaries and before anything is called
  complete** — the shortcut is for the inner loop, not for the gate. Every brief's gate item 1 continues to
  mean the whole suite.

## 4. Guardrails

- **Do not delete, skip, or weaken any test.** This is about *when* tests run, never *whether*.
- No `[Fact(Skip=…)]`, no conditional compilation, no environment-variable gating.
- Do not restructure test collections or parallelization — that changes execution semantics and is not
  low-risk.
- Don't touch product code at all.

## 5. Gate

1. `dotnet test` with no filter still runs **every** test, and the count is unchanged from before this brief.
2. `dotnet test --filter "Category!=Slow"` runs green and is **measurably** faster — report both wall-clock
   times and the test counts.
3. `dotnet test tests/Ui.Tests tests/Firewall.Tests` runs green.
4. The top-10 slowest tests with durations are recorded in the completion note, before and after tagging.
5. Root `CLAUDE.md` documents the fast loop and the full-suite-at-gates rule.

## 6. On completion

Note in `CLAUDE.md` which classes were tagged and why, the measured before/after times, and — explicitly —
that **gate item 1 in every brief still means the full suite**.
