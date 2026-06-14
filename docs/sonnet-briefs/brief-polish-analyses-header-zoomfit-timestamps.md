# Brief: polish-analyses-header-zoomfit-timestamps (B3+B4+B5)

Three small, independent polish items. All ready to implement.

---

## B5 — Symbol Editor Zoom-to-Fit includes pins

**File:** `src/Ui/Controls/SymbolEditorCanvas.cs` (`ZoomToFitInternal`).

`ZoomToFitInternal` computes bounds from `SymbolGeometry.ComputeBb(_renderSymbol.Primitives)` only, so
pins outside the primitive bbox (or a pins-only symbol) get clipped or hit the blank-symbol default.
Union pin positions into the bbox.

Replace the bbox line:

```csharp
        var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(_renderSymbol.Primitives);
```

with:

```csharp
        var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(_renderSymbol.Primitives);
        // Include pins so they're framed too (and so a pins-only symbol isn't treated as blank).
        const double pinPad = 10.0;   // world units: pin dot + a little label room
        foreach (var p in _renderSymbol.Pins)
        {
            bbMinX = Math.Min(bbMinX, p.LocalX - pinPad);
            bbMinY = Math.Min(bbMinY, p.LocalY - pinPad);
            bbMaxX = Math.Max(bbMaxX, p.LocalX + pinPad);
            bbMaxY = Math.Max(bbMaxY, p.LocalY + pinPad);
        }
```

The existing `if (bbMinX >= bbMaxX || bbMinY >= bbMaxY)` blank-symbol guard below still handles the
truly-empty case (no primitives **and** no pins).

**Schematic Zoom-to-Fit: no change** — `SchematicCanvas.ZoomToFit` fits the model bbox and component port
leads live inside each component's `FullBb`, so ports are already framed.

---

## B4 — Analyses authoring header shows the schematic filename + Help button

**Files:** `AnalysesListViewModel.cs`, `Dock/AnalysesTool.cs`, `WorkspaceViewModel.cs`,
`Views/Analyses/AnalysesListView.axaml`(+`.axaml.cs`).

### Header label

1. `AnalysesListViewModel`: add
   ```csharp
   [ObservableProperty] private string _headerLabel = "Analyses";
   ```
   Change `SetActiveSchematic` to take a name and set it:
   ```csharp
   public void SetActiveSchematic(SchematicViewModel? vm, string? schematicName = null)
   {
       // … existing body unchanged …
       HeaderLabel = string.IsNullOrEmpty(schematicName) ? "Analyses" : schematicName;
   }
   ```
2. `AnalysesTool.SetActiveSchematic`: forward the name:
   ```csharp
   public void SetActiveSchematic(SchematicViewModel? vm, string? schematicName = null)
       => ListVm.SetActiveSchematic(vm, schematicName);
   ```
3. `WorkspaceViewModel`: at the existing `AnalysesTool?.SetActiveSchematic(...)` call site (active-document
   change handler), pass the active `SchematicDocument`'s filename:
   ```csharp
   string? name = schDoc.FilePath is { } fp ? System.IO.Path.GetFileName(fp) : null;
   _factory.AnalysesTool?.SetActiveSchematic(schDoc.ActiveViewModel, name);
   ```
   (Scratch/unsaved → `FilePath` null → header falls back to "Analyses".)

### View — bind header, clip (no ellipsis), add Help

Header `TextBlock` (Grid.Column 0):
```xml
            <TextBlock Grid.Column="0"
                       Text="{Binding HeaderLabel}"
                       FontWeight="SemiBold" FontSize="12"
                       TextTrimming="None" ClipToBounds="True"
                       VerticalAlignment="Center"/>
```
`TextTrimming="None"` + the `*` column = clip (cut off), not "…".

Add a Help button: append one `Auto` to the toolbar `ColumnDefinitions` (currently `*` + 14×`Auto` → add a
15th `Auto`), and add at the new last column (15):
```xml
            <Button Grid.Column="15"
                    Click="OnHelp"
                    ToolTip.Tip="Help"
                    Padding="3,2" Background="Transparent" BorderThickness="0" Margin="2,0,0,0">
                <mi:MaterialIcon Kind="HelpCircleOutline" Width="14" Height="14"
                                 Foreground="{DynamicResource SystemBaseMediumColor}"/>
            </Button>
```
In `AnalysesListView.axaml.cs`, add the stub handler:
```csharp
    private void OnHelp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // TODO: open Analyses help. Stub for now.
    }
```

---

## B3 — message-timestamp display setting (uses the existing AppPreferences + Settings)

Default **time-only**; options Time / Date+Time / Hidden. Wires into the existing
`AppPreferences`/`AppPreferencesIo` (persisted to `preferences.json`) and the General tab of `SettingsView`.

### 1. `src/Ui/Messages/MessageEntry.cs` — mode enum, global, mode-aware `TimeText`

Add (same file, `CircuitRF.Ui.Messages` namespace):
```csharp
public enum MessageTimestampMode { Time, DateTime, None }

/// <summary>App-wide message-timestamp display mode. Set at startup from AppPreferences and by the
/// Settings dialog; MessageEntry.TimeText reads it. ModeChanged lets the Messages view re-render.</summary>
public static class MessageDisplay
{
    private static MessageTimestampMode _mode = MessageTimestampMode.Time;
    public static MessageTimestampMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; ModeChanged?.Invoke(null, EventArgs.Empty); }
    }
    public static event EventHandler? ModeChanged;
}
```
Change `TimeText` to honor it:
```csharp
    public string TimeText => MessageDisplay.Mode switch
    {
        MessageTimestampMode.None     => "",
        MessageTimestampMode.DateTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
        _                             => Timestamp.ToString("HH:mm:ss"),
    };
```

### 2. `src/Ui/Theming/AppPreferences.cs` — persisted field

Add `using CircuitRF.Ui.Messages;` and, in `AppPreferences`:
```csharp
    // Message timestamp display — null means default (Time). Serialized as a number, like the others.
    [JsonPropertyName("message_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageTimestampMode? MessageTimestamp { get; set; }
```

### 3. `src/Ui/App.axaml.cs` — apply at startup

In `OnFrameworkInitializationCompleted`, right after the existing `var prefs = AppPreferencesIo.Load();`:
```csharp
        CircuitRF.Ui.Messages.MessageDisplay.Mode =
            prefs.MessageTimestamp ?? CircuitRF.Ui.Messages.MessageTimestampMode.Time;
```

### 4. `SettingsView` — combo in the General tab

In `SettingsView.axaml`, add to the General tab `StackPanel`, after the Copy/Export `Grid`:
```xml
                        <!-- Messages section -->
                        <TextBlock Text="Messages" FontSize="11" FontWeight="SemiBold"
                                   Opacity="0.55" Margin="0,20,0,6"/>
                        <Grid ColumnDefinitions="130,*">
                            <TextBlock Grid.Column="0" Text="Timestamps:"
                                       VerticalAlignment="Center" FontSize="12"/>
                            <ComboBox  Grid.Column="1" Name="MsgTimestampCombo"
                                       FontSize="12" MinWidth="180" HorizontalAlignment="Left"
                                       SelectionChanged="OnMsgTimestampChanged"/>
                        </Grid>
```

In `SettingsView.axaml.cs`:
- In `LoadGeneralPrefs` (inside the `_updatingGeneral` block), populate + select:
  ```csharp
      MsgTimestampCombo.ItemsSource   = new[] { "Time", "Date + Time", "Hidden" };
      MsgTimestampCombo.SelectedIndex = (int)(prefs.MessageTimestamp ?? MessageTimestampMode.Time);
  ```
  (Combo order matches the enum: Time=0, DateTime=1, None=2.)
- Add the handler (needs `using CircuitRF.Ui.Messages;`):
  ```csharp
  private void OnMsgTimestampChanged(object? sender, SelectionChangedEventArgs e)
  {
      if (_updatingGeneral || MsgTimestampCombo.SelectedIndex < 0) return;
      var mode = (MessageTimestampMode)MsgTimestampCombo.SelectedIndex;
      AppPreferencesIo.Update(p => p.MessageTimestamp = mode);
      MessageDisplay.Mode = mode;   // live
  }
  ```

### 5. `src/Ui/Views/Messages/MessagesView.axaml.cs` — live refresh of existing rows

`TimeText` has no change notification, so re-render the list when the mode changes. Subscribe on attach,
unsubscribe on detach (no static-event leak):
```csharp
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MessageDisplay.ModeChanged += OnTimestampModeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MessageDisplay.ModeChanged -= OnTimestampModeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimestampModeChanged(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MessagesTool tool)
            {
                MessagesListBox.ItemsSource = null;          // force TimeText bindings to re-evaluate
                MessagesListBox.ItemsSource = tool.Messages;
            }
        });
```
(Add `using Avalonia.Controls;` if not present — it is. `MessageDisplay` is in `CircuitRF.Ui.Messages`,
already imported.)

---

## Verification

- **B5:** open a symbol whose pins sit outside the body (or a pins-only symbol) → Zoom-to-Fit frames all
  pins with margin; normal symbols unchanged.
- **B4:** open a saved schematic → Analyses header shows `name.csch`; long names clip (no "…"); scratch →
  "Analyses"; Help button visible/clickable (no-op).
- **B3:** Settings ▸ General ▸ Messages ▸ Timestamps: default **Time**; switch to **Date + Time** → open
  messages immediately show the date; **Hidden** → timestamps disappear; relaunch → choice persists
  (`preferences.json`).

## Acceptance

- Symbol Zoom-to-Fit always frames pins; schematic unchanged.
- Analyses header tracks the active schematic filename (ext, no path, clipped) + Help stub.
- Message timestamp mode is a persisted General-tab setting, default time-only, applied live and at launch.
