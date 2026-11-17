using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Sending a network record makes garbage: serializing it copies each of its seven value tables into a new list and builds
    // a delegate per table to write the values, about 1.25 KB a record, and reading one builds two delegates per table. Every
    // batch of records is built in two new packages whose streams grow by doubling, and writing a package into another first
    // copies it into a new array. The player who simulates an area sends every record that changes there, 300 to 1,000 a second
    // at the base, where this made up to two fifths of the client's garbage. The mod writes and reads the same values
    // in the same order without the lists and delegates, writes a package into another straight from its stream, and builds
    // each batch in two packages it keeps, so the bytes sent and the records read stay identical. The batch packages are kept
    // only while no other mod patches the methods a batch passes through, since one could keep a package the next batch reuses.
    internal static class LeanRecordUpdates
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".lean-record-updates");
        private static readonly AccessTools.FieldRef<ZPackage, MemoryStream> streamOf = AccessTools.FieldRefAccess<ZPackage, MemoryStream>("m_stream");
        private static readonly AccessTools.FieldRef<ZPackage, BinaryWriter> writerOf = AccessTools.FieldRefAccess<ZPackage, BinaryWriter>("m_writer");
        private static ZPackage batch, record;
        private static bool keepPackages;
        private static int mainThread;
        internal static bool Running { get; private set; }
        // Off writes, reads and sends records natively, with the native garbage; the isolated client switches it for paired measurements.
        internal static bool Lean { get; set; } = true;
        // The parts that stayed native, with why, or null.
        internal static string Failure { get; private set; }
        internal static long Written { get; private set; }
        internal static long Read { get; private set; }
        internal static long Batches { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            Failure = null;
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.Serialize)), transpiler: nameof(LeanSerialize));
            Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.Deserialize)), transpiler: nameof(LeanDeserialize));
            Patch(AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(ZPackage) }), prefix: nameof(WriteFromStream));
            var passes = new MethodBase[] { AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Invoke), new[] { typeof(string), typeof(object[]) }),
                AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Serialize)), AccessTools.Method(typeof(ZRpc), "SendPackage"),
                AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(ZPackage) }),
                // Network Performance System reuses the same two packages here, so this reaches that method first or not at all.
                AccessTools.Method(typeof(ZDOMan), "SendZDOs") };
            var foreign = passes.Where(method => method == null || Patched(method)).Select(method => method == null ? "a missing method" : method.DeclaringType.Name + "." + method.Name).ToList();
            keepPackages = foreign.Count == 0;
            if (!keepPackages) Fail("batches use new packages, since other mods patch " + string.Join(", ", foreign));
            else Patch(AccessTools.Method(typeof(ZDOMan), "SendZDOs"), transpiler: nameof(KeepBatchPackages));
        }

        // An error leaves that method native and takes this mod's patch off it, so that other mods can still patch it.
        private static void Patch(MethodBase method, string prefix = null, string transpiler = null)
        {
            try
            {
                patches.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(LeanRecordUpdates), prefix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(LeanRecordUpdates), transpiler));
            }
            catch (Exception error)
            {
                Fail($"{method.DeclaringType.Name}.{method.Name} stays native after an error: {error}");
                try { patches.Unpatch(method, HarmonyPatchType.All, patches.Id); }
                catch (Exception unpatching) { Fail($"removing the patch from {method.DeclaringType.Name}.{method.Name} failed: {unpatching}"); }
            }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            batch = record = null;
        }

        // Another mod's patch, not this mod's own: every part of this mod patches under an owner named after it.
        private static bool Patched(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Any(patch => patch.owner != patches.Id && !patch.owner.StartsWith(Plugin.Id, StringComparison.Ordinal));
        }

        private static void Fail(string reason)
        {
            Failure = Failure == null ? reason : Failure + "; " + reason;
            Plugin.Log.LogWarning("Network records: " + reason + ".");
        }

        // The serializer gets the record's values in kept lists and writes each table with a writer of its own type instead of a
        // delegate; the delegates it no longer builds become nulls.
        private static IEnumerable<CodeInstruction> LeanSerialize(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var native = instructions.ToList();
            var code = native.Select(instruction => new CodeInstruction(instruction)).ToList();
            var values = AccessTools.Method(typeof(ZDOExtraData), nameof(ZDOExtraData.GetData));
            var tables = AccessTools.Method(typeof(ZDODataHelper), nameof(ZDODataHelper.WriteData));
            int found = 0, written = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (code[index].Calls(values)) { code[index].operand = AccessTools.Method(typeof(LeanRecordUpdates), nameof(Values)); found++; continue; }
                if (!(code[index].operand is MethodInfo called) || !called.IsGenericMethod || called.GetGenericMethodDefinition() != tables) continue;
                var type = called.GetGenericArguments()[0];
                var writer = AccessTools.Method(typeof(LeanRecordUpdates), nameof(Write), new[] { typeof(ZPackage), Pairs(type), typeof(Action<>).MakeGenericType(type) });
                if (writer == null || !Unbuild(code, index, typeof(Action<>).MakeGenericType(type))) break;
                code[index].operand = writer;
                written++;
            }
            if (found == 1 && written == 7) return code;
            Fail("ZDO.Serialize no longer has the expected shape, so records are written natively");
            return native;
        }

        // The reader reads each table with a reader of its own type instead of the two delegates per table it built.
        private static IEnumerable<CodeInstruction> LeanDeserialize(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var native = instructions.ToList();
            var code = native.Select(instruction => new CodeInstruction(instruction)).ToList();
            var tables = AccessTools.Method(typeof(ZDODataHelper), nameof(ZDODataHelper.ReadData));
            int read = 0;
            for (int index = 0; index < code.Count; index++)
            {
                if (!(code[index].operand is MethodInfo called) || !called.IsGenericMethod || called.GetGenericMethodDefinition() != tables) continue;
                var type = called.GetGenericArguments()[0];
                var function = typeof(Func<>).MakeGenericType(type);
                var add = typeof(Action<,,>).MakeGenericType(typeof(ZDOID), typeof(int), type);
                var reader = AccessTools.Method(typeof(LeanRecordUpdates), nameof(ReadTable), new[] { typeof(bool), typeof(ZPackage), typeof(ZDOExtraData.Type), typeof(ZDOID), function, add });
                if (reader == null || !Unbuild(code, index, function) || !Unbuild(code, index, add)) break;
                code[index].operand = reader;
                read++;
            }
            if (read == 7) return code;
            Fail("ZDO.Deserialize no longer has the expected shape, so records are read natively");
            return native;
        }

        // A batch's two packages come from the kept ones.
        private static IEnumerable<CodeInstruction> KeepBatchPackages(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var native = instructions.ToList();
            var code = native.Select(instruction => new CodeInstruction(instruction)).ToList();
            var created = AccessTools.Constructor(typeof(ZPackage), Type.EmptyTypes);
            var sites = code.Where(instruction => instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, created)).ToList();
            if (sites.Count != 2)
            {
                Fail("ZDOMan.SendZDOs no longer has the expected shape, so batches use new packages");
                return native;
            }
            sites[0].opcode = sites[1].opcode = OpCodes.Call;
            sites[0].operand = AccessTools.Method(typeof(LeanRecordUpdates), nameof(BatchPackage));
            sites[1].operand = AccessTools.Method(typeof(LeanRecordUpdates), nameof(RecordPackage));
            return code;
        }

        private static Type Pairs(Type type) => typeof(List<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(typeof(int), type));

        // Replaces the nearest construction of a delegate of the given type before a call with a null of that type.
        private static bool Unbuild(List<CodeInstruction> code, int call, Type delegateType)
        {
            for (int index = call - 1; index >= Math.Max(0, call - 8); index--)
            {
                if (code[index].opcode != OpCodes.Newobj || !(code[index].operand is ConstructorInfo constructor) || constructor.DeclaringType != delegateType) continue;
                code[index].opcode = OpCodes.Call;
                code[index].operand = AccessTools.Method(typeof(LeanRecordUpdates), nameof(Unbuilt)).MakeGenericMethod(delegateType);
                return true;
            }
            return false;
        }

        private static TDelegate Unbuilt<TDelegate>(object target, IntPtr method) where TDelegate : class => null;

        private static bool Kept => Lean && System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread;

        private static ZPackage BatchPackage() => Reuse(ref batch);
        private static ZPackage RecordPackage() => Reuse(ref record);

        private static ZPackage Reuse(ref ZPackage kept)
        {
            if (!Kept) return new ZPackage();
            if (kept == null) kept = new ZPackage(); else kept.Clear();
            if (ReferenceEquals(kept, batch)) Batches++;
            return kept;
        }

        // ZPackage.Write(ZPackage) writes the length and the bytes of the other package, which GetArray would first copy into an
        // array; the bytes come straight from its stream instead.
        private static bool WriteFromStream(ZPackage __instance, ZPackage pkg)
        {
            if (!Lean || pkg == null || ReferenceEquals(pkg, __instance)) return true;
            writerOf(pkg).Flush();
            if (!streamOf(pkg).TryGetBuffer(out var bytes)) return true;
            var writer = writerOf(__instance);
            writer.Write(bytes.Count);
            writer.Write(bytes.Array, bytes.Offset, bytes.Count);
            return false;
        }

        // ZDOExtraData.GetData, with the record's values in lists kept for the next record instead of new ones.
        private static void Values(ZDOID zid, out List<KeyValuePair<int, float>> floats, out List<KeyValuePair<int, Vector3>> vec3s, out List<KeyValuePair<int, Quaternion>> quats,
            out List<KeyValuePair<int, int>> ints, out List<KeyValuePair<int, long>> longs, out List<KeyValuePair<int, string>> strings, out List<KeyValuePair<int, byte[]>> byteArrays,
            out ZDOConnection connection)
        {
            Written++;
            if (!Kept)
            {
                ZDOExtraData.GetData(zid, out floats, out vec3s, out quats, out ints, out longs, out strings, out byteArrays, out connection);
                return;
            }
            floats = Table<float>.Copy(zid);
            vec3s = Table<Vector3>.Copy(zid);
            quats = Table<Quaternion>.Copy(zid);
            ints = Table<int>.Copy(zid);
            longs = Table<long>.Copy(zid);
            strings = Table<string>.Copy(zid);
            byteArrays = Table<byte[]>.Copy(zid);
            connection = ZDOExtraData.GetConnection(zid);
        }

        // One of ZDOExtraData's value tables, read from the arrays of its sorted dictionaries in their order, as ToList copies them.
        private static class Table<T>
        {
            private static readonly Dictionary<ZDOID, BinarySearchDictionary<int, T>> table = (Dictionary<ZDOID, BinarySearchDictionary<int, T>>)AccessTools.Field(typeof(ZDOExtraData), Name()).GetValue(null);
            private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, int[]> keysOf = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, int[]>("m_keys");
            private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, T[]> valuesOf = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, T[]>("m_values");
            private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, ushort> lengthOf = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, ushort>("m_length");
            private static readonly List<KeyValuePair<int, T>> kept = new List<KeyValuePair<int, T>>();

            private static string Name() =>
                typeof(T) == typeof(float) ? "s_floats" : typeof(T) == typeof(Vector3) ? "s_vec3" : typeof(T) == typeof(Quaternion) ? "s_quats" : typeof(T) == typeof(int) ? "s_ints"
                : typeof(T) == typeof(long) ? "s_longs" : typeof(T) == typeof(string) ? "s_strings" : "s_byteArrays";

            internal static List<KeyValuePair<int, T>> Copy(ZDOID zid)
            {
                kept.Clear();
                if (!table.TryGetValue(zid, out var values)) return kept;
                int[] keys = keysOf(values);
                T[] items = valuesOf(values);
                for (int index = 0, length = lengthOf(values); index < length; index++) kept.Add(new KeyValuePair<int, T>(keys[index], items[index]));
                return kept;
            }
        }

        // ZDODataHelper.WriteData for each value type: the item count, then each key and value as the native delegates write them.
        private static void Write(ZPackage pkg, List<KeyValuePair<int, float>> data, Action<float> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, List<KeyValuePair<int, Vector3>> data, Action<Vector3> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, List<KeyValuePair<int, Quaternion>> data, Action<Quaternion> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, List<KeyValuePair<int, int>> data, Action<int> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, List<KeyValuePair<int, long>> data, Action<long> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, List<KeyValuePair<int, string>> data, Action<string> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, List<KeyValuePair<int, byte[]>> data, Action<byte[]> unused)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, pkg.Write); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            foreach (var pair in data) { pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        // ZDODataHelper.ReadData for each value type: the item count, then each key and value, added to the record in the native order.
        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<float> unused, Action<ZDOID, int, float> unusedAdd)
        {
            // The float table comes first in every record.
            Read++;
            if (!Lean) { ZDODataHelper.ReadData<float>(read, pkg, type, uid, pkg.ReadSingle, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadSingle()); }
        }

        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<Vector3> unused, Action<ZDOID, int, Vector3> unusedAdd)
        {
            if (!Lean) { ZDODataHelper.ReadData<Vector3>(read, pkg, type, uid, pkg.ReadVector3, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadVector3()); }
        }

        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<Quaternion> unused, Action<ZDOID, int, Quaternion> unusedAdd)
        {
            if (!Lean) { ZDODataHelper.ReadData<Quaternion>(read, pkg, type, uid, pkg.ReadQuaternion, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadQuaternion()); }
        }

        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<int> unused, Action<ZDOID, int, int> unusedAdd)
        {
            if (!Lean) { ZDODataHelper.ReadData<int>(read, pkg, type, uid, pkg.ReadInt, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadInt()); }
        }

        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<long> unused, Action<ZDOID, int, long> unusedAdd)
        {
            if (!Lean) { ZDODataHelper.ReadData<long>(read, pkg, type, uid, pkg.ReadLong, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadLong()); }
        }

        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<string> unused, Action<ZDOID, int, string> unusedAdd)
        {
            if (!Lean) { ZDODataHelper.ReadData<string>(read, pkg, type, uid, pkg.ReadString, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadString()); }
        }

        private static void ReadTable(bool read, ZPackage pkg, ZDOExtraData.Type type, ZDOID uid, Func<byte[]> unused, Action<ZDOID, int, byte[]> unusedAdd)
        {
            if (!Lean) { ZDODataHelper.ReadData<byte[]>(read, pkg, type, uid, pkg.ReadByteArray, ZDOExtraData.Add); return; }
            if (!read) return;
            int count = Begin(pkg, type, uid);
            for (int index = 0; index < count; index++) { int key = pkg.ReadInt(); ZDOExtraData.Add(uid, key, pkg.ReadByteArray()); }
        }

        private static int Begin(ZPackage pkg, ZDOExtraData.Type type, ZDOID uid)
        {
            int count = ZDO.ReadNumItems(pkg);
            ZDOExtraData.Reserve(uid, type, count);
            return count;
        }
    }
}
