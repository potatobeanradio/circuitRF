# UI (Avalonia) — local conventions

Standing instructions for `src/Ui`. Read with the root `CLAUDE.md`. The UI is how people drive the
engine; it must never become the source of truth for simulation.

## Framework & patterns
- **Avalonia 12**, single codebase for Windows/macOS/Linux. Mirror splotRF's structure, controls,
  and packaging recipes wherever possible — it's our proven sibling app (same stack, MIT).
- **MVVM via CommunityToolkit.MVVM.** Views are thin; logic lives in view models. No simulation
  logic in code-behind.
- **The GUI never simulates the design layer directly.** It always builds/edits the design layer,
  then asks the engine to elaborate and run. Results come back as a **`DataSet`** (named
  single-kind `DataCube`s).

## Schematic canvas — the performance-critical control
- **Do NOT render components as individual styled controls.** A 10,000-component schematic will die
  that way. Render the canvas yourself via a custom control (`DrawingContext`, dropping to
  **SkiaSharp** for hot paths), with **viewport virtualization** and a **spatial index** (e.g.
  quadtree/grid) for hit-testing and pan/zoom.
- Lean on Avalonia 12's rendering work (deferred composition, dirty-rect tracking) but verify with
  a large stress schematic in `testdata/`, not a toy one.

## Wiring & auto-routing
- Obstacle-aware auto-routing must avoid drawing wires over placed symbols. Start with **orthogonal
  A\*** over a coarse grid using the spatial index for obstacles; refine later. Keep the router
  separable and testable headless.

## Editing model
- **Undo/redo via the command pattern** across all editors (schematic, symbol). Every mutation is a
  reversible command; nothing mutates model state directly.
- **Copy/paste via the system clipboard**, all or a selectable subset of a schematic, to/from other
  schematics.
- **Hierarchy navigation:** push into a sub-cell's schematic, edit, pop back. Editing a cell affects
  every instance; instance-level parameter overrides (root `CLAUDE.md` → expressions) stay per-instance.

## Data Display
Plots and tables are **splotRF** controls from `RfCore` — do not reimplement Smith/polar/rectangular
plotting here. Support measured-vs-simulated overlay (a lab Touchstone over a simulated result
cube from the `DataSet`).

## "Easy" budgets (testable)
Honor the PRD §12 click budgets (e.g. placed FET → running HB power sweep in ≤ 8 actions). Advanced
settings must remain reachable but must not clutter the default path.

# circuitRF UI Coding Context

## Abstract

circuitRF UI development should prioritize accessibility, clarity, consistency, and professional presentation. Interfaces should follow modern Human Interface Guidelines where practical, using system fonts, semantic styling, adaptive layouts, intuitive interactions, and restrained motion. UI text and workflows should target RF engineers, researchers, technical managers, and advanced hobbyists. The overall goal is to create a polished, trustworthy, efficient engineering application suitable for advanced technical workflows.

---

# 1. Core Design Principles

* Prioritize usability, clarity, readability, and consistency.
* Follow Apple Human Interface Guidelines where practical, but treat them as preferred guidance rather than strict rules.
* Favor clean, minimal, engineering-oriented interfaces.
* Avoid visual clutter and unnecessary animation.
* Design for desktop workflows and technically sophisticated users.

---

# 2. Accessibility

## Text and Typography

* Default font size: 12–13 pt.
* Minimum font size: 9–10 pt.
* Support scaling text up to at least 200%.
* Avoid ultra-light font weights.
* Prefer:

  * Regular
  * Medium
  * Semibold
  * Bold

## Contrast

Use WCAG AA contrast guidance:

* Normal text under 18 pt: minimum 4.5:1 contrast.
* Large text (18+ pt): minimum 3:1 contrast.
* Bold text: minimum 3:1 contrast.

## Visual Communication

* Never rely on color alone to convey meaning.
* Use icons, shapes, labels, or patterns alongside color.
* Support color-blind accessibility.

## Layout Accessibility

* Ensure layouts adapt cleanly to large font sizes.
* Reduce truncation wherever possible.
* Prefer stacked layouts when horizontal space becomes constrained.

---

# 3. Colors

* Use system colors whenever possible.
* Avoid hardcoded colors.
* Support light and dark modes automatically.
* Use colors consistently across the application.
* Do not reuse the same color for conflicting meanings.

---

# 4. Icons and Symbols

## UI Icons

* Use Material.Icons.Avalonia for normal UI icons.
* Maintain consistency in:

  * stroke weight
  * scale
  * detail level
  * perspective

## Cell Symbols

Cell symbols are NOT UI icons.

They serve two purposes:

1. Visual identification of RF/electrical components.
2. Port connection geometry for schematic editing.

Do NOT use Material.Icons.Avalonia for schematic cell symbols.

---

# 5. Layout and Visual Hierarchy

## General Layout

* Group related content visually.
* Use spacing and alignment consistently.
* Prioritize important information near the top-left (LTR layouts).
* Avoid overcrowding controls.
* Ensure controls remain visually distinct from content.

## Content Flow

* Use progressive disclosure for advanced or hidden information.
* Avoid overwhelming the user with excessive detail at once.

## Window and Screen Usage

* Extend backgrounds fully to edges.
* Scrollable regions should fill available space.
* Respect safe areas and window chrome.
* Avoid placing critical controls at the bottom edge of windows.

---

# 6. Motion and Animation

* Use animation only when it improves clarity.
* Keep animations subtle, brief, and purposeful.
* Avoid decorative or excessive motion.
* Use motion to:

  * reinforce navigation
  * indicate scrolling
  * communicate state transitions

---

# 7. Localization and RTL Support

* Support localization using ResX.
* Consider right-to-left layout support where practical.
* Flip directional icons in RTL layouts.
* Do NOT flip icons representing real-world objects.

---

# 8. Typography and Semantic Styles

## Fonts

* Use the system UI font.
* Create centralized semantic Avalonia styles.
* Avoid excessive typeface variation.

## Hierarchy

Use typography consistently to indicate hierarchy:

* weight
* size
* spacing
* leading

## Leading

* Loose leading improves readability in large text blocks.
* Tight leading may be used in compact lists.
* Avoid tight leading for 3+ line text blocks.

---

# 9. UI Writing Style

## Tone

UI language should be:

* professional
* concise
* direct
* trustworthy
* technically appropriate

Target audience:

* RF engineers
* researchers
* technical managers
* advanced hobbyists

## Writing Rules

* Prefer active voice.
* Use clear verbs for actions.
* Avoid overly clever or playful wording.
* Use consistent terminology.
* Prefer concise labels.
* Avoid unnecessary possessive pronouns.
* Avoid vague “we” language in errors.

## Error Messages

* Make errors actionable and specific.
* Clearly explain:

  * what failed
  * why
  * how to fix it

---

# 10. Data Entry

* Dynamically validate fields while editing.
* Give immediate feedback on invalid input.
* Use numeric formatters for numeric fields.
* Do not request data that can be inferred automatically.
* Never prepopulate password fields.
* Disable Continue/Next actions until required data is valid.

---

# 11. Drag and Drop

## Behavior

* Support drag-and-drop broadly where practical.
* Support both move and copy semantics correctly.
* Support undo for drag operations whenever possible.

## Visual Feedback

Provide:

* drag previews
* insertion indicators
* valid/invalid destination feedback
* progress indicators for long transfers

## UX Expectations

* Keep dragged items selected after drop.
* Support multi-item drag where appropriate.
* Preserve styling when dropping rich text/content.

---

# 12. User Feedback

Feedback should communicate:

* status
* progress
* success/failure
* warnings
* recoverable problems

## Best Practices

* Keep feedback contextual and near related UI.
* Use passive feedback for status updates.
* Use interruptive alerts only for serious or destructive actions.
* Ensure all feedback mechanisms are accessible.

---

# 13. Engineering Expectations for AI UI Code Generation

When generating UI code for circuitRF:

* Prefer Avalonia UI conventions and semantic styles.
* Reuse centralized styling resources.
* Use adaptive/responsive layouts.
* Maintain accessibility standards by default.
* Favor readability and clarity over visual novelty.
* Avoid hardcoded dimensions/colors unless required.
* Use consistent spacing and alignment.
* Keep interfaces efficient for technical workflows.
* Ensure keyboard accessibility where practical.
* Minimize unnecessary dependencies and visual complexity.
* Treat schematic symbols separately from standard UI iconography.
