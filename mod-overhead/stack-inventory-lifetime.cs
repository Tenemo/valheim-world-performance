using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace WorldPerformance
{
    // Item Stacks Item Weights remembers inventories it patched after a config reload. That membership must not own
    // unloaded chests and their items. Keep the mod's patching and reload rules, with weak inventory membership.
    internal static class StackInventoryLifetime
    {
        private static ConditionalWeakTable<Inventory, object> patched = new ConditionalWeakTable<Inventory, object>();
        private static readonly object marker = new object();
        private static bool patchedNull;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.ItemStacks, out var plugin)) return;
            var processing = plugin.Instance.GetType().Assembly.GetType("ItemStacksItemWeights.ItemProcessing", true);
            var original = AccessTools.Field(processing, "containerInventoryUpdated")?.GetValue(null) as List<Inventory>;
            if (original == null) throw new InvalidOperationException("Item Stacks Item Weights' inventory membership changed.");
            foreach (var method in new[] { AccessTools.Method(processing, "OnConfigFileChange"),
                AccessTools.Method(AccessTools.Inner(processing, "Player_Load_PatchItems"), "Postfix"),
                AccessTools.Method(AccessTools.Inner(processing, "InventoryGrid_UpdateInventory_PatchContainerItems"), "Postfix") })
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(StackInventoryLifetime), nameof(WeakMembership)));
            var moved = new List<Inventory>();
            harmony.Defer(() =>
            {
                moved.AddRange(original);
                foreach (var inventory in moved) Remember(original, inventory);
                original.Clear();
            }, () => { Reset(original); original.AddRange(moved); });
            harmony.Cleanup.Add(moved.Clear);
        }

        private static IEnumerable<CodeInstruction> WeakMembership(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            bool query = original.DeclaringType.Name == "InventoryGrid_UpdateInventory_PatchContainerItems";
            var code = instructions;
            if (query)
            {
                code = Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.Method(typeof(List<Inventory>), "Contains")),
                    AccessTools.Method(typeof(StackInventoryLifetime), nameof(Contains)), 1, "The inventory patch membership query changed.");
                return Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.Method(typeof(List<Inventory>), "Add")),
                    AccessTools.Method(typeof(StackInventoryLifetime), nameof(Remember)), 1, "The inventory patch membership insertion changed.");
            }
            return Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.Method(typeof(List<Inventory>), "Clear")),
                AccessTools.Method(typeof(StackInventoryLifetime), nameof(Reset)), 1, "The inventory patch membership reset changed.");
        }

        private static bool Contains(List<Inventory> unused, Inventory inventory) => inventory == null ? patchedNull : patched.TryGetValue(inventory, out _);
        private static void Remember(List<Inventory> unused, Inventory inventory)
        {
            if (inventory == null) patchedNull = true;
            else patched.GetValue(inventory, _ => marker);
        }
        private static void Reset(List<Inventory> original)
        {
            patched = new ConditionalWeakTable<Inventory, object>(); patchedNull = false;
            original.Clear();
        }
    }
}
