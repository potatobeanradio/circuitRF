# Brief: fix-messages-sink-stale — Messages post to an orphaned tool after workspace switch

**Severity:** high — after opening/creating a workspace, **no** messages appear in the Messages pane
(saves, "Opened", errors — nothing). This is why B7's save-logging looked like it "didn't get through":
the logging is correct, but it posts to a `MessagesTool` instance that is no longer the one displayed.

## Root cause (confirmed in code)

`WorkspaceViewModel`:

```csharp
public IMessageSink Messages { get; }          // get-only — assigned once, never re-pointed

public WorkspaceViewModel()
{
    _factory = new CircuitRfDockFactory();
    var layout = _factory.CreateLayout();        // creates MessagesTool instance #1
    ...
    Messages = _factory.MessagesTool ?? throw…;  // captures #1
    ...
}
```

But `NewWorkspace` and `SwitchToWorkspace` (the latter backs Open Workspace **and** Open Recent) both do:

```csharp
var newLayout = _factory.CreateDefaultLayout();  // == CreateLayout() → NEW MessagesTool instance #2
_factory.InitLayout(newLayout);
Layout = newLayout;                               // view now binds instance #2
...
// re-wires ProjectTreeTool.SetActions, SubscribeToFilterState, SubscribeToTreeSelection,
// and the DocumentDock PropertyChanged — but NOT Messages.
```

`CircuitRfDockFactory.CreateDefaultLayout() => CreateLayout()`, and `CreateLayout()` does
`MessagesTool = new MessagesTool();` (a fresh instance, reassigning the factory property).

Result: the view renders instance **#2** (current `_factory.MessagesTool`, in the new `Layout`), while
`WorkspaceViewModel.Messages` still references instance **#1**. Every post — `Messages.Success("Saved", …)`,
and even the `Messages.Clear()` + `Messages.Info("Opened", …)` inside `SwitchToWorkspace` — lands on the
orphaned #1 and is never shown. At fresh startup nothing has swapped yet, so `"circuitRF ready."`
appears on #1 (still the displayed tool) — matching the original "sometimes" report.

(There is no Dock-layout deserialization to blame — `WriteWorkspaceFile` notes `DockLayout` stays null;
the only source of `MessagesTool` instances is the factory.)

## Fix (single source of truth — can't drift)

Make `Messages` always resolve to the factory's **current** tool instead of caching one. Replace the
get-only property + its constructor assignment:

```csharp
// was: public IMessageSink Messages { get; }
public IMessageSink Messages => _factory.MessagesTool
    ?? throw new InvalidOperationException("DockFactory must expose MessagesTool.");
```

And in the constructor, **remove** the assignment line:

```csharp
// DELETE this line (Messages is now a computed property):
// Messages = _factory.MessagesTool ?? throw new InvalidOperationException("DockFactory must expose MessagesTool.");
```

`CreateLayout()`/`CreateDefaultLayout()` always set `_factory.MessagesTool` before `Messages` is read,
so the computed property is always non-null and always points at the tool currently in `Layout` (the one
the view binds). No re-pointing needed anywhere; the drift class is eliminated.

### Alternative (if you prefer a stored field + explicit re-point)

Keep a settable backing field and re-point it next to the other re-wiring in **both** `NewWorkspace` and
`SwitchToWorkspace`:

```csharp
// after `Layout = newLayout;` and alongside SetActions / SubscribeToFilterState / DocumentDock re-wire:
Messages = _factory.MessagesTool!;
```

The computed-property version is preferred — it's impossible to forget at a future call site.

## Also verify: no other stale captures

Grep for anything that captures the sink instance once and could go stale across a workspace switch:

- `_factory.MessagesTool` — only `WorkspaceViewModel` should read it; confirm nothing else stores it.
- Any constructor that receives an `IMessageSink` and caches it (e.g. a run service, NetExtractor
  validation helper, a tool VM). Most posting flows through `WorkspaceViewModel.Messages`
  (`RunAnalysis`, `WriteNetlist`, save paths), which the fix covers. If a long-lived component caches a
  sink handed to it at construction, give it the same treatment (resolve live, or re-point on switch).
  Note findings in the PR; if everything posts via `WorkspaceViewModel.Messages`, no further change.

## Verification (runtime proof — don't just trust the build)

1. Launch → pane shows `"circuitRF ready."`.
2. **Open Workspace** (or Open Recent / New Workspace) → pane shows `"Opened"` / `"New workspace … created."`
   (previously these vanished).
3. Save a schematic → a `Saved` message with a clickable path link appears (this is the user's repro).
4. Trigger an error path (e.g. open a non-workspace folder) → the error message appears.
5. Open a *second* workspace in the same window, then save again → message still appears (proves it
   tracks the latest tool, not a one-time re-point).

## Notes

- This is a pre-existing latent bug surfaced by B7 (which made the absence of save logs obvious). It is
  not caused by B6 (Messages UX) or B7 (coverage); both are correct.
- After the fix, the `Messages.Clear()` in `SwitchToWorkspace` correctly clears the displayed pane, and
  the subsequent `"Opened"` shows on it.
