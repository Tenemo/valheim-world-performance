using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    [HarmonyPatch(typeof(Plant), "HaveGrowSpace")]
    internal static class PlantSpacingQuery
    {
        private static readonly AccessTools.FieldRef<Collider[]> Colliders = AccessTools.StaticFieldRefAccess<Collider[]>(AccessTools.Field(typeof(Plant), "s_colliders"));
        private static int retainedCount;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var overlap = AccessTools.Method(typeof(Physics), nameof(Physics.OverlapSphereNonAlloc),
                new[] { typeof(Vector3), typeof(float), typeof(Collider[]), typeof(int) });
            return Transpilers.CallInstead(instructions, instruction => instruction.Calls(overlap),
                AccessTools.Method(typeof(PlantSpacingQuery), nameof(CompleteOverlap)), 2, "Plant spacing overlap queries changed.");
        }

        private static int CompleteOverlap(Vector3 position, float radius, Collider[] colliders, int mask)
        {
            int count = Physics.OverlapSphereNonAlloc(position, radius, colliders, mask);
            while (count == colliders.Length)
            {
                colliders = new Collider[checked(colliders.Length * 2)];
                Colliders() = colliders;
                count = Physics.OverlapSphereNonAlloc(position, radius, colliders, mask);
            }
            if (count < retainedCount) Array.Clear(colliders, count, retainedCount - count);
            retainedCount = count;
            return count;
        }
    }
}
