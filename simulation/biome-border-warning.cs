using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // Once a second the player's biome update compares the biome sector the position falls in, which the game uses as the
    // current biome, with the biome computed at the exact position, and logs "GetBiome error" with a stack trace wherever
    // they differ. The sectors are a fixed grid of 12 m cells, each holding the biome at its lower-left corner, so the two
    // differ by design along every biome border, up to a cell inside it, and each warning adds about 1.5 ms to its frame.
    // The mod skips only that warning; the current biome, discoveries and the rest of the update stay native.
    internal static class BiomeBorderWarning
    {
        private const string Message = "GetBiome error {0} -> {1} {2} -> {3}, {4}";
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".biome-border-warning");
        internal static bool Running { get; private set; }
        // Off logs the warning natively; the isolated client switches it for its checks and paired measurements.
        internal static bool Skipping { get; set; } = true;

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(Player), "UpdateBiome"), transpiler: new HarmonyMethod(typeof(BiomeBorderWarning), nameof(Transpiler)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        // Natively the warning follows "if (sector.Biome != biome)", a beq past it; the mod branches on Logs instead.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int message = code.FindIndex(instruction => instruction.opcode == OpCodes.Ldstr && instruction.operand as string == Message);
            if (message < 1 || code[message - 1].opcode != OpCodes.Beq_S && code[message - 1].opcode != OpCodes.Beq)
            {
                Plugin.Fallback("Player.UpdateBiome no longer logs its biome warning where expected, so the warning stays native.");
                return code;
            }
            var skip = code[message - 1];
            code[message - 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BiomeBorderWarning), nameof(Logs))).MoveLabelsFrom(skip);
            code.Insert(message, new CodeInstruction(OpCodes.Brfalse, skip.operand));
            return code;
        }

        private static bool Logs(Heightmap.Biome sector, Heightmap.Biome position) => sector != position && !Skipping;
    }
}
