using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // Valheim plans a plant's next growth check 10 seconds after each check, but Plant.SUpdate checked the plant only until
    // that time instead of from it. Its slow update loop reaches 100 objects a frame and usually comes back within a few
    // seconds, so every plant was checked on every pass, and a gap over 10 seconds between two checks, such as a pass slowed
    // by a frame-rate drop with many objects loaded, ended the plant's checks: it never grew, and its hover text kept showing
    // it ready, until its area reloaded. Each plant is now checked once its time comes, every 10 seconds, however late the
    // loop reaches it.
    [HarmonyPatch(typeof(Plant), nameof(Plant.SUpdate))]
    internal static class PlantGrowthChecks
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var nextCheck = AccessTools.Field(typeof(Plant), "m_updateTime");
            int index = code.FindIndex(x => x.LoadsField(nextCheck));
            // Valheim 1.0.15: if (time > m_updateTime) return; m_updateTime = time + 10f;
            if (index < 2 || code.FindLastIndex(x => x.LoadsField(nextCheck)) != index || index + 1 >= code.Count ||
                !code[index - 2].IsLdarg(1) || !code[index - 1].IsLdarg(0) ||
                (code[index + 1].opcode != OpCodes.Ble_Un && code[index + 1].opcode != OpCodes.Ble_Un_S))
                throw new InvalidOperationException("Valheim's plant growth checks changed; expected one check time comparison.");
            // if (time < m_updateTime) return;
            code[index + 1].opcode = code[index + 1].opcode == OpCodes.Ble_Un ? OpCodes.Bge_Un : OpCodes.Bge_Un_S;
            return code;
        }
    }

    // Plant.Start plans a newly loaded plant's first check 10 seconds ahead, a time the loop used to ignore. Keep checking it
    // on the loop's first pass, so the plant shows its current look and health, such as why it cannot grow, as soon as its
    // area loads. Growing still waits 10 seconds after loading, as in the game.
    [HarmonyPatch(typeof(Plant), "Start")]
    internal static class FirstPlantGrowthCheck
    {
        private static readonly AccessTools.FieldRef<Plant, float> NextCheck = AccessTools.FieldRefAccess<Plant, float>("m_updateTime");

        private static void Postfix(Plant __instance) => NextCheck(__instance) = 0f;
    }
}
