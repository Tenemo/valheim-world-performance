using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

namespace WorldPerformance
{
    // Cover.GetCoverForPoint casts 17 rays for how covered a point is and a 100 m sphere upwards for whether it is under a
    // roof. Beehives (and Progression Fix's bird nests through them), windmills and wisp spawners use only the cover, so
    // theirs now skip the roof cast; the cover comes from the same rays. A fire checking for wet weather needs the cover
    // only in strong wind and the roof only in rain when the wind has not already put it out, so each is cast only then.
    // Physics queries change nothing, so only the answers the callers read remain. A patch of another mod on the cover or
    // roof checks, or on the fire's check, turns this off and fails mod-compatibility. Both are read when the player spawns,
    // so a conflict shows even where no hive, windmill, wisp spawner or fire is loaded.
    internal static class CoverChecks
    {
        private static readonly AccessTools.FieldRef<Vector3[]> rays = AccessTools.StaticFieldRefAccess<Vector3[]>(AccessTools.Field(typeof(Cover), "m_coverRays"));
        private static readonly AccessTools.FieldRef<NativeArray<RaycastCommand>> commands = AccessTools.StaticFieldRefAccess<NativeArray<RaycastCommand>>(AccessTools.Field(typeof(Cover), "m_commands"));
        private static readonly AccessTools.FieldRef<NativeArray<RaycastHit>> results = AccessTools.StaticFieldRefAccess<NativeArray<RaycastHit>>(AccessTools.Field(typeof(Cover), "m_results"));
        private static readonly AccessTools.FieldRef<QueryParameters> query = AccessTools.StaticFieldRefAccess<QueryParameters>(AccessTools.Field(typeof(Cover), "m_queryParameters"));
        private static readonly Action setup = AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(Cover), "Setup"));
        private static readonly AccessTools.FieldRef<Fireplace, bool> wet = AccessTools.FieldRefAccess<Fireplace, bool>("m_wet");
        private static readonly MethodInfo cover = AccessTools.Method(typeof(Cover), nameof(Cover.GetCoverForPoint));
        private static readonly MethodInfo roof = AccessTools.Method(typeof(Cover), nameof(Cover.IsUnderRoof));
        private static MethodInfo checkWet;
        internal static long Casts, Skipped;
        // 0 until the first call reads the patches, then 1 when the shortcuts run, -1 when the game's own checks run.
        private static int ready, fireReady;

        internal static void Initialize(PerformancePatches harmony)
        {
            var transpiler = new HarmonyMethod(typeof(CoverChecks), nameof(Transpiler));
            foreach (var caller in new[] { AccessTools.Method(typeof(Beehive), "HaveFreeSpace"), AccessTools.Method(typeof(Windmill), "CheckCover"), AccessTools.Method(typeof(WispSpawner), "HaveFreeSpace") })
                harmony.Patch(caller, transpiler: transpiler);
            checkWet = AccessTools.Method(typeof(Fireplace), "CheckWet");
            harmony.Patch(checkWet, prefix: new HarmonyMethod(typeof(CoverChecks), nameof(CheckWet)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnSpawned)), postfix: new HarmonyMethod(typeof(CoverChecks), nameof(Spawned)));
        }

        private static void Spawned(Player __instance)
        {
            if (__instance == Player.m_localPlayer && Ready()) FireReady();
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(cover), AccessTools.Method(typeof(CoverChecks), nameof(CoverOnly)), 1, "A cover check changed.");

        private static bool FireReady()
        {
            if (fireReady == 0)
            {
                string problem = PatchSets.Unknown(checkWet);
                fireReady = problem == null ? 1 : -1;
                if (problem != null) Plugin.Fallback("CoverChecks unavailable: " + problem);
            }
            return fireReady > 0;
        }

        private static bool Ready()
        {
            if (ready != 0) return ready > 0;
            string problem = PatchSets.Unknown(cover) ?? PatchSets.Unknown(roof);
            ready = problem == null ? 1 : -1;
            if (problem != null) Plugin.Fallback("CoverChecks unavailable: " + problem);
            return ready > 0;
        }

        // Cover.GetCoverForPoint for a caller that discards underRoof.
        internal static void CoverOnly(Vector3 startPos, out float coverPercentage, out bool underRoof, float minDistance)
        {
            if (!Ready()) { Cover.GetCoverForPoint(startPos, out coverPercentage, out underRoof, minDistance); return; }
            Skipped++;
            underRoof = false;
            coverPercentage = Rays(startPos, minDistance);
        }

        // The cover part of Cover.GetCoverForPoint: the rays as one batch in the game's own command and result arrays, a ray
        // covering where it hit at a distance.
        private static float Rays(Vector3 startPos, float minDistance)
        {
            setup();
            var directions = rays();
            var batch = commands();
            var hits = results();
            for (int index = 0; index < directions.Length; index++)
            {
                Vector3 direction = directions[index];
                batch[index] = new RaycastCommand(startPos + direction * minDistance, direction, query(), 30f - minDistance);
            }
            RaycastCommand.ScheduleBatch(batch, hits, 1).Complete();
            float covered = 0f;
            for (int index = 0; index < hits.Length; index++)
                if (hits[index].distance > 0f) covered += 1f;
            return covered / directions.Length;
        }

        // Fireplace.CheckWet, casting the cover rays only in strong wind and the roof sphere only when rain decides.
        private static bool CheckWet(Fireplace __instance)
        {
            if (!Ready() || !FireReady()) return true;
            wet(__instance) = false;
            bool windy = EnvMan.instance.GetWindIntensity() >= 0.8f;
            bool raining = EnvMan.IsWet();
            if (!windy && !raining) return false;
            Vector3 point = __instance.transform.position + Vector3.up * __instance.m_coverCheckOffset;
            Casts++;
            if (windy && Rays(point, 0.5f) < 0.7f) wet(__instance) = true;
            else if (raining && !Cover.IsUnderRoof(point)) wet(__instance) = true;
            return false;
        }
    }
}
