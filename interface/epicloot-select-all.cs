using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Epic Loot's Select all chooses a list's visible rows one by one, and each row's change has the enchanting table
    // panel rebuild its result and cost lists from the selection so far: about 150 ms a row with a late-game character's
    // Identify list, so selecting fifteen stacks took over two seconds in one frame. Every panel works those lists out
    // from the selection alone, so the rows now change together and the panel rebuilds once, from the final selection,
    // as the last of those rebuilds did. Lists that select one row at a time keep their own handling.
    internal static class EpicLootSelectAll
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Batched;
        private static object batching;
        private static bool changed;
        private static FieldInfo multiselect, selectedItemsChanged;
        private static Action<object> refreshSelectAll;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var list = plugin.Instance.GetType().Assembly.GetType("EpicLoot_UnityLib.MultiSelectItemList");
            var self = typeof(EpicLootSelectAll);
            var toggled = list == null ? null : AccessTools.DeclaredMethod(list, "OnSelectAllToggled");
            var rowChanged = list == null ? null : AccessTools.DeclaredMethod(list, "OnElementSelectionChanged");
            var refresh = list == null ? null : AccessTools.DeclaredMethod(list, "RefreshSelectAllToggle", Type.EmptyTypes);
            multiselect = list == null ? null : AccessTools.DeclaredField(list, "Multiselect");
            selectedItemsChanged = list == null ? null : AccessTools.DeclaredField(list, "OnSelectedItemsChanged");
            if (toggled == null || rowChanged == null || refresh == null || multiselect?.FieldType != typeof(bool) || selectedItemsChanged?.FieldType != typeof(Action))
                throw new InvalidOperationException("Epic Loot's selectable lists changed.");
            refreshSelectAll = target => refresh.Invoke(target, null);
            harmony.Patch(toggled, prefix: new HarmonyMethod(self, nameof(Begin)), postfix: new HarmonyMethod(self, nameof(End)), finalizer: new HarmonyMethod(self, nameof(Done)));
            harmony.Patch(rowChanged, prefix: new HarmonyMethod(self, nameof(RowChanged)));
            log.LogInfo("Epic Loot's Select all rebuilds the enchanting table's result lists once.");
        }

        private static void Begin(object __instance)
        {
            if (!Shortcut || batching != null || !(bool)multiselect.GetValue(__instance)) return;
            batching = __instance;
            changed = false;
        }

        // A multiselect list only passes each row's change on to the panel and refreshes the Select all toggle.
        private static bool RowChanged(object __instance)
        {
            if (batching != __instance) return true;
            changed = true;
            return false;
        }

        // The original has refreshed the toggle after the last row; the last row's change then passed on and refreshed it.
        private static void End(object __instance)
        {
            if (batching != __instance) return;
            batching = null;
            if (!changed) return;
            Batched++;
            ((Action)selectedItemsChanged.GetValue(__instance))?.Invoke();
            refreshSelectAll(__instance);
        }

        private static void Done(object __instance)
        {
            if (batching == __instance) batching = null;
        }
    }
}
