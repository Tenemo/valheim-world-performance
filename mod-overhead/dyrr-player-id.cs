using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Dyrr 1.4.2 reads the playing character's id every frame through reflection, which also makes a new boxed number each
    // time. It now reads the same field directly.
    internal static class DyrrPlayerId
    {
        private static readonly AccessTools.FieldRef<PlayerProfile, long> id = AccessTools.FieldRefAccess<PlayerProfile, long>("m_playerID");

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.Dyrr, out var plugin)) return;
            var home = plugin.Instance.GetType().Assembly.GetType("Dyrr.Home");
            harmony.Patch(home == null ? null : AccessTools.Method(home, "IdOf", new[] { typeof(PlayerProfile) }), prefix: new HarmonyMethod(typeof(DyrrPlayerId), nameof(IdOf)));
            log.LogInfo("Dyrr reads the character's id without reflection.");
        }

        // Home.IdOf: 0 for no profile, otherwise PlayerProfile.m_playerID.
        private static bool IdOf(PlayerProfile profile, ref long __result)
        {
            __result = profile == null ? 0L : id(profile);
            return false;
        }
    }
}
