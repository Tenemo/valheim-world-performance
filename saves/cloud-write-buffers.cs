using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // The game's Steam Cloud file write copies the file into a new 100 MB buffer before handing it to Steam, whatever the file's
    // size. A character save writes three files, so it allocated 300 MB on the main thread, which made the frame wait for the
    // memory and usually for a garbage collection. The buffer is now as large as the file when the file fits in one 100 MB
    // chunk; Steam receives the same calls with the same bytes. Larger files keep the game's buffer.
    internal static class CloudWriteBuffers
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".cloud-write-buffers");
        internal static bool Running { get; private set; }
        // Off allocates the game's 100 MB buffer; the isolated client switches it for paired measurements.
        internal static bool Lean { get; set; } = true;

        internal static void Start()
        {
            if (Running) return;
            var cloud = AccessTools.TypeByName("Splatform.Steam.SteamCloud") ?? throw new InvalidOperationException("The game's Steam Cloud provider is missing.");
            patches.Patch(AccessTools.Method(cloud, "WriteFile"), transpiler: new HarmonyMethod(typeof(CloudWriteBuffers), nameof(Transpiler)));
            Running = true;
        }

        // The chunk buffer: the file's size when it fits in one chunk, else the chunk size, which the game fills chunk by chunk.
        private static byte[] ChunkBuffer(int chunk, byte[] data) => new byte[Lean ? Math.Min(chunk, data.Length) : chunk];

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int allocation = code.FindIndex(instruction => instruction.opcode == OpCodes.Newarr && (Type)instruction.operand == typeof(byte));
            if (allocation < 0 || code.Count(instruction => instruction.opcode == OpCodes.Newarr) != 1 ||
                !code.Any(instruction => instruction.LoadsConstant(104857600)))
                throw new InvalidOperationException("The game's Steam Cloud write changed.");
            // WriteFile(string filePath, byte[] data, ...): the data is argument 2.
            code[allocation] = new CodeInstruction(OpCodes.Ldarg_2) { labels = code[allocation].labels, blocks = code[allocation].blocks };
            code.Insert(allocation + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CloudWriteBuffers), nameof(ChunkBuffer))));
            return code;
        }
    }
}
