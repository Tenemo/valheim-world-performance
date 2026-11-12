using System.Diagnostics;
using HarmonyLib;

namespace WorldPerformance
{
    // A player's game sets up at most one zone every tenth of a second, also while a portal trip, a respawn or the login
    // keeps the loading screen up, so a base's area waits on that tick before any of its objects exist. Behind the loading
    // screen the game now sets up further zones in the same update until 40 ms have passed or none is missing; Progression
    // Fix's range applies to each of them as to the game's own. Once the screen is gone the native tick returns.
    [HarmonyPatch(typeof(ZoneSystem), "CreateLocalZones")]
    internal static class LoadingScreenZones
    {
        // The isolated test client sets false to compare the native tick.
        internal static bool Batching = true;
        internal static long Batched;
        private const double BudgetMilliseconds = 40;
        private static readonly System.Func<ZoneSystem, UnityEngine.Vector3, bool> create =
            AccessTools.MethodDelegate<System.Func<ZoneSystem, UnityEngine.Vector3, bool>>(AccessTools.Method(typeof(ZoneSystem), "CreateLocalZones"));
        private static bool batching;

        private static bool Covered()
        {
            var player = Player.m_localPlayer;
            return player == null || player.IsTeleporting();
        }

        // After Progression Fix's own postfix, which loads its farther zones through the same result.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZoneSystem __instance, UnityEngine.Vector3 refPoint, bool __result)
        {
            if (!Batching || batching || !__result || ZNet.instance == null || ZNet.instance.IsDedicated() || !Covered()) return;
            batching = true;
            try
            {
                long end = Stopwatch.GetTimestamp() + (long)(BudgetMilliseconds * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < end && create(__instance, refPoint)) Batched++;
            }
            finally { batching = false; }
        }
    }
}
