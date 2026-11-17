using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace WorldPerformance
{
    // MultiUserChest 0.6.2 keeps a lock record for every inventory it ever sees, including each creature's equipment as
    // it spawns, in a dictionary nothing removes from, so those inventories live for the rest of the session. Each record
    // now lives exactly as long as its inventory. The records that already exist move over unchanged, so a live lock
    // stays in force until its own request releases it.
    internal static class InventoryLocks
    {
        private static readonly ConditionalWeakTable<Inventory, object> Records = new ConditionalWeakTable<Inventory, object>();
        private static Func<object> create;
        private static readonly ConditionalWeakTable<Inventory, object>.CreateValueCallback NewRecord = _ => create();

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return;
            var block = plugin.Instance.GetType().Assembly.GetType("MultiUserChest.InventoryBlock");
            var get = block == null ? null : AccessTools.Method(block, "Get", new[] { typeof(Inventory) });
            var original = block == null ? null : AccessTools.Field(block, "Inventories")?.GetValue(null) as IDictionary;
            var constructor = block == null ? null : AccessTools.Constructor(block, Type.EmptyTypes);
            if (get?.ReturnType != block || original == null || constructor == null) throw new InvalidOperationException("MultiUserChest's inventory locks changed.");
            create = () => constructor.Invoke(null);
            harmony.Patch(get, prefix: new HarmonyMethod(typeof(InventoryLocks), nameof(Get)));
            var moved = new List<DictionaryEntry>();
            harmony.Defer(() =>
            {
                foreach (DictionaryEntry entry in original) moved.Add(entry);
                foreach (var entry in moved) Records.Add((Inventory)entry.Key, entry.Value);
                original.Clear();
            }, () =>
            {
                foreach (var entry in moved) { Records.Remove((Inventory)entry.Key); original[entry.Key] = entry.Value; }
            });
            harmony.Cleanup.Add(moved.Clear);
        }

        private static bool Get(Inventory inventory, ref object __result)
        {
            __result = Records.GetValue(inventory, NewRecord);
            return false;
        }
    }
}
