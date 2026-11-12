using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // AzuCraftyBoxes 1.8.28 keeps each material's chest supply until its count version moves, but that version moves on
    // every rebuild of its list of chests in reach, a quarter second apart, and on every chest refresh Progression Fix makes
    // when the character moves, which a standing character's millimetre settling triggers on every frame that asks. So the
    // build menu, each recolour, the crafting panel and each placement count the same chests again. The kept supply now
    // stays while what it counts is the same: the same chests in reach in the same order, none of them changed (the change
    // event AzuCraftyBoxes listens to, a save, or a craft), the same station, exclusion rules and world level. Every change
    // to a chest's contents in the game and the pack's mods goes through the inventory's change event or a chest save, and a
    // change in which chests may be used changes the list. A reloaded rules file counts afresh too.
    internal static class CraftyBoxesCounts
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Kept, Cleared;
        private static int generation, countedGeneration = -1, countedWorldLevel;
        private static bool counting;
        private static object countedRules;
        private static string countedStation;
        private static readonly List<Container> chests = new List<Container>();
        private static readonly ConditionalWeakTable<Inventory, object> watched = new ConditionalWeakTable<Inventory, object>();
        private static readonly object watching = new object();
        private static readonly Action changed = Changed;
        private static IList all, nearby;
        private static IDictionary any, pullable;
        private static AccessTools.FieldRef<object, Container> chest;
        private static Type vanilla;
        private static AccessTools.FieldRef<int> vanillaCount, allCount;
        private static AccessTools.FieldRef<Dictionary<string, Dictionary<string, List<string>>>> rules;
        private static AccessTools.FieldRef<string> station;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
            vanilla = assembly.GetType("AzuCraftyBoxes.IContainers.VanillaContainer");
            var main = assembly.GetType("AzuCraftyBoxes.AzuCraftyBoxesPlugin");
            var cache = assembly.GetType("AzuCraftyBoxes.Patches.CacheCurrentCraftingStationPrefabName");
            var memo = boxes == null ? null : AccessTools.Method(boxes, "MemoUsable");
            var search = boxes == null ? null : AccessTools.Method(boxes, "GetNearbyContainers");
            var groups = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Util.Functions.YamlUtils"), "ParseGroups");
            Field(boxes, "_cachedAll"); Field(boxes, "_scratchNearby"); Field(boxes, "_talliesAny"); Field(boxes, "_talliesPullable");
            if (memo == null || memo.ReturnType != typeof(bool) || memo.GetParameters().Length != 1 || search == null || !search.IsGenericMethodDefinition || groups == null || vanilla == null ||
                Field(vanilla, "<_container>P")?.FieldType != typeof(Container) || Field(boxes, "_vanillaCount")?.FieldType != typeof(int) ||
                Field(boxes, "_cachedAllCount")?.FieldType != typeof(int) || Field(main, "yamlData")?.FieldType != typeof(Dictionary<string, Dictionary<string, List<string>>>) || Field(cache, "CachedStationName")?.FieldType != typeof(string))
                throw new InvalidOperationException("AzuCraftyBoxes' chest supply memo changed.");
            all = (IList)Field(boxes, "_cachedAll").GetValue(null);
            nearby = (IList)Field(boxes, "_scratchNearby").GetValue(null);
            any = (IDictionary)Field(boxes, "_talliesAny").GetValue(null);
            pullable = (IDictionary)Field(boxes, "_talliesPullable").GetValue(null);
            chest = AccessTools.FieldRefAccess<Container>(vanilla, "<_container>P");
            vanillaCount = AccessTools.StaticFieldRefAccess<int>(Field(boxes, "_vanillaCount"));
            allCount = AccessTools.StaticFieldRefAccess<int>(Field(boxes, "_cachedAllCount"));
            station = AccessTools.StaticFieldRefAccess<string>(Field(cache, "CachedStationName"));
            rules = AccessTools.StaticFieldRefAccess<Dictionary<string, Dictionary<string, List<string>>>>(Field(main, "yamlData"));
            harmony.Patch(memo, prefix: new HarmonyMethod(typeof(CraftyBoxesCounts), nameof(Usable)));
            harmony.Patch(search.MakeGenericMethod(typeof(Component)), postfix: new HarmonyMethod(typeof(CraftyBoxesCounts), nameof(Searched)));
            harmony.Patch(AccessTools.Method(typeof(Container), "Save"), postfix: new HarmonyMethod(typeof(CraftyBoxesCounts), nameof(Changed)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "DoCrafting"), prefix: new HarmonyMethod(typeof(CraftyBoxesCounts), nameof(Changed)));
            // A reloaded rules file replaces the rules, then fills the item groups they name in place.
            harmony.Patch(groups, postfix: new HarmonyMethod(typeof(CraftyBoxesCounts), nameof(Changed)));
            log.LogInfo("AzuCraftyBoxes keeps its chest counts until the chests in reach or their contents change.");
        }

        private static FieldInfo Field(Type type, string name) => type == null ? null : AccessTools.Field(type, name);

        private static void Changed() => generation++;

        // After every search: a list holding other chests, or the same chests in another order, counts afresh. Its chests'
        // inventories report their changes from then on.
        private static void Searched()
        {
            int count = vanillaCount();
            bool same = count == chests.Count && count <= nearby.Count;
            for (int index = 0; same && index < count; index++)
            {
                var entry = nearby[index];
                same = entry != null && entry.GetType() == vanilla && ReferenceEquals(chest(entry), chests[index]);
            }
            if (same) return;
            generation++;
            chests.Clear();
            for (int index = 0; index < count && index < nearby.Count; index++)
            {
                var entry = nearby[index];
                var container = entry != null && entry.GetType() == vanilla ? chest(entry) : null;
                chests.Add(container);
                var inventory = container ? container.GetInventory() : null;
                if (inventory != null && !watched.TryGetValue(inventory, out _))
                {
                    watched.Add(inventory, watching);
                    inventory.m_onChanged = (Action)Delegate.Combine(inventory.m_onChanged, changed);
                }
            }
        }

        // Replaces MemoUsable, which both tallies ask before using and filling the kept supply; it answers false only for a
        // list other than the one the last search built, as MemoUsable does.
        private static bool Usable(object containers, ref bool __result)
        {
            if (!Shortcut) { counting = false; return true; }
            __result = false;
            if (!ReferenceEquals(containers, all) || all.Count != allCount()) return false;
            if (!counting || generation != countedGeneration || Game.m_worldLevel != countedWorldLevel || !ReferenceEquals(rules(), countedRules) || station() != countedStation)
            {
                any.Clear();
                pullable.Clear();
                counting = true;
                countedGeneration = generation;
                countedWorldLevel = Game.m_worldLevel;
                countedRules = rules();
                countedStation = station();
                Cleared++;
            }
            else Kept++;
            __result = true;
            return false;
        }
    }
}
