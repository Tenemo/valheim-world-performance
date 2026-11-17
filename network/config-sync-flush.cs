using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Conditional Config Sync 1.0.11 tries to send each synchronized mod's pending setting changes every frame. On a player's
    // game, with nothing pending, each try still read the game's network instance by reflection twice and cleared its empty
    // pending lists, copying one of them into a new array. With no pending state, setting, value or package the try sends
    // and clears nothing, so it now returns at once.
    internal static class ConfigSyncFlush
    {
        internal static long Skipped;
        private static Func<object, bool> idle;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ConditionalConfigSync, out _)) return;
            // The plugin's library; other mods may carry copies of their own, which keep their own tries.
            var library = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "ConditionalConfigSync");
            var sync = library?.GetType("ConditionalConfigSync.ConditionalConfigSync");
            FieldInfo Field(string name) => sync == null ? null : AccessTools.Field(sync, name);
            var slot = Field("pendingStateSendSlot");
            var lists = new[] { Field("pendingConfigBroadcasts"), Field("pendingCustomValueBroadcasts"), Field("pendingSequencedCustomValuePackages") };
            var flush = sync == null ? null : AccessTools.Method(sync, "FlushPendingBroadcastsIfIdle");
            if (slot == null || flush == null || Array.Exists(lists, list => list == null || AccessTools.PropertyGetter(list.FieldType, "Count") == null))
                throw new InvalidOperationException("Conditional Config Sync's pending broadcasts changed.");
            // pendingStateSendSlot == null && every pending collection's Count == 0.
            idle = Bridges.Make<Func<object, bool>>(typeof(bool), new[] { typeof(object) }, il =>
            {
                var busy = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, sync); il.Emit(OpCodes.Ldfld, slot); il.Emit(OpCodes.Brtrue, busy);
                foreach (var list in lists)
                {
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, sync); il.Emit(OpCodes.Ldfld, list);
                    il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(list.FieldType, "Count")); il.Emit(OpCodes.Brtrue, busy);
                }
                il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
                il.MarkLabel(busy);
                il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            });
            harmony.Patch(flush, prefix: new HarmonyMethod(typeof(ConfigSyncFlush), nameof(Prefix)));
            log.LogInfo("Conditional Config Sync's idle sends return at once.");
        }

        private static bool Prefix(object __instance)
        {
            if (!idle(__instance)) return true;
            Skipped++;
            return false;
        }
    }
}
