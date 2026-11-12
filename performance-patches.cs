using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace WorldPerformance
{
    // Each optional integration owns only its hooks and deferred subscriptions. A failed integration removes its hooks
    // and reverses its changes without affecting the other integrations or any of their owners.
    internal sealed class PerformancePatches
    {
        private readonly Harmony harmony;
        private readonly List<Action> patches = new List<Action>(), changes = new List<Action>(), undo = new List<Action>();
        internal readonly List<Action> Cleanup = new List<Action>();
        private int appliedChanges;

        internal PerformancePatches(string name) => harmony = new Harmony(Plugin.Id + "." + name);

        internal void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (original == null) throw new MissingMethodException("Performance integration target was not found.");
            foreach (var handler in new[] { prefix, postfix, transpiler, finalizer })
                if (handler != null && handler.method == null) throw new MissingMethodException("Performance integration handler was not found.");
            patches.Add(() => harmony.Patch(original, prefix, postfix, transpiler, finalizer, null));
        }

        internal void Defer(Action apply, Action reverse) { changes.Add(apply); undo.Add(reverse); }

        internal void Apply()
        {
            foreach (var patch in patches) patch();
            foreach (var change in changes) { appliedChanges++; change(); }
            foreach (var cleanup in Cleanup) cleanup();
        }

        internal void Stop()
        {
            harmony.UnpatchSelf();
            for (int index = appliedChanges - 1; index >= 0; index--) undo[index]();
            appliedChanges = 0;
        }
    }

    internal static class Transpilers
    {
        internal static List<CodeInstruction> CallInstead(IEnumerable<CodeInstruction> instructions, Func<CodeInstruction, bool> match,
            MethodInfo replacement, int expected, string changed)
        {
            var code = instructions.ToList();
            var matches = code.Where(match).ToList();
            if (matches.Count != expected) throw new InvalidOperationException(changed);
            foreach (var instruction in matches)
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = replacement;
            }
            return code;
        }
    }
}
