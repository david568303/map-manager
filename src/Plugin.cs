using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MapManager.Features;

namespace MapManager;

/// <summary>
/// Map Manager: tools for working with custom maps. Each thing it does is a feature (see <see cref="IFeature"/>)
/// with its own config section and Enabled switch; the switch under [General] turns all of them off at once.
/// Game-wide helpers (safe patching, texture access, main-thread work) come from the compatibility layer.
/// </summary>
[BepInPlugin(PluginGuid, "Map Manager", PluginVersion)]
[BepInDependency("carx.modmanager", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "carx.mapmanager";
    public const string PluginVersion = "0.1.0";

    /// <summary>Short commit hash of the build ("local" for a build made outside CI). Versions are not bumped for dev builds.</summary>
    public static readonly string BuildId = ReadBuildId();

    private static string ReadBuildId()
    {
        string? info = typeof(Plugin).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        int plus = info?.IndexOf('+') ?? -1;
        return info != null && plus >= 0 && info.Length > plus + 1
            ? info.Substring(plus + 1, Math.Min(7, info.Length - plus - 1))
            : "local";
    }

    internal static ManualLogSource Logger = null!;

    /// <summary>Everything Map Manager can do. Add new features here.</summary>
    private static readonly IFeature[] Features = { new MapPreviewFeature() };

    public override void Load()
    {
        Logger = Log;
        ConfigEntry<bool> enabled = Config.Bind("General", "Enabled", true,
            "Master switch. false = Map Manager installs nothing and the game behaves exactly as without it.");
        foreach (IFeature feature in Features) feature.BindConfig(Config);

        if (!enabled.Value)
        {
            Log.LogInfo($"MAPMANAGER_LOADED — dev build {BuildId}; OFF ([General] Enabled = false), the game is left untouched.");
            return;
        }

        var harmony = new Harmony(PluginGuid);
        var report = new List<string>();
        foreach (IFeature feature in Features)
        {
            if (!feature.IsEnabled) { report.Add($"{feature.Name}: off"); continue; }
            bool installed = feature.Install(harmony, Log);
            report.Add($"{feature.Name}: {(installed ? "on" : "FAILED to install")}");
        }
        Log.LogInfo($"MAPMANAGER_LOADED — dev build {BuildId}; {string.Join(", ", report)}.");
    }
}
