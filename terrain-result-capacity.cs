using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // The terrain builder keeps 16 finished heightmaps and drops the oldest. Zone loading asks for every missing zone at once
    // and loads one a tenth of a second, so on arrival the builder ran ahead and dropped the oldest finished zones, which were
    // the next to load; each was built again from the back of the queue, and a zone dropped while it was being spawned held
    // the main thread until then. The builder now keeps 192: more than Progression Fix's largest client range of 145 zones and
    // two sets of distant terrain ask for at once, at 82.5 KB a zone.
    // The builder's thread runs this loop from the builder's first use until the game quits, before which plugins load.
    [HarmonyPatch(typeof(HeightmapBuilder), "BuildThread")]
    internal static class TerrainResultCapacity
    {
        internal const int Capacity = 192;

        // Valheim 1.0.15: while (m_ready.Count > 16) m_ready.RemoveAt(0);
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var limits = code.Where(x => x.LoadsConstant(16)).ToList();
            if (limits.Count != 1) throw new InvalidOperationException("Valheim's terrain builder changed; expected one limit of finished results.");
            limits[0].opcode = OpCodes.Ldc_I4;
            limits[0].operand = Capacity;
            return code;
        }
    }
}
