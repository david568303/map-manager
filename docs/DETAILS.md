# Map Manager (dev builds)

A BepInEx 6 IL2CPP mod for **CarX Drift Racing Online 2** with tools for working with custom (mod.io) maps. It is
built from independent **features**; each has its own config section and Enabled switch, so features can be added,
switched off or fail without affecting the others. Versions are not bumped for dev builds; the log carries the commit
hash instead.

## Requires the compatibility layer

Map Manager **requires** the [Dro2-compatibility-layer](https://github.com/david568303/Dro2-compatibility-layer),
a separate mod with its own release. Install its 0.1-alpha release as well; without it BepInEx will not load Map
Manager. Game-wide helpers (safe patching, texture access, main-thread work) come from it.

## Features

### MapPreview - schematic previews for custom maps

The game's own maps show a clean schematic in the map selection screen (dark background, roads and surfaces
outlined in grey). A custom map ships only a top-down picture of the terrain, which the game shows as-is: all green,
with the circuit hard to read.

This feature redraws that picture in the same kind of schematic: scenery (grass, dirt, water) becomes the dark
background; greyer pixels are surfaces, dark ones as asphalt and light ones as concrete/buildings; each surface gets
a light outline.

Buildings and pads are drawn with straight edges only: a clean rectangle becomes an exact rectangle, a round one a
perfect circle, anything else a polygon of straight segments. Buildings that touch are told apart and get a thin
seam between them. Roads keep smooth, anti-aliased outlines.

How: `ModWorldMapTrack.Build(modId, ModMinimapMeta, Texture2D)` receives the picture. A Harmony prefix copies its
pixels out (through the GPU when the texture is not readable), the filter (`src/SchematicFilter.cs`, plain image
code, tested off-line) redraws them, and the game gets the redrawn texture before it ever uses the original, so the
green picture is not shown. The result is cached on disk (`BepInEx/cache/MapManager`), keyed by the picture's
SHA-256 and the settings, so an unchanged map is not redrawn.

Known limits: only the world-map preview is hooked, not the flat in-menu minimap; whether the game's transparent
material makes the scenery see-through is not yet confirmed in-game (set `UseTransparentMaterial = false` and
`BackgroundOpacity = 1` if the map looks wrong).

## Config

`BepInEx/config/carx.mapmanager.cfg`, created on first launch:

```ini
[General]
Enabled = true      # false = nothing is installed; the game behaves exactly as without this mod

[MapPreview]
Enabled = true      # false = custom maps keep the game's normal green preview
# ...plus the colours (#RRGGBB) and thresholds that decide what counts as road, to tune the look without rebuilding
```

## Install

1. Install the [compatibility layer](https://github.com/david568303/Dro2-compatibility-layer/releases/tag/0.1-alpha)
   (its `CarXModManager.zip`) into `BepInEx/plugins`.
2. Download `MapManager.zip` from this repo's **0.1-alpha** pre-release and extract it into `BepInEx/plugins` too. It
   adds only the `MapManager` folder.

In `BepInEx/LogOutput.log`, look for `MAPMANAGER_LOADED` (with `MapPreview: on`), then `MAPMANAGER_PREVIEW_START` and
`MAPMANAGER_PREVIEW_DONE` when a custom map's preview is opened. A hook that could not be installed (for example
after a game update moves the method) is logged as an Error, which also shows up in the layer's bottom-left status
line.

## Build

```bash
dotnet build -c Release                           # needs the game's BepInEx folder (interop + the installed compatibility layer)
dotnet build -c Release -p:DeployToGame=true      # also copies MapManager.dll to BepInEx/plugins/MapManager
dotnet run --project tools/FilterPreview -- --selftest
dotnet run --project tools/FilterPreview -- in.rgba out.rgba   # raw file: int32 width, int32 height, RGBA bytes
```

## Adding a feature

Implement `IFeature` (`src/Features/`), bind its config in `BindConfig`, install its hooks in `Install` (use
`SafePatch.TryPatch` so failures are logged as errors under this mod's name), and add it to the `Features` list in
`src/Plugin.cs`.
