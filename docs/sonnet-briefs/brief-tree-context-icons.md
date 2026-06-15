# Sonnet Brief — Project Tree: icons for every context-menu item

Add a Material icon to each `MenuItem` in the Project Tree context menu
(`src/Ui/Views/ProjectTree/ProjectTreeView.axaml`). Pure AXAML; no VM/logic changes.

**Do this AFTER** the "Open" item (`brief-tree-hide-junk-and-open.md`) and the Remove items
(`brief-tree-trash-and-file-remove.md`, `brief-tree-remove-cell.md`) have landed, so every item exists to
receive an icon. If those aren't in yet, add icons only to the items that currently exist and leave the rest for
when they land.

## How
For each `<MenuItem>`, add an `Icon`:
```xml
<MenuItem Header="Open" Command="{Binding ActivateCommand}" IsVisible="{Binding IsOpenableFile}">
    <MenuItem.Icon><mi:MaterialIcon Kind="OpenInApp" Width="14" Height="14"/></MenuItem.Icon>
</MenuItem>
```
The `mi` namespace is already imported in the view
(`xmlns:mi="clr-namespace:Material.Icons.Avalonia;assembly=Material.Icons.Avalonia"`).

**Do NOT set `Foreground` on these icons to a `System*Color` resource key** — those keys resolve to `Color`, not
`IBrush`, and silently fail on the `IBrush` `Foreground` property (known project footgun). Leave the default
(inherits menu foreground) or use the app brush `CrfIconBrush` if a tint is wanted. Width/Height 14 to match the
tree's row icons.

## Suggested Kind per item (owner can tweak to taste)
| Menu item                | `Kind`              |
|--------------------------|---------------------|
| Open                     | `OpenInApp`         |
| Open External…           | `OpenInNew`         |
| Copy to Workspace        | `ContentCopy`       |
| Remove Reference         | `LinkVariantOff`    |
| Make Primary             | `Star`              |
| Reveal in Finder/Explorer| `FolderSearchOutline` |
| Open Schematic           | `FileDocumentOutline` |
| Open Symbol              | `VectorSquare`      |
| New Cell                 | `IntegratedCircuitChip` |
| New Schematic            | `FilePlusOutline`   |
| New Symbol               | `ShapePlusOutline`  |
| New Layout               | `LayersPlusOutline` |
| Edit Parameters          | `Pencil`            |
| Remove Data Display      | `TrashCanOutline`   |
| Remove                   | `TrashCanOutline`   |
| Remove Cell              | `TrashCanOutline`   |

If any `Kind` above isn't a valid `MaterialIconKind` in the pinned Material.Icons.Avalonia 3.0.2, pick the
closest valid one (build will fail on an invalid enum, so verify each compiles).

## Gate
Build 0W/0E. Every context-menu item shows its icon; no `System*Color`-on-`IBrush` foreground anywhere. No
behavior change.
