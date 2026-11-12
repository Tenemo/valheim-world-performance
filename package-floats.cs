using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game's runtime writes each float through BitConverter, which returns its four bytes in a new array, 48 bytes of
    // garbage, and each double the same way. A network record's position is three floats, every vector and rotation in its
    // values three or four more, each terrain operation saves two or four floats for every modified vertex of its zone, and a
    // world save writes millions. The mod writes the same bytes of each float and double a package writes through the
    // writer's integer writes, which keep their own buffer. They share nothing but a rough count, so the world save's own
    // thread writes the same way.
    internal static class PackageFloats
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".package-floats");
        private static readonly AccessTools.FieldRef<ZPackage, BinaryWriter> writerOf = AccessTools.FieldRefAccess<ZPackage, BinaryWriter>("m_writer");
        internal static bool Running { get; private set; }
        // Off writes natively, with the arrays; the isolated client switches it for paired measurements.
        internal static bool Lean { get; set; } = true;
        internal static long Writes { get; private set; }

        // A value's bits, which the writer's integer writes put out little-endian as the runtime writes floats and doubles.
        [StructLayout(LayoutKind.Explicit)]
        private struct Bits
        {
            [FieldOffset(0)] internal float Single;
            [FieldOffset(0)] internal uint Word;
            [FieldOffset(0)] internal double Double;
            [FieldOffset(0)] internal ulong Long;
        }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            Patch(typeof(float), nameof(WriteSingle));
            Patch(typeof(double), nameof(WriteDouble));
            Patch(typeof(Vector3), nameof(WriteVector));
            Patch(typeof(Quaternion), nameof(WriteRotation));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        private static void Patch(System.Type value, string prefix) =>
            patches.Patch(AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { value }), prefix: new HarmonyMethod(typeof(PackageFloats), prefix));

        private static void Write(BinaryWriter writer, float value) => writer.Write(new Bits { Single = value }.Word);

        private static bool WriteSingle(ZPackage __instance, float data)
        {
            Writes++;
            if (!Lean) return true;
            Write(writerOf(__instance), data);
            return false;
        }

        private static bool WriteDouble(ZPackage __instance, double data)
        {
            Writes++;
            if (!Lean) return true;
            writerOf(__instance).Write(new Bits { Double = data }.Long);
            return false;
        }

        private static bool WriteVector(ZPackage __instance, Vector3 v3)
        {
            Writes++;
            if (!Lean) return true;
            var writer = writerOf(__instance);
            Write(writer, v3.x);
            Write(writer, v3.y);
            Write(writer, v3.z);
            return false;
        }

        private static bool WriteRotation(ZPackage __instance, Quaternion q)
        {
            Writes++;
            if (!Lean) return true;
            var writer = writerOf(__instance);
            Write(writer, q.x);
            Write(writer, q.y);
            Write(writer, q.z);
            Write(writer, q.w);
            return false;
        }
    }
}
