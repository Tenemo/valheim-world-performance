using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace WorldPerformance
{
    // A game rechecks the support of each piece it owns about once a second. Valheim reuses its last result only for a
    // piece resting on stronger pieces, so a piece touching the ground repeats a full physics overlap every time, though
    // touching the ground always gives full support. A piece the game's own check found on the ground now keeps its full
    // support without the check until the game clears its support cache, as damage, repair, a piece placed or destroyed
    // against it and a rebuild of its zone's terrain do, or until any terrain is rebuilt, loaded or unloaded.
    [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
    internal static class GroundSupport
    {
        private sealed class State
        {
            internal int terrain = -1;
            internal bool grounded;
        }

        private static readonly ConditionalWeakTable<WearNTear, State> States = new ConditionalWeakTable<WearNTear, State>();
        private static readonly AccessTools.FieldRef<WearNTear, float> Support = AccessTools.FieldRefAccess<WearNTear, float>("m_support");
        private static readonly AccessTools.FieldRef<WearNTear, bool> ClearsNeighbours = AccessTools.FieldRefAccess<WearNTear, bool>("m_clearCachedSupport");
        private static readonly Func<WearNTear, float> MaxSupport = AccessTools.MethodDelegate<Func<WearNTear, float>>(AccessTools.Method(typeof(WearNTear), "GetMaxSupport"));
        private static int terrainVersion;
        // Set while the game's check runs, when its overlap finds terrain.
        private static bool touchedTerrain;

        private static bool Prefix(WearNTear __instance)
        {
            if (States.TryGetValue(__instance, out var state) && state.grounded && state.terrain == terrainVersion && !ClearsNeighbours(__instance))
                return false;
            touchedTerrain = false;
            return true;
        }

        private static void Postfix(WearNTear __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            var state = States.GetOrCreateValue(__instance);
            state.terrain = terrainVersion;
            state.grounded = touchedTerrain && Support(__instance) >= MaxSupport(__instance);
        }

        // The check marks terrain among its overlap's colliders in a local flag; this records the same moment.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var layer = AccessTools.Field(typeof(WearNTear), "s_terrainLayer");
            var reads = Enumerable.Range(0, code.Count).Where(index => code[index].LoadsField(layer)).ToList();
            int set = reads.Count == 1 ? code.FindIndex(reads[0], instruction => instruction.opcode == OpCodes.Ldc_I4_1) : -1;
            if (set < 0 || set + 1 >= code.Count || !code[set + 1].IsStloc())
                throw new InvalidOperationException("The game's ground support check changed.");
            code.Insert(set + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GroundSupport), nameof(Touched))));
            return code;
        }

        private static void Touched() => touchedTerrain = true;

        [HarmonyPatch(typeof(WearNTear), "ClearCachedSupport")]
        private static class Cleared
        {
            private static void Postfix(WearNTear __instance)
            {
                if (States.TryGetValue(__instance, out var state)) state.terrain = -1;
            }
        }

        [HarmonyPatch]
        private static class TerrainChanged
        {
            private static IEnumerable<MethodBase> TargetMethods() => new[] {
                AccessTools.Method(typeof(Heightmap), nameof(Heightmap.Regenerate)),
                AccessTools.Method(typeof(Heightmap), "OnEnable"),
                AccessTools.Method(typeof(Heightmap), "OnDisable") };

            private static void Prefix() => terrainVersion++;
        }
    }
}
