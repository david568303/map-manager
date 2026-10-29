# Map Manager (dev builds)

A BepInEx 6 IL2CPP mod for **CarX Drift Racing Online 2** with tools for working with custom (mod.io) maps. It is
built from independent **features**; each has its own config section and Enabled switch, so features can be added,
switched off or fail without affecting the others. Versions are not bumped for dev builds; the log carries the commit
hash instead.

Game-wide helpers (safe patching, texture access, main-thread work) come from the
[compatibility layer](https://github.com/david568303/Dro2-compatibility-layer), which this mod requires. The release
zip contains both.

## Features

### MapPreview - schematic previews for custom maps

The game's own maps show a clean schematic in the map selection screen (dark background, roads and surfaces
outlined in grey). A custom map ships only a top-down picture of the terrain, which the game shows as-is: all green,
with the circuit hard to read.

This feature redraws that picture in the same kind of schematic: scenery (grass, dirt, water) becomes the dark
background; greyer pixels are surfaces, dark ones as asphalt and light ones as concrete/buildings; each surface gets
a light outline.

How: `ModWorldMapTrack.Build(modId, ModMinimapMeta, Texture2D)` receives the picture. A Harmony prefix copies its
pixels out, the filter (`src/SchematicFilter.cs`, plain image code, tested off-line) runs on a background thread, and
the result is written back into the same texture on the main thread.

Known limits of this first version: the preview shows the green picture for a moment before it switches to the
schematic; only textures with Read/Write enabled are handled (anything else is logged as
`MAPMANAGER_PREVIEW_UNREADABLE`); only the world-map preview is hooked, not the flat in-menu minimap.

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

Download `MapManager.zip` from the **latest** pre-release and extract it into the game folder. It adds
`BepInEx/plugins/MapManager` and `BepInEx/plugins/CarXModManager` (the compatibility layer).

In `BepInEx/LogOutput.log`, look for `MAPMANAGER_LOADED` (with `MapPreview: on`), then `MAPMANAGER_PREVIEW_START` and
`MAPMANAGER_PREVIEW_DONE` when a custom map's preview is opened. A hook that could not be installed (for example
after a game update moves the method) is logged as an Error, which also shows up in the layer's bottom-left status
line.

## Build

```bash
dotnet build -c Release                           # needs the game's BepInEx folder (interop + the compatibility layer)
dotnet build -c Release -p:DeployToGame=true      # also copies MapManager.dll to BepInEx/plugins/MapManager
dotnet run --project tools/FilterPreview -- --selftest
dotnet run --project tools/FilterPreview -- in.rgba out.rgba   # raw file: int32 width, int32 height, RGBA bytes
```

## Adding a feature

Implement `IFeature` (`src/Features/`), bind its config in `BindConfig`, install its hooks in `Install` (use
`SafePatch.TryPatch` so failures are logged as errors under this mod's name), and add it to the `Features` list in
`src/Plugin.cs`.
