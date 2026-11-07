# Brief LCP-3 — The Component ports section in the `.cem` editor

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp3-<n>` · **Depends on:** LCP-1
**Area:** `src/Ui/Layout/Em/EmSetupEditorViewModel.ComponentPorts.cs`, `EmSetupEditorViewModel.cs`, `.Palace.cs`,
`src/Ui/Views/Layout/EmSetupEditorView.axaml`, `src/Ui/ThreeD/` where the `.c3d` host is wired.

## The gap

The section exists and works, inside a `.c3d`. `ShowComponentPorts` requires `IsEmbedded`, and `EmComponentPortsContext` is tied to
`C3dDocument` and `C3dElaboration`. A standalone `.cem` shows nothing. In a `.c3d`, a port's Z0 and width are edited in the Properties
Inspector through the Object Tree, and a `.cem` has neither.

## Requirements

**R-lcp3-1 — One section, two hosts.** Replace the context record with what the list actually needs: list the placed parts, build and
verify for Check, and post a message. The `.c3d` host wraps today's behaviour unchanged. The `.cem` host reads the layout the panel has
already resolved. No control differs between them in the list itself: the same checkbox rows, **Every two-terminal part**, **Check**
and status line, in the same XAML.

**R-lcp3-2 — Where it shows.** On a `.cem`: Planar, and any 3D problem a `.c3d` shows it for. Hidden on a thermal setup, on an
electrostatic one, and on the uniform-line kernel, whose ports cannot be anything but the line's two ends. Check builds off the UI
thread as it does now, and posts the same lines to Messages. Until LCP-5 lands, a 3D `.cem`'s Check reports the refusal: the section is
wired with its list visible, but the merge order should make this window short.

**R-lcp3-3 — Per-part settings in the port list.** The existing `PortRows` (Planar) and 3D port rows gain a row per built component port,
reading "Port 3 · C1". Z0 writes into the part's entry, not `PortZ0s`. A width box and, for a part of more than two pads, a reference
picker of its pins, both writing the entry; blank is the default, shown as a placeholder. The Type combo does not appear on these rows:
a component port's type is not a choice. Same commit path as `C3dPropertiesViewModel`'s `SetComponentPortOverride`, one undo entry each.

**R-lcp3-4 — A part that left the layout.** An entry naming an unplaced part keeps its row and is marked, so it can be unticked. It is
not silently dropped from the file: renaming a part in the layout must not erase its settings without saying so. Check's message names
it.

**R-lcp3-5 — Refresh.** The list re-reads the parts when the panel's existing Refresh runs. A layout edit marks a Check's verdicts stale
and does not rebuild per keystroke: on a board the projection is seconds of work.

**R-lcp3-6 — Tooltip and wording.** `ComponentPortsTip` is the only explanation in the panel, and it depends on the solver: on Planar it
says a port goes across a strip the solver adds between the pads, and that nothing is added to the layout. No extra text under the
list. The word in the panel is "component port" throughout.

**R-lcp3-7 — Controls never shift.** The section's position is reserved by setup type as the rest of the panel's is. Ticking a part never
moves another control.

## Gates

No solves. View-model tests: toggling writes the list, undo restores it, `*` keeps a part's override, a row's Z0 writes its entry, and
the `.cem` round-trips byte-identically when nothing was changed. **Visual:** `tools/UiSnapshot` renders of the section on a Planar `.cem`,
a 3D `.cem`, and a `.c3d` host (unchanged), each looked at and listed in the closing note, including a list with one refused part and a
long list scrolling.
