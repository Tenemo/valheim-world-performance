using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // When Epic Loot 0.15.6's material conversion finishes its countdown, ConvertUI.DoMainAction first cancels the countdown,
    // and ConvertUI.Cancel shows the mode again, which rebuilds the recipe list from the bag and every chest in reach. The
    // action then pays, gives the products and rebuilds the list again in the same frame, so the first list is never drawn.
    // That first rebuild is skipped; the cancel still resets the mode's labels, filter and selection as before.
    internal static class EpicLootConvertRefresh
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Skipped;
        private static bool cancelling;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var convert = plugin.Instance.GetType().Assembly.GetType("EpicLoot_UnityLib.ConvertUI");
            var self = typeof(EpicLootConvertRefresh);
            var action = convert == null ? null : AccessTools.DeclaredMethod(convert, "DoMainAction");
            harmony.Patch(action, transpiler: new HarmonyMethod(self, nameof(Transpiler)), finalizer: new HarmonyMethod(self, nameof(Done)));
            harmony.Patch(convert == null ? null : AccessTools.DeclaredMethod(convert, "RefreshAvailableItems", Type.EmptyTypes), prefix: new HarmonyMethod(self, nameof(Refresh)));
            log.LogInfo("Epic Loot's material conversion rebuilds its recipe list once when it finishes.");
        }

        // this.Cancel() becomes Quiet(); this.Cancel(); Loud(), around the action's only cancel.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var cancels = code.Select((instruction, index) => (instruction, index))
                .Where(entry => entry.instruction.operand is MethodInfo method && method.Name == "Cancel" && method.GetParameters().Length == 0).ToList();
            if (cancels.Count != 1 || cancels[0].index < 1 || code[cancels[0].index - 1].opcode != OpCodes.Ldarg_0 ||
                code.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == "RefreshAvailableItems") < cancels[0].index)
                throw new InvalidOperationException("Epic Loot's material conversion changed.");
            int receiver = cancels[0].index - 1;
            var quiet = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EpicLootConvertRefresh), nameof(Quiet)));
            quiet.MoveLabelsFrom(code[receiver]);
            code.Insert(cancels[0].index + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EpicLootConvertRefresh), nameof(Loud))));
            code.Insert(receiver, quiet);
            return code;
        }

        private static void Quiet() => cancelling = Shortcut;
        private static void Loud() => cancelling = false;
        private static void Done() => cancelling = false;

        private static bool Refresh()
        {
            if (!cancelling) return true;
            Skipped++;
            return false;
        }
    }
}
