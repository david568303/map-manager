# Map Manager (dev builds)

A BepInEx 6 IL2CPP mod for **CarX Drift Racing Online 2** that makes custom (mod.io) maps look right in the game's
map preview.

**The problem:** the game's own maps show a clean schematic in the map selection screen (dark background, roads and
surfaces outlined in grey). A custom map has no such schematic, so the game shows a top-down picture of the terrain:
all green, with the circuit hard to read.

**The fix:** take that top-down picture and redraw it as the same kind of schematic: scenery (grass, dirt, water)
becomes the dark background; greyer pixels are surfaces, dark ones as asphalt and light ones as concrete/buildings;
each surface gets a light outline.

## Status

| Part | State |
|---|---|
| Schematic filter (`src/SchematicFilter.cs`) | Done and tested off-line on a real custom-map screenshot |
| Config switch + colour/threshold settings | Done |
| Hook into the game's map preview | **Not done**: needs the game's map-preview classes (found from the interop assemblies) |

The plugin loads and reads its config but does not change anything in the game yet.

## Turning it off

`BepInEx/config/carx.mapmanager.cfg` (created on first launch):

```ini
[General]
Enabled = true   # false = the game is left exactly as it is without this mod
```

The `[Schematic]` section holds the colours (`#RRGGBB`) and the thresholds that decide what counts as road, so the
look can be tuned without rebuilding.

## Trying the filter off-line

```bash
dotnet run --project tools/FilterPreview -- --selftest
dotnet run --project tools/FilterPreview -- in.rgba out.rgba   # raw file: int32 width, int32 height, RGBA bytes
```

## Build

```bash
dotnet build -c Release                                   # DLL in bin/Release/net6.0
dotnet build -c Release -p:DeployToGame=true              # also copies it to BepInEx/plugins/MapManager
```

## Sharing code with the compatibility layer

Anything game-wide that another mod could also use (reading pixels back from a texture, safe Harmony patching,
reporting errors to the status line) belongs in
[Dro2-compatibility-layer](https://github.com/david568303/Dro2-compatibility-layer) and is called from here.
Map-specific code (this filter, the map-preview hook) stays in this repo.
