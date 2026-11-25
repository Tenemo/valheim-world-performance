using System;
using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace WorldPerformance
{
    // MultiUserChest keeps an inventory's list of pending moves after the last one is answered, and then copies the whole
    // inventory every frame its grid is drawn to preview no moves. An empty list previews nothing without the copy.
    internal static class ChestMovePreview
    {
        // The shipped feature; the isolated client switches it off and on for checks and paired measurements.
        internal static bool Lean = true;
        internal static long Skipped;
        private static Func<Inventory, IList> pending;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.MultiUserChest, out var plugin)) return;
            var preview = plugin.Instance.GetType().Assembly.GetType("MultiUserChest.InventoryPreview", true);
            var table = AccessTools.Field(preview, "PackageChanges")?.GetValue(null);
            var changes = AccessTools.DeclaredMethod(preview, "GetChanges");
            var type = table?.GetType();
            if (type == null || !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ConditionalWeakTable<,>) || type.GetGenericArguments()[0] != typeof(Inventory) ||
                !typeof(IList).IsAssignableFrom(type.GetGenericArguments()[1]) || changes == null || changes.ReturnType != typeof(bool) ||
                changes.GetParameters().Select(parameter => parameter.ParameterType.IsByRef).SequenceEqual(new[] { false, true }) == false)
                throw new InvalidOperationException("MultiUserChest's move previews changed.");
            pending = (Func<Inventory, IList>)AccessTools.Method(typeof(ChestMovePreview), nameof(Lookup)).MakeGenericMethod(type.GetGenericArguments()[1]).Invoke(null, new[] { table });
            harmony.Patch(changes, prefix: new HarmonyMethod(typeof(ChestMovePreview), nameof(Nothing)));
        }

        private static Func<Inventory, IList> Lookup<T>(object table) where T : class, IList
        {
            var changes = (ConditionalWeakTable<Inventory, T>)table;
            return inventory => changes.TryGetValue(inventory, out var list) ? list : null;
        }

        // GetChanges previews the moves in the list on a copy and answers whether any changed it; with none it answers no.
        private static bool Nothing(Inventory inventory, ref bool __result)
        {
            if (!Lean || inventory == null || pending(inventory)?.Count != 0) return true;
            Skipped++;
            __result = false;
            return false;
        }
    }
}
