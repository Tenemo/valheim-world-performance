using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game rebuilds grass one patch a frame as the player moves, but every waiting patch in one frame after the grass
    // is reset or cleared: when a terrain record loads with more than one new edit, as when another player's edits arrive
    // together, all grass on its 64 m tile resets, and leaving a dungeon or changing the grass setting clears it all. A
    // patch takes about 0.75 ms, so a tile's 47 patches took 28-40 ms of one frame in the hidden client. Those rebuilds now
    // go nearest first within 2 ms a frame, and a reset patch keeps its old grass until its turn. Each patch seeds its own
    // random state, so the grass comes out the same.
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class PacedGrassRebuilds
    {
        // The isolated test client sets false to compare the native rebuild.
        internal static bool Pacing = true;
        private const double BudgetMilliseconds = 2.0;
        private static readonly AccessTools.FieldRef<ClutterSystem, bool> forceRebuild = AccessTools.FieldRefAccess<ClutterSystem, bool>("m_forceRebuild");
        private static readonly System.Type patchType = AccessTools.Inner(typeof(ClutterSystem), "PatchData");
        // The patch at a place, or null, read from the game's typed table without boxing its key.
        private static readonly System.Func<ClutterSystem, Vector2Int, object> patchAt = (System.Func<ClutterSystem, Vector2Int, object>)System.Delegate.CreateDelegate(
            typeof(System.Func<ClutterSystem, Vector2Int, object>), AccessTools.Method(typeof(PacedGrassRebuilds), nameof(PatchAt)).MakeGenericMethod(patchType));
        private static readonly AccessTools.FieldRef<object, bool> resetOf = AccessTools.FieldRefAccess<bool>(patchType, "m_reset");
        private static readonly AccessTools.FieldRef<object, float> timerOf = AccessTools.FieldRefAccess<float>(patchType, "m_timer");
        private static int frame = -1;
        private static long frameStart;
        private static bool unfinished;

        [HarmonyPatch("GeneratePatch")]
        [HarmonyPrefix]
        private static bool Generate(ClutterSystem __instance, Vector3 camPos, Vector2Int p, bool rebuildAll)
        {
            if (!Pacing || !rebuildAll || __instance.m_menuHack || Utils.DistanceXZ(__instance.GetVegPatchCenter(p), camPos) > __instance.m_distance) return true;
            object patch = patchAt(__instance, p);
            // A kept patch only has its timer refreshed.
            if (patch != null && !resetOf(patch)) return true;
            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                frameStart = Stopwatch.GetTimestamp();
                return true;
            }
            if ((Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency < BudgetMilliseconds) return true;
            // A waiting reset patch stays drawn; its timer would otherwise remove it.
            if (patch != null) timerOf(patch) = 0f;
            unfinished = true;
            return false;
        }

        private static object PatchAt<T>(ClutterSystem clutter, Vector2Int place) where T : class =>
            Patches<T>.Of(clutter).TryGetValue(place, out var patch) ? patch : null;

        private static class Patches<T> where T : class
        {
            internal static readonly AccessTools.FieldRef<ClutterSystem, Dictionary<Vector2Int, T>> Of =
                AccessTools.FieldRefAccess<ClutterSystem, Dictionary<Vector2Int, T>>("m_patches");
        }

        [HarmonyPatch("LateUpdate")]
        [HarmonyPostfix]
        private static void Continue(ClutterSystem __instance)
        {
            if (!unfinished) return;
            unfinished = false;
            forceRebuild(__instance) = true;
        }
    }
}
