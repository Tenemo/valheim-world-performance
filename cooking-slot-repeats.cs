using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // NoSmokeStayLit 2.4.4 repeats CookingStation.UpdateVisual's refresh of every cooking slot after the game's own. A slot
    // whose refresh would change nothing is now passed over: its food model is already the one the slot holds, or there is
    // none for an empty slot, and its cooking, burnt and done effects and its sizzle are already on or off as the refresh
    // would set them. Every other slot is refreshed again as before. The game's own refresh is unchanged.
    internal static class CookingSlotRepeats
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Skipped, Refreshed;
        private const int Done = 1, Burnt = 2;
        private static readonly AccessTools.FieldRef<CookingStation, ParticleSystem[]> effects = AccessTools.FieldRefAccess<CookingStation, ParticleSystem[]>("m_ps");
        private static readonly AccessTools.FieldRef<CookingStation, AudioSource[]> sounds = AccessTools.FieldRefAccess<CookingStation, AudioSource[]>("m_as");

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.NoSmokeStayLit, out var plugin)) return;
            var patch = plugin.Instance.GetType().Assembly.GetType("NoSmokeStayLit.Patches.CookingStationPatch+CookingStationSmoke_Patch");
            harmony.Patch(patch == null ? null : AccessTools.Method(patch, "Postfix"), transpiler: new HarmonyMethod(typeof(CookingSlotRepeats), nameof(Transpiler)));
            log.LogInfo("NoSmokeStayLit's second cooking slot refresh passes over slots it would not change.");
        }

        // The refresh's arguments are loaded again for the check, and a true answer jumps past the refresh.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var refresh = AccessTools.Method(typeof(CookingStation), "SetSlotVisual");
            var code = instructions.ToList();
            var loads = new[] { OpCodes.Ldarg_0, OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldarg_1, OpCodes.Ldind_U1, OpCodes.Ldloc_3 };
            int call = code.FindIndex(instruction => instruction.Calls(refresh));
            if (call < loads.Length || call + 1 >= code.Count || code.FindLastIndex(instruction => instruction.Calls(refresh)) != call ||
                Enumerable.Range(0, loads.Length).Any(index => code[call - loads.Length + index].opcode != loads[index]))
                throw new InvalidOperationException("NoSmokeStayLit's cooking slot refresh changed.");
            var after = generator.DefineLabel();
            code[call + 1].labels.Add(after);
            int first = call - loads.Length;
            var check = code.GetRange(first, loads.Length).Select(instruction => new CodeInstruction(instruction.opcode, instruction.operand)).ToList();
            check.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CookingSlotRepeats), nameof(Unchanged))));
            check.Add(new CodeInstruction(OpCodes.Brtrue, after));
            check[0].labels.AddRange(code[first].labels);
            code[first].labels.Clear();
            code.InsertRange(first, check);
            return code;
        }

        // Whether CookingStation.SetSlotVisual(slot, item, fireLit, status), with World Performance's detaching of old models,
        // would leave the slot as it is.
        private static bool Unchanged(CookingStation station, int slot, string item, bool fireLit, int status)
        {
            if (!Shortcut || station == null) return false;
            var holder = station.m_slots[slot];
            int models = holder.childCount;
            bool cooking, burnt, done, mute;
            if (item == "")
            {
                if (models != 0) return Refresh();
                cooking = burnt = done = false;
                mute = true;
            }
            else
            {
                if (models != 1 || holder.GetChild(0).name != item) return Refresh();
                cooking = fireLit && status != Burnt;
                burnt = fireLit && status == Burnt;
                done = fireLit && status == Done;
                mute = !fireLit;
            }
            if (effects(station)[slot].emission.enabled != cooking) return Refresh();
            if (station.m_burntPS.Length != 0 && station.m_burntPS[slot].emission.enabled != burnt) return Refresh();
            if (station.m_donePS.Length != 0 && station.m_donePS[slot].emission.enabled != done) return Refresh();
            if (sounds(station)[slot].mute != mute) return Refresh();
            Skipped++;
            return true;
        }

        private static bool Refresh()
        {
            Refreshed++;
            return false;
        }
    }
}
