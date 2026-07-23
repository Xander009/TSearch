# TSearch

A clientside Vintage Story mod. Hover an item and press T to highlight
every nearby container that holds it with highlights that show through walls
and a camera that snaps to the nearest match.

Inspired by the NEI item highlight feature from the GregTech: New Horizons (FindIt)
Minecraft modpack. TSearch is the successor to my original InvSearch mod.

Targets Vintage Story 1.22.5 (.NET 10).

## Features

- Press T while hovering an item in your inventory, hotbar, an open container,
  your hand, or a Survival Handbook item page to scan nearby containers.
- Matching containers get a seethrough highlight rendered through walls via a small custom shader
- If a container GUI is open, the matching slot inside it is highlighted too.
- On a successful search the camera snaps toward the nearest container
  (always the nearest, even with multiple results); open windows are closed first
  for a clear view, and a snap sound plays.
- Highlights auto-clear after a timeout, when you walk away, or on pressing T again.
- Hotkey is rebindable in Options -> Controls.
- Everything tunable via a JSON config.

## Building

```bat
build.bat
```

On a successful build the mod is zipped and copied straight into your
`VintagestoryData\Mods` folder.

## Configuration

Edit `VintagestoryData\ModConfig\tsearch.json` 
Colors are RGBA arrays, each channel `0-255`.

| Key | Default | Meaning |
|---|---|---|
| `ScanRange` | `32` | Horizontal (X/Z) scan radius in blocks |
| `ScanRangeVertical` | `3` | Vertical (Y) scan radius in blocks |
| `HighlightDurationMs` | `10000` | Auto-clear timeout |
| `ClearDistanceBlocks` | `6` | Auto-clear once you walk this far from the search origin (effective threshold is at least `ScanRange`, so highlights survive while you walk to a container you just found) |
| `SearchFromHand` | `false` | Count an item held in your active hand as a search target when nothing is hovered |
| `SeeThrough` | `true` | Draw highlights through walls (custom renderer). `false` = plain engine highlight |
| `SnapCameraToNearest` | `true` | Snap camera to nearest match on search |
| `CloseGuisOnSnap` | `true` | Close open windows before snapping |
| `PlaySound` | `true` | Play a sound on a successful search |
| `ChatFeedback` | `true` | Print "Found N container(s)…" chat messages |
| `EdgeColor` | `[255,165,0,255]` | Outline color (RGBA) |
| `FillColor` | `[255,165,0,60]` | Box fill color (RGBA) |
| `Glow` | `0.9` | Extra glow (0–1) so highlights stay visible in the dark |

## Notes / limitations

- The slot highlight color inside open GUIs is fixed by the engine's GUI
  renderer and isn't configurable (only the world highlights are).
- The Survival Handbook source works on item detail pages only, not on search
  results or text/guide pages.
- Reads a few private/internal fields via reflection (`GuiComposer`,
  `GuiDialogHandbook`), so a future VS update that renames them could break the
  handbook or slot-highlight paths. The core search and see-through highlight do
  not depend on those.
