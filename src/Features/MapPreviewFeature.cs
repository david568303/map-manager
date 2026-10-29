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
/// Hooks: the picture reaches the preview through ModWorldMapTrack.Build(modId, meta, texture), which calls
/// ModWorldMapBuilder.Build(parent, meta, texture), which calls CreateMaterial(texture, colour, flag) to put it on a flat
/// quad. All three are patched with the same prefix, because the compiler may have inlined one of them or the game may
/// enter at a lower level; each texture is only handled once.
///
/// The game's texture has Read/Write disabled, so it cannot be changed in place. The prefix therefore makes a readable
/// copy on the GPU, hands the copy to the game instead of the original, copies its pixels out, runs the filter on a
/// background thread, and writes the result back into the copy on the main thread. The preview shows the normal green
/// picture first and switches to the schematic when the filter finishes.
/// </summary>
internal sealed class MapPreviewFeature : IFeature
{
    private const string ModsNamespace = "CarX.Game.DR3Console.DR3.Scripts.States.Controllers.Mods.";
    private const string GameAssembly = "CarX.DR3.Assembly";
    private const int MaxKeptCopies = 6;

    private static MapPreviewFeature? _instance;
    private static ManualLogSource _log = null!;

    private static readonly object Gate = new();
    /// <summary>Instance ids of textures that are ours or already redrawn; the game builds the same texture through several calls.</summary>
    private static readonly HashSet<int> Handled = new();
    /// <summary>The game's original texture id -> the readable copy we gave the game instead.</summary>
    private static readonly Dictionary<int, Texture2D> Replacements = new();
    /// <summary>Copies we created, oldest first, so they can be destroyed instead of leaking.</summary>
    private static readonly Queue<(int OriginalId, Texture2D Copy)> Copies = new();

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
        const System.Reflection.BindingFlags Hook = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        int installed = 0;

        // Harmony passes a call's arguments by position: __0 is the first, __2 the third.
        if (SafePatch.TryPatch(harmony, log, ModsNamespace + "ModWorldMapTrack", GameAssembly, "Build",
                p => p.Length == 3 && p[0].ParameterType == typeof(string) && p[2].ParameterType == typeof(Texture2D),
                typeof(MapPreviewFeature).GetMethod(nameof(TrackBuildPrefix), Hook), null)) installed++;

        if (SafePatch.TryPatch(harmony, log, ModsNamespace + "ModWorldMapBuilder", GameAssembly, "Build",
                p => p.Length == 3 && p[0].ParameterType != typeof(string) && p[2].ParameterType == typeof(Texture2D),
                typeof(MapPreviewFeature).GetMethod(nameof(BuilderBuildPrefix), Hook), null)) installed++;

        if (SafePatch.TryPatch(harmony, log, ModsNamespace + "ModWorldMapBuilder", GameAssembly, "CreateMaterial",
                p => p.Length == 3 && p[0].ParameterType == typeof(Texture2D),
                typeof(MapPreviewFeature).GetMethod(nameof(CreateMaterialPrefix), Hook), null)) installed++;

        return installed > 0;
    }

    private static void TrackBuildPrefix(ref Texture2D __2) => Handle("ModWorldMapTrack.Build", ref __2);
    private static void BuilderBuildPrefix(ref Texture2D __2) => Handle("ModWorldMapBuilder.Build", ref __2);
    private static void CreateMaterialPrefix(ref Texture2D __0) => Handle("ModWorldMapBuilder.CreateMaterial", ref __0);

    /// <summary>Replaces the texture the game is about to use with our readable, redrawn copy when needed.</summary>
    private static void Handle(string hook, ref Texture2D texture)
    {
        try
        {
            if (_instance != null) texture = _instance.Process(hook, texture);
        }
        catch (Exception exception)
        {
            _log.LogError($"MAPMANAGER_PREVIEW_FAILED — {hook}: {exception}");
        }
    }

    private Texture2D Process(string hook, Texture2D texture)
    {
        if (texture == null)
        {
            _log.LogInfo($"MAPMANAGER_HOOK — {hook} was called without a texture.");
            return texture!;
        }

        int id = texture.GetInstanceID();
        bool readable = texture.isReadable;
        lock (Gate)
        {
            if (Handled.Contains(id)) return texture;
            if (Replacements.TryGetValue(id, out Texture2D? existing)) return existing;
        }
        _log.LogInfo($"MAPMANAGER_HOOK — {hook}: texture '{texture.name}' {texture.width}x{texture.height}, format {texture.format}, readable {readable}.");

        Texture2D target = texture;
        if (!readable)
        {
            if (!TextureTools.TryCopyToReadable(texture, out Texture2D? copy, out string? copyProblem) || copy == null)
            {
                _log.LogError($"MAPMANAGER_PREVIEW_UNREADABLE — could not make a readable copy: {copyProblem}");
                return texture;
            }
            target = copy;
            Remember(id, copy);
            _log.LogInfo("MAPMANAGER_PREVIEW_COPY — the game's texture is not readable; working on a GPU copy and giving the game that instead.");
        }
        else
        {
            lock (Gate) Handled.Add(id);
        }

        if (!TextureTools.TryRead(target, out byte[] rgba, out int width, out int height, out string? problem))
        {
            _log.LogError($"MAPMANAGER_PREVIEW_UNREADABLE — {problem}");
            return target;
        }

        _log.LogInfo($"MAPMANAGER_PREVIEW_START — redrawing a {width}x{height} custom-map picture.");
        SchematicStyle style = BuildStyle();
        var started = System.Diagnostics.Stopwatch.StartNew();
        Texture2D redrawn = target;
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
                if (TextureTools.TryWriteInPlace(redrawn, result, width, height, out string? writeProblem))
                    _log.LogInfo($"MAPMANAGER_PREVIEW_DONE — filter {filterMs} ms, applied after {started.ElapsedMilliseconds} ms.");
                else
                    _log.LogError($"MAPMANAGER_PREVIEW_FAILED — could not write the result back: {writeProblem}");
            });
        });
        return target;
    }

    /// <summary>Records a copy we gave the game, and destroys the oldest ones so previews opened over and over don't leak.</summary>
    private static void Remember(int originalId, Texture2D copy)
    {
        var expired = new List<Texture2D>();
        lock (Gate)
        {
            Replacements[originalId] = copy;
            Handled.Add(copy.GetInstanceID());
            Copies.Enqueue((originalId, copy));
            while (Copies.Count > MaxKeptCopies)
            {
                var (oldId, oldCopy) = Copies.Dequeue();
                Replacements.Remove(oldId);
                Handled.Remove(oldCopy.GetInstanceID());
                expired.Add(oldCopy);
            }
        }
        foreach (Texture2D old in expired)
        {
            try { UnityEngine.Object.Destroy(old); }
            catch (Exception exception) { _log.LogDebug($"Could not destroy an old preview copy: {exception.Message}"); }
        }
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
