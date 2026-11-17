using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // ComfortMe 1.3.1 finds the rested status icon after every status effect update, each frame, and reads the hud's list of
    // status entries through reflection to do it. It now reads the same list directly.
    internal static class ComfortStatusList
    {
        private static readonly FieldInfo entriesField = AccessTools.Field(typeof(Hud), "m_statusEffects");
        private static readonly AccessTools.FieldRef<Hud, List<RectTransform>> entries = AccessTools.FieldRefAccess<Hud, List<RectTransform>>("m_statusEffects");

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var links = plugin.Instance.GetType().Assembly.GetType("ComfortMe.ComfortLinks");
            harmony.Patch(links == null ? null : AccessTools.Method(links, "BindRestedIcons"), transpiler: new HarmonyMethod(typeof(ComfortStatusList), nameof(Transpiler)));
            log.LogInfo("ComfortMe reads the hud's status entries without reflection.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(FieldInfo), nameof(FieldInfo.GetValue), new[] { typeof(object) })),
                AccessTools.Method(typeof(ComfortStatusList), nameof(Read)), 1, "ComfortMe's rested icon search changed.");

        // FieldInfo.GetValue(target), read directly for the hud's status entries.
        internal static object Read(FieldInfo field, object target) =>
            field == entriesField && target is Hud hud ? entries(hud) : field.GetValue(target);
    }
}
