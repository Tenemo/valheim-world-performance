using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // Preserve the native map bytes and compression, but avoid millions of individual BinaryWriter calls per save.
    [HarmonyPatch(typeof(Minimap), "GetMapData")]
    internal static class MapSaveWrites
    {
        private static readonly AccessTools.FieldRef<ZPackage, BinaryWriter> Writer = AccessTools.FieldRefAccess<ZPackage, BinaryWriter>("m_writer");

        // Inline bytes of a 64-bit word are the eight booleans the game writes for one packed byte.
        private static readonly ulong[] expandedFlags = ExpandBytes();

        private static ulong[] ExpandBytes()
        {
            var table = new ulong[256];
            var bytes = new byte[8];
            for (int value = 0; value < table.Length; value++)
            {
                for (int bit = 0; bit < bytes.Length; bit++)
                    bytes[bit] = (byte)((value >> bit) & 1);
                table[value] = BitConverter.ToUInt64(bytes, 0);
            }
            return table;
        }

        private static void WriteExploration(ZPackage package, BitArray explored, BitArray exploredOthers)
        {
            var buffer = new byte[8192];
            var words = new ulong[buffer.Length / 8];
            WriteFlags(Writer(package), explored, explored.Length, buffer, words);
            WriteFlags(Writer(package), exploredOthers, explored.Length, buffer, words);
        }

        private static void WriteFlags(BinaryWriter writer, BitArray flags, int count, byte[] buffer, ulong[] words)
        {
            if (flags.Length < count) throw new ArgumentOutOfRangeException(nameof(count));
            var packed = new byte[(flags.Length + 7) / 8];
            flags.CopyTo(packed, 0);
            for (int start = 0; start < count; start += buffer.Length)
            {
                int length = Math.Min(buffer.Length, count - start);
                int source = start / 8;
                int packedLength = (length + 7) / 8;
                for (int index = 0; index < packedLength; index++)
                    words[index] = expandedFlags[packed[source + index]];
                Buffer.BlockCopy(words, 0, buffer, 0, length);
                writer.Write(buffer, 0, length);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int size = code.FindIndex(instruction => instruction.LoadsField(AccessTools.Field(typeof(Minimap), "m_textureSize")));
            int pins = code.FindIndex(instruction => instruction.LoadsField(AccessTools.Field(typeof(Minimap), "m_pins")));
            int start = size + 2, end = pins - 1;
            var writeBoolean = AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(bool) });
            if (size < 2 || end <= start || !code[size - 2].IsLdloc() || !code[size + 1].Calls(AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(int) })) ||
                code.Skip(start).Take(end - start).Count(instruction => instruction.Calls(writeBoolean)) != 2)
                throw new InvalidOperationException("The game's map exploration serialization changed.");
            var package = new CodeInstruction(code[size - 2].opcode, code[size - 2].operand);
            package.labels.AddRange(code[start].labels);
            code.RemoveRange(start, end - start);
            code.InsertRange(start, new[] {
                package,
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Minimap), "m_explored")),
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Minimap), "m_exploredOthers")),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MapSaveWrites), nameof(WriteExploration))) });
            return code;
        }
    }
}
