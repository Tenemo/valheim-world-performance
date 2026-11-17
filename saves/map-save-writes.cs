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

        private static void WriteExploration(ZPackage package, BitArray explored, BitArray exploredOthers)
        {
            var buffer = new byte[8192];
            WriteFlags(Writer(package), explored, explored.Length, buffer);
            WriteFlags(Writer(package), exploredOthers, explored.Length, buffer);
        }

        private static void WriteFlags(BinaryWriter writer, BitArray flags, int count, byte[] buffer)
        {
            if (flags.Length < count) throw new ArgumentOutOfRangeException(nameof(count));
            var packed = new byte[(flags.Length + 7) / 8];
            flags.CopyTo(packed, 0);
            for (int start = 0; start < count; start += buffer.Length)
            {
                int length = Math.Min(buffer.Length, count - start);
                for (int index = 0; index < length; index++)
                {
                    int bit = start + index;
                    buffer[index] = (byte)((packed[bit >> 3] >> (bit & 7)) & 1);
                }
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
