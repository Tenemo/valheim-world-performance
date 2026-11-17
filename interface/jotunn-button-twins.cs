using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Jotunn 2.30.2 answers every button read in the game, about a hundred a frame, by reading the button and, when it is not
    // pressed, its controller twin "Joy!" plus the name, which only buttons Jotunn adds for other mods have; the pack has
    // none. Each read built the twin's name anew and read the game's button table for it. The twin is now read only when the
    // game's button table holds it, and its name is made once. A missing button reads as not pressed; the patches the pack's
    // mods put on that read only clear their own pending flags, which the button's own read has already cleared, so any other
    // patch there turns this off and fails mod-compatibility.
    internal static class JotunnButtonTwins
    {
        internal static long Read, Skipped;
        private static readonly Dictionary<string, string> twins = new Dictionary<string, string>();
        private static readonly AccessTools.FieldRef<ZInput, Dictionary<string, ZInput.ButtonDef>> buttons = AccessTools.FieldRefAccess<ZInput, Dictionary<string, ZInput.ButtonDef>>("m_buttons");
        private static Func<string, bool> down, held, up;
        private static MethodInfo state;
        // 0 until the first read looks at the button table read's patches, then 1 when twins are skipped, -1 when not.
        private static int ready;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.Jotunn, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var manager = assembly.GetType("Jotunn.Managers.InputManager");
            var patches = assembly.GetType("Jotunn.Managers.InputManager+Patches");
            Func<string, bool> Original(string name)
            {
                var method = patches == null ? null : AccessTools.Method(patches, name, new[] { typeof(string) });
                return method == null ? null : AccessTools.MethodDelegate<Func<string, bool>>(method);
            }
            down = Original("ZInput_GetButtonDown_Original");
            held = Original("ZInput_GetButton_Original");
            up = Original("ZInput_GetButtonUp_Original");
            state = AccessTools.Method(typeof(ZInput), "TryGetButtonState");
            if (manager == null || down == null || held == null || up == null || state == null) throw new InvalidOperationException("Jotunn's button reads changed.");
            harmony.Patch(AccessTools.Method(manager, "ZInput_GetButtonDown"), transpiler: new HarmonyMethod(typeof(JotunnButtonTwins), nameof(DownTranspiler)));
            harmony.Patch(AccessTools.Method(manager, "ZInput_GetButton"), transpiler: new HarmonyMethod(typeof(JotunnButtonTwins), nameof(HeldTranspiler)));
            harmony.Patch(AccessTools.Method(manager, "ZInput_GetButtonUp"), transpiler: new HarmonyMethod(typeof(JotunnButtonTwins), nameof(UpTranspiler)));
            log.LogInfo("Jotunn reads a button's controller twin only when the game has one.");
        }

        private static IEnumerable<CodeInstruction> DownTranspiler(IEnumerable<CodeInstruction> instructions) => Twin(instructions, nameof(TwinDown));

        private static IEnumerable<CodeInstruction> HeldTranspiler(IEnumerable<CodeInstruction> instructions) => Twin(instructions, nameof(TwinHeld));

        private static IEnumerable<CodeInstruction> UpTranspiler(IEnumerable<CodeInstruction> instructions) => Twin(instructions, nameof(TwinUp));

        // ldstr "Joy!"; ldarg.1; call string.Concat; call <original> becomes ldarg.1; call Twin<kind>.
        private static List<CodeInstruction> Twin(IEnumerable<CodeInstruction> instructions, string helper)
        {
            var concat = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
            var code = instructions.ToList();
            int start = code.FindIndex(instruction => instruction.opcode == OpCodes.Ldstr && (string)instruction.operand == "Joy!");
            if (start < 0 || start + 3 >= code.Count || code[start + 1].opcode != OpCodes.Ldarg_1 || !code[start + 2].Calls(concat) ||
                !(code[start + 3].operand is MethodInfo original && original.Name.EndsWith("_Original")) ||
                code[start + 1].labels.Count > 0 || code[start + 2].labels.Count > 0 || code[start + 3].labels.Count > 0)
                throw new InvalidOperationException("Jotunn's button reads changed.");
            code[start].opcode = OpCodes.Ldarg_1;
            code[start].operand = null;
            code[start + 1].opcode = OpCodes.Call;
            code[start + 1].operand = AccessTools.Method(typeof(JotunnButtonTwins), helper);
            code[start + 2].opcode = code[start + 3].opcode = OpCodes.Nop;
            code[start + 2].operand = code[start + 3].operand = null;
            return code;
        }

        internal static bool TwinDown(string name) => Pressed(name, down);

        internal static bool TwinHeld(string name) => Pressed(name, held);

        internal static bool TwinUp(string name) => Pressed(name, up);

        private static bool Pressed(string name, Func<string, bool> original)
        {
            if (name == null) return original("Joy!" + name);
            if (!twins.TryGetValue(name, out string twin))
            {
                if (twins.Count >= 1024) twins.Clear();
                twins[name] = twin = "Joy!" + name;
            }
            var input = ZInput.instance;
            if (input != null && !buttons(input).ContainsKey(twin) && Ready()) { Skipped++; return false; }
            Read++;
            return original(twin);
        }

        private static bool Ready()
        {
            if (ready != 0) return ready > 0;
            string problem = PatchSets.Unknown(state, "EquipmentAndQuickSlots.PreventSimilarHotkeys+ZInput_TryGetButtonState_PreventSimilarHotkeys.Postfix");
            ready = problem == null ? 1 : -1;
            if (problem != null) Plugin.Fallback("JotunnButtonTwins unavailable: " + problem);
            return ready > 0;
        }
    }
}
