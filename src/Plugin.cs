using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace MapManager;

[BepInPlugin(PluginGuid, "Map Manager", PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "carx.mapmanager";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Logger = null!;

    /// <summary>Master switch. When false the mod must leave the game exactly as it is without it.</summary>
    internal static ConfigEntry<bool> Enabled = null!;

    private static ConfigEntry<string> _background = null!, _asphalt = null!, _concrete = null!, _asphaltEdge = null!, _concreteEdge = null!;
    private static ConfigEntry<float> _saturationMax = null!, _lightLuminance = null!, _minLuminance = null!;
    private static ConfigEntry<int> _smoothingPasses = null!, _edgeWidth = null!;

    public override void Load()
    {
        Logger = Log;
        var defaults = new SchematicStyle();

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch for the map preview fix. false = custom maps keep the game's normal (green) preview.");

        _background = Config.Bind("Schematic", "Background", defaults.Background.ToString(), "Colour of scenery (grass, dirt, water), as #RRGGBB.");
        _asphalt = Config.Bind("Schematic", "Asphalt", defaults.Asphalt.ToString(), "Fill of dark road surfaces, as #RRGGBB.");
        _concrete = Config.Bind("Schematic", "Concrete", defaults.Concrete.ToString(), "Fill of light surfaces and buildings, as #RRGGBB.");
        _asphaltEdge = Config.Bind("Schematic", "AsphaltEdge", defaults.AsphaltEdge.ToString(), "Outline of dark road surfaces, as #RRGGBB.");
        _concreteEdge = Config.Bind("Schematic", "ConcreteEdge", defaults.ConcreteEdge.ToString(), "Outline of light surfaces and buildings, as #RRGGBB.");
        _saturationMax = Config.Bind("Schematic", "SurfaceSaturationMax", defaults.SurfaceSaturationMax,
            new ConfigDescription("Pixels more colourful than this count as scenery, greyer ones as road/buildings.", new AcceptableValueRange<float>(0f, 1f)));
        _lightLuminance = Config.Bind("Schematic", "LightSurfaceLuminance", defaults.LightSurfaceLuminance,
            new ConfigDescription("Surfaces darker than this are asphalt, brighter ones concrete/buildings.", new AcceptableValueRange<float>(0f, 1f)));
        _minLuminance = Config.Bind("Schematic", "MinSurfaceLuminance", defaults.MinSurfaceLuminance,
            new ConfigDescription("Surfaces darker than this are treated as shadow, not road.", new AcceptableValueRange<float>(0f, 1f)));
        _smoothingPasses = Config.Bind("Schematic", "SmoothingPasses", defaults.SmoothingPasses,
            new ConfigDescription("Noise-removal passes. More = smoother but blobbier.", new AcceptableValueRange<int>(0, 6)));
        _edgeWidth = Config.Bind("Schematic", "EdgeWidth", defaults.EdgeWidth,
            new ConfigDescription("Outline thickness in pixels. 0 = automatic.", new AcceptableValueRange<int>(0, 8)));

        Log.LogInfo(Enabled.Value
            ? "MAPMANAGER_LOADED — preview fix is ON (set Enabled = false in carx.mapmanager.cfg to turn it off). The game hook is not wired up yet."
            : "MAPMANAGER_LOADED — preview fix is OFF (Enabled = false); the game is left untouched.");
    }

    /// <summary>The current schematic colours and thresholds from the config file.</summary>
    internal static SchematicStyle BuildStyle() => new()
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
        Logger.LogWarning($"MAPMANAGER_BAD_COLOUR — [{entry.Definition.Section}] {entry.Definition.Key} = '{entry.Value}' is not #RRGGBB; using {fallback}.");
        return fallback;
    }
}
