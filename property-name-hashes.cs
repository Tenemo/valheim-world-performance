using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // The health, stamina, adrenaline, eitr and stagger bars set their animators' "Visible" switch by name every frame, and
    // the grass sets the player's position for its shaders by name every frame; each name is hashed again every time. The
    // game's named calls only hash the name and pass it on, so the hashes are now computed once and passed on directly.
    internal static class PropertyNameHashes
    {
        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var bars = new HarmonyMethod(typeof(PropertyNameHashes), nameof(BarTranspiler));
            foreach (string bar in new[] { "UpdateStagger", "UpdateStamina", "UpdateAdrenaline", "UpdateEitr" })
                harmony.Patch(AccessTools.Method(typeof(Hud), bar), transpiler: bars);
            harmony.Patch(AccessTools.Method(typeof(ClutterSystem), "LateUpdate"), transpiler: new HarmonyMethod(typeof(PropertyNameHashes), nameof(GrassTranspiler)));
        }

        private static IEnumerable<CodeInstruction> BarTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Hashed(instructions, AccessTools.Method(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(string), typeof(bool) }),
                AccessTools.Method(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(int), typeof(bool) }), Animator.StringToHash);

        private static IEnumerable<CodeInstruction> GrassTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Hashed(instructions, AccessTools.Method(typeof(Shader), nameof(Shader.SetGlobalVector), new[] { typeof(string), typeof(Vector4) }),
                AccessTools.Method(typeof(Shader), nameof(Shader.SetGlobalVector), new[] { typeof(int), typeof(Vector4) }), Shader.PropertyToID);

        // Each call's name is the last string loaded before it; the calls between them, which make the value, take and give
        // no strings.
        private static List<CodeInstruction> Hashed(IEnumerable<CodeInstruction> instructions, MethodInfo named, MethodInfo hashed, Func<string, int> hash)
        {
            var code = instructions.ToList();
            int replaced = 0;
            bool Strings(MethodBase method) => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)) || (method as MethodInfo)?.ReturnType == typeof(string);
            for (int index = 0; index < code.Count; index++)
            {
                if (!code[index].Calls(named)) continue;
                int name = code.FindLastIndex(index, instruction => instruction.opcode == OpCodes.Ldstr);
                if (name < 0 || code.Skip(name + 1).Take(index - name - 1).Any(instruction => instruction.operand is MethodBase method && Strings(method)))
                    throw new InvalidOperationException("A hud bar's or the grass's named property changed.");
                code[name].opcode = OpCodes.Ldc_I4;
                code[name].operand = hash((string)code[name].operand);
                code[index].operand = hashed;
                replaced++;
            }
            if (replaced == 0) throw new InvalidOperationException("A hud bar's or the grass's named property changed.");
            return code;
        }
    }
}
