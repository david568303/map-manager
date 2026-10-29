using BepInEx.Configuration;
using BepInEx.Logging;
using CarXModManager.Api;
using HarmonyLib;
using UnityEngine;

namespace MapManager.Features;

/// <summary>
/// Custom (mod.io) maps ship a top-down picture of the terrain, which the game shows as-is on the world-map preview:
/// all green. This feature redraws that picture in the schematic style of the game's own maps (road and building fills
/// with light outlines, scenery transparent).
///
/// Hooks: the picture reaches the preview through ModWorldMapTrack.Build(modId, meta, texture), which calls
/// ModWorldMapBuilder.Build(parent, meta, texture), which calls CreateMaterial(texture, tint, transparent) to put it on
/// a flat quad. All three are patched, because the compiler may have inlined one of them or the game may enter at a
/// lower level. Each prefix swaps the texture for a redrawn one before the game uses it (so the original never shows),
/// using the compatibility layer's <see cref="TextureReplacer"/>. In CreateMaterial the "transparent" flag is also set
/// for our texture, so the game uses its transparent material and the scenery shows what is behind the map instead of
/// a solid rectangle.
/// </summary>
internal sealed class MapPreviewFeature : IFeature
{
    private const string ModsNamespace = "CarX.Game.DR3Console.DR3.Scripts.States.Controllers.Mods.";
    private const string GameAssembly = "CarX.DR3.Assembly";

    private static MapPreviewFeature? _instance;
    private static TextureReplacer _replacer = null!;

    private ConfigEntry<bool> _enabled = null!, _transparentMaterial = null!;
    private ConfigEntry<float> _backgroundOpacity = null!, _saturationMax = null!, _lightLuminance = null!, _minLuminance = null!;
    private ConfigEntry<string> _background = null!, _asphalt = null!, _concrete = null!, _asphaltEdge = null!, _concreteEdge = null!;
    private ConfigEntry<int> _smoothing = null!, _edgeWidth = null!;

    public string Name => "MapPreview";
    public bool IsEnabled => _enabled.Value;

    public void BindConfig(ConfigFile config)
    {
        var d = new SchematicStyle();
        _enabled = config.Bind(Name, "Enabled", true,
            "Redraw custom maps' preview picture as a schematic (roads and surfaces outlined, scenery transparent). false = the game's normal green preview.");
        _backgroundOpacity = config.Bind(Name, "BackgroundOpacity", d.BackgroundOpacity,
            new ConfigDescription("0 = scenery fully transparent (like the game's own maps); 1 = scenery drawn as a solid Background colour.", new AcceptableValueRange<float>(0f, 1f)));
        _transparentMaterial = config.Bind(Name, "UseTransparentMaterial", true,
            "Ask the game for its transparent map material so transparent scenery really is see-through. Turn off if the map looks wrong; then use BackgroundOpacity = 1.");
        _background = config.Bind(Name, "Background", d.Background.ToString(), "Colour of scenery when BackgroundOpacity is above 0, as #RRGGBB.");
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
        _smoothing = config.Bind(Name, "Smoothing", d.SmoothingPasses,
            new ConfigDescription("How round the edges get. 0 = raw, 1 = default, higher = smoother but thin roads start to break up.", new AcceptableValueRange<int>(0, 4)));
        _edgeWidth = config.Bind(Name, "EdgeWidth", d.EdgeWidth,
            new ConfigDescription("Outline thickness in pixels. 0 = automatic.", new AcceptableValueRange<int>(0, 8)));
    }

    public bool Install(Harmony harmony, ManualLogSource log)
    {
        _instance = this;
        _replacer = new TextureReplacer(log, "MAPMANAGER_PREVIEW", (rgba, width, height) => SchematicFilter.Apply(rgba, width, height, BuildStyle()));
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

    private static void TrackBuildPrefix(ref Texture2D __2) => Swap(ref __2);
    private static void BuilderBuildPrefix(ref Texture2D __2) => Swap(ref __2);

    private static void CreateMaterialPrefix(ref Texture2D __0, ref bool __2)
    {
        Swap(ref __0);
        MapPreviewFeature? feature = _instance;
        if (feature != null && feature._transparentMaterial.Value && feature._backgroundOpacity.Value < 1f && _replacer.IsOurs(__0))
            __2 = true; // the game's transparent material, so the transparent scenery shows through
    }

    private static void Swap(ref Texture2D texture)
    {
        try
        {
            if (_instance != null) texture = _replacer.Replace(texture);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"MAPMANAGER_PREVIEW_FAILED — {exception}");
        }
    }

    private SchematicStyle BuildStyle() => new()
    {
        Background = Colour(_background, new SchematicStyle().Background),
        Asphalt = Colour(_asphalt, new SchematicStyle().Asphalt),
        Concrete = Colour(_concrete, new SchematicStyle().Concrete),
        AsphaltEdge = Colour(_asphaltEdge, new SchematicStyle().AsphaltEdge),
        ConcreteEdge = Colour(_concreteEdge, new SchematicStyle().ConcreteEdge),
        BackgroundOpacity = _backgroundOpacity.Value,
        SurfaceSaturationMax = _saturationMax.Value,
        LightSurfaceLuminance = _lightLuminance.Value,
        MinSurfaceLuminance = _minLuminance.Value,
        SmoothingPasses = _smoothing.Value,
        EdgeWidth = _edgeWidth.Value,
    };

    private static Rgb Colour(ConfigEntry<string> entry, Rgb fallback)
    {
        if (Rgb.TryParse(entry.Value, out Rgb colour)) return colour;
        Plugin.Logger.LogWarning($"MAPMANAGER_BAD_COLOUR — [{entry.Definition.Section}] {entry.Definition.Key} = '{entry.Value}' is not #RRGGBB; using {fallback}.");
        return fallback;
    }
}
