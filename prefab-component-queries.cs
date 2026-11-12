using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    internal static class PrefabComponentQueries
    {
        // Unity already filters inactive descendants. Native setup repeats the parent walk for every component.
        internal static T[] ActiveChildren<T>(GameObject root) where T : Component =>
            root.activeSelf ? root.GetComponentsInChildren<T>() : Array.Empty<T>();

        internal static T[] EnabledChildren<T>(GameObject root) where T : Component
        {
            var components = ActiveChildren<T>(root);
            int count = 0;
            foreach (var component in components)
                if (component.gameObject != root) components[count++] = component;
            if (count != components.Length) Array.Resize(ref components, count);
            return components;
        }

        [HarmonyPatch]
        private static class LocationAndRoom
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ZoneSystem), "SpawnLocation");
                yield return AccessTools.Method(typeof(DungeonGenerator), "PlaceRoom",
                    new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) });
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                int replaced = 0;
                foreach (var instruction in code)
                    if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(Utils) &&
                        method.Name == nameof(Utils.GetEnabledComponentsInChildren) && method.IsGenericMethod)
                    {
                        instruction.operand = AccessTools.Method(typeof(PrefabComponentQueries), nameof(EnabledChildren)).MakeGenericMethod(method.GetGenericArguments());
                        replaced++;
                    }
                if (replaced != 3) throw new InvalidOperationException("The game's location or room component queries changed.");
                return code;
            }
        }

        [HarmonyPatch(typeof(RandomSpawn), nameof(RandomSpawn.Prepare))]
        private static class RandomSpawnPreparation
        {
            private static bool Prefix(RandomSpawn __instance, ref ZNetView ___m_nview, ref List<ZNetView> ___m_childNetViews)
            {
                ___m_nview = __instance.GetComponent<ZNetView>();
                ___m_childNetViews = new List<ZNetView>(ActiveChildren<ZNetView>(__instance.gameObject));
                return false;
            }
        }
    }
}
