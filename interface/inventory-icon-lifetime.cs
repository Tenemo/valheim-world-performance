using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // BetterUI's permanent setting owns every icon it ever subscribed. One setting listener instead updates the live
    // icons only when the size changes, including inactive icons, without retaining destroyed grids or polling in play.
    internal static class InventoryIconLifetime
    {
        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            var updater = plugin.Instance.GetType().Assembly.GetType("BetterUI.Patches.ItemIconUpdater", true);
            var setting = AccessTools.Field(plugin.Instance.GetType(), "iconScaleSize")?.GetValue(null) as ConfigEntry<float>;
            var apply = AccessTools.Method(updater, "IconScaleSize_SettingChanged");
            if (setting == null || apply == null) throw new InvalidOperationException("BetterUI's inventory icon size setting changed.");
            harmony.Patch(AccessTools.Method(updater, "Setup"), transpiler: new HarmonyMethod(typeof(InventoryIconLifetime), nameof(SubscribeWithIcon)));
            void Refresh(object sender, EventArgs arguments)
            {
                foreach (var icon in Resources.FindObjectsOfTypeAll(updater)) if (icon) apply.Invoke(icon, null);
            }
            harmony.Defer(() => setting.SettingChanged += Refresh, () => setting.SettingChanged -= Refresh);
        }

        private static IEnumerable<CodeInstruction> SubscribeWithIcon(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var subscribe = AccessTools.Method(typeof(ConfigEntry<float>), "add_SettingChanged");
            int index = code.FindIndex(instruction => instruction.Calls(subscribe));
            if (index < 0 || code.Count(instruction => instruction.Calls(subscribe)) != 1)
                throw new InvalidOperationException("BetterUI's inventory icon subscription changed.");
            code[index].opcode = OpCodes.Call;
            code[index].operand = AccessTools.Method(typeof(InventoryIconLifetime), nameof(SkipSubscription));
            return code;
        }

        private static void SkipSubscription(ConfigEntry<float> setting, EventHandler handler) { }
    }
}
