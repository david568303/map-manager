using BepInEx.Configuration;
using BepInEx.Logging;
using CarXModManager.Api;
using HarmonyLib;
using UnityEngine;

namespace MapManager.Features;

/// <summary>
/// Custom (mod.io) maps ship a top-down picture of the terrain, which the game shows as-is on the world-map preview:
/// all green. This feature redraws that picture in the schematic style of the game's own maps (dark background, grey
/// road fills, light outlines).
///
/// Hook: ModWorldMapTrack.Build(modId, ModMinimapMeta, Texture2D) receives the picture. A prefix copies its pixels
/// out, the filter runs on a background thread, and the result is written back into the same texture on the main
/// thread, so whatever already shows the texture updates.
/// </summary>
internal sealed class MapPreviewFeature : IFeature
{
    private const string TrackType = "CarX.Game.DR3Console.DR3.Scripts.States.Controllers.Mods.ModWorldMapTrack";
    private const string TrackAssembly = "CarX.DR3.Assembly";

    private static MapPreviewFeature? _instance;
    private static ManualLogSource _log = null!;
    private static readonly HashSet<int> Processed = new();

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<string> _background = null!, _asphalt = null!, _concrete = null!, _asphaltEdge = null!, _concreteEdge = null!;
    private ConfigEntry<float> _saturationMax = null!, _lightLuminance = null!, _minLuminance = null!;
    private ConfigEntry<int> _smoothingPasses = null!, _edgeWidth = null!;

    public string Name => "MapPreview";
    public bool IsEnabled => _enabled.Value;

    public void BindConfig(ConfigFile config)
    {
        var d = new SchematicStyle();
        _enabled = config.Bind(Name, "Enabled", true,
            "Redraw custom maps' preview picture as a schematic (roads and surfaces outlined on a dark background). false = the game's normal green preview.");
        _background = config.Bind(Name, "Background", d.Background.ToString(), "Colour of scenery (grass, dirt, water), as #RRGGBB.");
        _asphalt = config.Bind(Name, "Asphalt", d.Asphalt.ToString(), "Fill of dark road surfaces, as #RRGGBB.");
        _concrete = config.Bind(Name, "Concrete", d.Concrete.ToString(), "Fill of light surfaces and buildings, as #RRGGBB.");
        _asphaltEdge = config.Bind(Name, "AsphaltEdge", d.AsphaltEdge.ToString(), "Outline of dark road surfaces, as #RRGGBB.");
        _concreteEdge = config.Bind(Name, "ConcreteEdge", d.ConcreteEdge.ToString(), "Outline of light surfaces and buildings, as #RRGGBB.");
        _saturationMax = config.Bind(Name, "SurfaceSaturationMax", d.SurfaceSaturationMax,
            new ConfigDescription("Pixels more colourful than this count as scenery, greyer ones as road/buildings.", new AcceptableValueRange<float>(0f, 1f)));
        _lightLuminance = config.Bind(Name, "LightSurfaceLuminance", d.LightSurfaceLuminance,
            new ConfigDescription("Surfaces darker than this are asphalt, brighter ones concrete/buildings.", new AcceptableValueRange<float>(0f, 1f)));
        _minLuminance = config.Bind(Name, "MinSurfaceLuminance", d.MinSurfaceLuminance,
            new ConfigDescription("Surfaces darker than this are treated as shadow, not road.", new AcceptableValueRange<float>(0f, 1f)));
        _smoothingPasses = config.Bind(Name, "SmoothingPasses", d.SmoothingPasses,
            new ConfigDescription("Noise-removal passes. More = smoother but blobbier.", new AcceptableValueRange<int>(0, 6)));
        _edgeWidth = config.Bind(Name, "EdgeWidth", d.EdgeWidth,
            new ConfigDescription("Outline thickness in pixels. 0 = automatic.", new AcceptableValueRange<int>(0, 8)));
    }

    public bool Install(Harmony harmony, ManualLogSource log)
    {
        _instance = this;
        _log = log;
        var prefix = typeof(MapPreviewFeature).GetMethod(nameof(BuildPrefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return SafePatch.TryPatch(harmony, log, TrackType, TrackAssembly, "Build",
            parameters => parameters.Length == 3 && parameters[0].ParameterType == typeof(string) && parameters[2].ParameterType == typeof(Texture2D),
            prefix, null);
    }

    // Harmony passes the call's arguments by position: __2 is the third one, the Texture2D.
    private static void BuildPrefix(Texture2D __2)
    {
        try { _instance?.Process(__2); }
        catch (Exception exception) { _log.LogError($"MAPMANAGER_PREVIEW_FAILED — {exception}"); }
    }

    private void Process(Texture2D texture)
    {
        if (texture == null) return;
        int id = texture.GetInstanceID();
        lock (Processed)
            if (!Processed.Add(id)) return; // already redrawn; the game may build the same texture again

        if (!TextureTools.TryRead(texture, out byte[] rgba, out int width, out int height, out string? problem))
        {
            lock (Processed) Processed.Remove(id);
            _log.LogError($"MAPMANAGER_PREVIEW_UNREADABLE — {problem}");
            return;
        }

        _log.LogInfo($"MAPMANAGER_PREVIEW_START — redrawing a {width}x{height} custom-map picture.");
        SchematicStyle style = BuildStyle();
        var started = System.Diagnostics.Stopwatch.StartNew();
        Task.Run(() => SchematicFilter.Apply(rgba, width, height, style)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                _log.LogError($"MAPMANAGER_PREVIEW_FAILED — filter: {task.Exception?.GetBaseException()}");
                return;
            }
            byte[] result = task.Result;
            long filterMs = started.ElapsedMilliseconds;
            MainThread.Post("MapManager preview write-back", () =>
            {
                if (TextureTools.TryWriteInPlace(texture, result, width, height, out string? writeProblem))
                    _log.LogInfo($"MAPMANAGER_PREVIEW_DONE — filter {filterMs} ms, applied after {started.ElapsedMilliseconds} ms.");
                else
                    _log.LogError($"MAPMANAGER_PREVIEW_FAILED — could not write the result back: {writeProblem}");
            });
        });
    }

    private SchematicStyle BuildStyle() => new()
    {
        Background = Colour(_background, new SchematicStyle().Background),
        Asphalt = Colour(_asphalt, new SchematicStyle().Asphalt),
        Concrete = Colour(_concrete, new SchematicStyle().Concrete),
        AsphaltEdge = Colour(_asphaltEdge, new SchematicStyle().AsphaltEdge),
        ConcreteEdge = Colour(_concreteEdge, new SchematicStyle().ConcreteEdge),
        SurfaceSaturationMax = _saturationMax.Value,
        LightSurfaceLuminance = _lightLuminance.Value,
        MinSurfaceLuminance = _minLuminance.Value,
        SmoothingPasses = _smoothingPasses.Value,
        EdgeWidth = _edgeWidth.Value,
    };

    private static Rgb Colour(ConfigEntry<string> entry, Rgb fallback)
    {
        if (Rgb.TryParse(entry.Value, out Rgb colour)) return colour;
        _log.LogWarning($"MAPMANAGER_BAD_COLOUR — [{entry.Definition.Section}] {entry.Definition.Key} = '{entry.Value}' is not #RRGGBB; using {fallback}.");
        return fallback;
    }
}
