using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace MapManager.Features;

/// <summary>
/// One self-contained thing Map Manager does. Each feature owns its config section (with its own Enabled switch)
/// and installs its own hooks, so features can be added, switched off or fail independently.
/// </summary>
internal interface IFeature
{
    /// <summary>Also the name of the feature's config section.</summary>
    string Name { get; }

    void BindConfig(ConfigFile config);

    bool IsEnabled { get; }

    /// <summary>Installs the feature's hooks. Only called when the feature is enabled. Returns false if it could not.</summary>
    bool Install(Harmony harmony, ManualLogSource log);
}
