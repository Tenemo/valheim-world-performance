using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

    internal static class PatchSets
    {
        // Null when every patch on the method is this mod's own or one of the known patch methods, named as
        // "Namespace.Type+Nested.Method"; otherwise the others. A shortcut that leaves out or reorders a call relies on what
        // the call's patches do, so it reads them once all mods have loaded.
        internal static string Unknown(MethodBase method, params string[] known)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return null;
            var others = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators)
                .Select(patch => patch.PatchMethod)
                .Where(patch => patch.DeclaringType?.Namespace != typeof(PatchSets).Namespace && !known.Contains(patch.DeclaringType?.FullName + "." + patch.Name))
                .Select(patch => patch.DeclaringType?.FullName + "." + patch.Name).ToList();
            return others.Count == 0 ? null : method.DeclaringType?.Name + "." + method.Name + " has other patches: " + string.Join(", ", others);
        }
    }

    internal static class Bridges
    {
        // A delegate with this mod's parameter types, for calling into other mods' types it cannot name.
        internal static T Make<T>(Type returns, Type[] parameters, Action<ILGenerator> body) where T : Delegate
        {
            var method = new DynamicMethod("WorldPerformance.Bridge", returns, parameters, typeof(Bridges), true);
            body(method.GetILGenerator());
            return (T)method.CreateDelegate(typeof(T));
        }
    }

    internal static class Transpilers
    {
        // The local variable a ldloc or stloc instruction reads or writes.
        internal static int Local(CodeInstruction instruction)
        {
            var opcode = instruction.opcode;
            if (opcode == System.Reflection.Emit.OpCodes.Ldloc_0 || opcode == System.Reflection.Emit.OpCodes.Stloc_0) return 0;
            if (opcode == System.Reflection.Emit.OpCodes.Ldloc_1 || opcode == System.Reflection.Emit.OpCodes.Stloc_1) return 1;
            if (opcode == System.Reflection.Emit.OpCodes.Ldloc_2 || opcode == System.Reflection.Emit.OpCodes.Stloc_2) return 2;
            if (opcode == System.Reflection.Emit.OpCodes.Ldloc_3 || opcode == System.Reflection.Emit.OpCodes.Stloc_3) return 3;
            switch (instruction.operand)
            {
                case LocalVariableInfo local: return local.LocalIndex;
                case byte index: return index;
                case short index: return index;
                case int index: return index;
                default: throw new InvalidOperationException("Not a local variable: " + instruction);
            }
        }

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
