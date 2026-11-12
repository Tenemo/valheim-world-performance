using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Better Beehives 1.3.1 asks the engine for every pollination target each time a hive's honey level rises, and the engine
    // answers by looking through every component in the game, so a base with many hives freezes for a moment every
    // 20 minutes. Targets now join a list as they wake, and the search reads that list with the same distance test.
    internal static class HivePollinationSearch
    {
        private static readonly List<MonoBehaviour> Targets = new List<MonoBehaviour>();

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.BetterBeehives, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var target = assembly.GetType("BetterBeehives.Scripts.BeePollinate");
            var helper = assembly.GetType("BetterBeehives.Data.Helper");
            var woke = target == null ? null : AccessTools.Method(target, "Awake");
            var search = helper == null ? null : AccessTools.Method(helper, "FindAllPollinatesInRadius");
            if (woke == null || search == null) throw new InvalidOperationException("Better Beehives' pollination search changed.");
            harmony.Patch(woke, postfix: new HarmonyMethod(typeof(HivePollinationSearch), nameof(Woke)));
            harmony.Patch(search, prefix: new HarmonyMethod(typeof(HivePollinationSearch), nameof(Search)));
            log.LogInfo("Better Beehives finds its pollination targets without searching every object.");
        }

        private static void Woke(MonoBehaviour __instance) => Targets.Add(__instance);

        // As the engine's search does, this skips inactive objects; a destroyed target leaves the list here.
        private static bool Search(Beehive beehive, float radius, ref List<GameObject> __result)
        {
            Targets.RemoveAll(target => !target);
            __result = new List<GameObject>();
            float limit = radius * radius;
            var center = beehive.transform.position;
            foreach (var target in Targets)
            {
                var position = target.transform.position;
                float x = position.x - center.x, z = position.z - center.z;
                if (target.gameObject.activeInHierarchy && x * x + z * z <= limit) __result.Add(target.gameObject);
            }
            return false;
        }
    }
}
