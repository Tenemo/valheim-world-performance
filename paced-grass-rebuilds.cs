using System.Collections;
using System.Diagnostics;
using System.Reflection;
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
        private static readonly FieldInfo patchesField = AccessTools.Field(typeof(ClutterSystem), "m_patches");
        private static readonly System.Type patchType = AccessTools.Inner(typeof(ClutterSystem), "PatchData");
        private static readonly FieldInfo resetField = AccessTools.Field(patchType, "m_reset");
        private static readonly FieldInfo timerField = AccessTools.Field(patchType, "m_timer");
        private static int frame = -1;
        private static long frameStart;
        private static bool unfinished;

        [HarmonyPatch("GeneratePatch")]
        [HarmonyPrefix]
        private static bool Generate(ClutterSystem __instance, Vector3 camPos, Vector2Int p, bool rebuildAll)
        {
            if (!Pacing || !rebuildAll || __instance.m_menuHack || Utils.DistanceXZ(__instance.GetVegPatchCenter(p), camPos) > __instance.m_distance) return true;
            object patch = ((IDictionary)patchesField.GetValue(__instance))[p];
            // A kept patch only has its timer refreshed.
            if (patch != null && !(bool)resetField.GetValue(patch)) return true;
            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                frameStart = Stopwatch.GetTimestamp();
                return true;
            }
            if ((Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency < BudgetMilliseconds) return true;
            // A waiting reset patch stays drawn; its timer would otherwise remove it.
            if (patch != null) timerField.SetValue(patch, 0f);
            unfinished = true;
            return false;
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
