using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // Streaming a modifier that only paints the ground still requested a full collision and render mesh rebuild.
    // Keep the native timing, affected heightmaps and grass refresh; use its paint-only request when no heights change.
    [HarmonyPatch(typeof(TerrainModifier), "PokeHeightmaps")]
    internal static class TerrainPaintUpdates
    {
        private static void Poke(Heightmap heightmap, int delayed, bool paintOnly, TerrainModifier modifier)
        {
            if (!modifier.m_level && !modifier.m_smooth && !modifier.m_paintCleared) return;
            heightmap.Poke(delayed, paintOnly || !modifier.m_level && !modifier.m_smooth);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var poke = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.Poke));
            var calls = Enumerable.Range(0, code.Count).Where(index => code[index].Calls(poke)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("The game's terrain modifier refresh changed.");
            var original = code[calls[0]];
            code[calls[0]] = new CodeInstruction(OpCodes.Ldarg_0) { labels = original.labels, blocks = original.blocks };
            code.Insert(calls[0] + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TerrainPaintUpdates), nameof(Poke))));
            return code;
        }
    }
}
