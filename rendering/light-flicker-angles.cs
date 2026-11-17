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
    // LightFlicker.CustomUpdate, which every flickering torch, fire and lantern light runs each frame (about 200 at a large
    // base), takes nine sines and cosines of five different angles of one flicker time. Each of the five is now taken once,
    // right after the flicker time is known, by the same instructions the game uses, and the nine uses read it: the same
    // floats, so the same brightness and the same light position.
    internal static class LightFlickerAngles
    {
        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            harmony.Patch(AccessTools.Method(typeof(LightFlicker), nameof(LightFlicker.CustomUpdate)), transpiler: new HarmonyMethod(typeof(LightFlickerAngles), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var sin = AccessTools.Method(typeof(MathF), nameof(MathF.Sin));
            var cos = AccessTools.Method(typeof(MathF), nameof(MathF.Cos));
            var speed = AccessTools.Field(typeof(LightFlicker), "m_flickerSpeed");
            var code = instructions.ToList();
            InvalidOperationException Changed() => new InvalidOperationException("LightFlicker's flicker changed.");
            // The flicker time: the local stored right after m_flickerOffset + time * m_flickerSpeed.
            int read = code.FindIndex(instruction => instruction.LoadsField(speed));
            if (read < 0 || read + 3 >= code.Count || code[read + 1].opcode != OpCodes.Mul || code[read + 2].opcode != OpCodes.Add || !code[read + 3].IsStloc() || code[read + 4].labels.Count > 0) throw Changed();
            int angle = Transpilers.Local(code[read + 3]);
            bool LoadsAngle(CodeInstruction instruction) => instruction.IsLdloc() && Transpilers.Local(instruction) == angle;
            // Each sine or cosine of the flicker time, alone or times a constant.
            var uses = new List<(int first, int call, MethodInfo function, float? factor)>();
            for (int index = 0; index < code.Count; index++)
            {
                if (!code[index].Calls(sin) && !code[index].Calls(cos)) continue;
                var function = (MethodInfo)code[index].operand;
                if (index >= 1 && LoadsAngle(code[index - 1])) uses.Add((index - 1, index, function, null));
                else if (index >= 3 && LoadsAngle(code[index - 3]) && code[index - 2].opcode == OpCodes.Ldc_R4 && code[index - 1].opcode == OpCodes.Mul)
                    uses.Add((index - 3, index, function, (float)code[index - 2].operand));
                else throw Changed();
            }
            var distinct = uses.Select(use => (use.function, use.factor)).Distinct().ToList();
            if (uses.Count != 9 || distinct.Count != 5) throw Changed();
            var locals = distinct.ToDictionary(key => key, key => generator.DeclareLocal(typeof(float)));
            var loadAngle = code[uses[0].first];
            var load = new CodeInstruction(loadAngle.opcode, loadAngle.operand);
            foreach (var use in uses)
            {
                for (int index = use.first + 1; index <= use.call; index++)
                {
                    if (code[index].labels.Count > 0 || code[index].blocks.Count > 0) throw Changed();
                    code[index].opcode = OpCodes.Nop;
                    code[index].operand = null;
                }
                code[use.first].opcode = OpCodes.Ldloc;
                code[use.first].operand = locals[(use.function, use.factor)];
            }
            var computed = new List<CodeInstruction>();
            foreach (var key in distinct)
            {
                computed.Add(new CodeInstruction(load.opcode, load.operand));
                if (key.factor.HasValue)
                {
                    computed.Add(new CodeInstruction(OpCodes.Ldc_R4, key.factor.Value));
                    computed.Add(new CodeInstruction(OpCodes.Mul));
                }
                computed.Add(new CodeInstruction(OpCodes.Call, key.function));
                computed.Add(new CodeInstruction(OpCodes.Stloc, locals[key]));
            }
            code.InsertRange(read + 4, computed);
            return code;
        }
    }
}
