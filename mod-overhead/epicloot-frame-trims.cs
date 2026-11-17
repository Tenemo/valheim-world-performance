using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Three small things Epic Loot 0.15.6 repeats every frame: its map asks whether its pin queue has anything through a
    // general enumeration, which makes an enumerator object, and now reads the queue's count; its ability bar lowercases
    // each slot's key setting anew, and now keeps the lowercased text of the setting it last saw; and each player's armour
    // tint reads its saved set by name, which only hashes the name, and now passes the hash.
    internal static class EpicLootFrameTrims
    {
        private static readonly int tint = "el-tint".GetStableHashCode();
        private static readonly string[] keys = new string[4], lowered = new string[4];
        private static int nextKey;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var minimap = assembly.GetType("EpicLoot.Adventure.MinimapController");
            var controller = assembly.GetType("EpicLoot.Abilities.AbilityController");
            var armor = assembly.GetType("EpicLoot.LegendarySystem.SetArmorTint");
            harmony.Patch(minimap == null ? null : AccessTools.Method(minimap, "Update"), transpiler: new HarmonyMethod(typeof(EpicLootFrameTrims), nameof(QueueTranspiler)));
            harmony.Patch(controller == null ? null : AccessTools.Method(controller, "GetBindingKeycode"), transpiler: new HarmonyMethod(typeof(EpicLootFrameTrims), nameof(KeyTranspiler)));
            var tintTranspiler = new HarmonyMethod(typeof(EpicLootFrameTrims), nameof(TintTranspiler));
            harmony.Patch(armor == null ? null : AccessTools.Method(armor, "Refresh"), transpiler: tintTranspiler);
            harmony.Patch(armor == null ? null : AccessTools.Method(armor, "Publish"), transpiler: tintTranspiler);
            log.LogInfo("Epic Loot's map queue, ability keys and armour tint repeat less work each frame.");
        }

        // MinimapPinQueue.Any() becomes MinimapPinQueue.Count > 0.
        private static IEnumerable<CodeInstruction> QueueTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int any = code.FindIndex(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(Enumerable) && method.Name == nameof(Enumerable.Any) &&
                method.GetParameters().Length == 1);
            var queue = any < 1 ? null : code[any - 1].operand as FieldInfo;
            if (queue == null || code[any - 1].opcode != OpCodes.Ldsfld || !queue.FieldType.IsGenericType || queue.FieldType.GetGenericTypeDefinition() != typeof(Queue<>) ||
                code.FindLastIndex(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(Enumerable) && method.Name == nameof(Enumerable.Any)) != any)
                throw new InvalidOperationException("Epic Loot's map pin queue changed.");
            code[any].opcode = OpCodes.Callvirt;
            code[any].operand = AccessTools.PropertyGetter(queue.FieldType, "Count");
            code.InsertRange(any + 1, new[] { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Cgt) });
            return code;
        }

        private static IEnumerable<CodeInstruction> KeyTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(string), nameof(string.ToLowerInvariant))),
                AccessTools.Method(typeof(EpicLootFrameTrims), nameof(Lowered)), 1, "Epic Loot's ability keys changed.");

        // The lowercased text of the same setting text is the same, so it is kept for the last few texts.
        internal static string Lowered(string key)
        {
            if (key == null) return key.ToLowerInvariant();
            for (int index = 0; index < keys.Length; index++)
                if (ReferenceEquals(keys[index], key)) return lowered[index];
            nextKey = (nextKey + 1) % keys.Length;
            keys[nextKey] = key;
            return lowered[nextKey] = key.ToLowerInvariant();
        }

        // zdo.GetString("el-tint") and zdo.Set("el-tint", text) become their hashed forms.
        private static IEnumerable<CodeInstruction> TintTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var overloads = new Dictionary<MethodInfo, MethodInfo>
            {
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.GetString), new[] { typeof(string), typeof(string) }), AccessTools.Method(typeof(ZDO), nameof(ZDO.GetString), new[] { typeof(int), typeof(string) }) },
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(string), typeof(string) }), AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(string) }) },
            };
            var code = instructions.ToList();
            int replaced = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (code[index].opcode != OpCodes.Ldstr || (string)code[index].operand != "el-tint") continue;
                int use = code.FindIndex(index + 1, instruction => instruction.operand is MethodBase);
                if (use < 0 || !(code[use].operand is MethodInfo method) || !overloads.ContainsKey(method)) throw new InvalidOperationException("Epic Loot's armour tint changed.");
                code[index].opcode = OpCodes.Ldc_I4;
                code[index].operand = tint;
                code[use].operand = overloads[method];
                replaced++;
            }
            if (replaced == 0) throw new InvalidOperationException("Epic Loot's armour tint changed.");
            return code;
        }
    }
}
