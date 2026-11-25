using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Sending a network record makes garbage: reading one builds two delegates per value table, and every batch of records is
    // built in two new packages whose streams grow by doubling. The player who simulates an area sends every record that
    // changes there, 300 to 1,000 a second at the base. The mod reads the same values in the same order without the
    // delegates, writes each value directly instead of through a delegate call, and builds each batch in two packages it
    // keeps, so the bytes sent and the records read stay identical. The batch packages are kept
    // only while no other mod patches the methods a batch passes through, since one could keep a package the next batch reuses.
    internal static class LeanRecordUpdates
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".lean-record-updates");
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
            Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.Serialize)), nameof(LeanSerialize));
            Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.Deserialize)), nameof(LeanDeserialize));
            var passes = new MethodBase[] { AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Invoke), new[] { typeof(string), typeof(object[]) }),
                AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Serialize)), AccessTools.Method(typeof(ZRpc), "SendPackage"),
                AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(ZPackage) }),
                // Network Performance System reuses the same two packages here, so this reaches that method first or not at all.
                AccessTools.Method(typeof(ZDOMan), "SendZDOs") };
            var foreign = passes.Where(method => method == null || Patched(method)).Select(method => method == null ? "a missing method" : method.DeclaringType.Name + "." + method.Name).ToList();
            keepPackages = foreign.Count == 0;
            if (!keepPackages) Fail("batches use new packages, since other mods patch " + string.Join(", ", foreign), false);
            else Patch(AccessTools.Method(typeof(ZDOMan), "SendZDOs"), nameof(KeepBatchPackages));
        }

        // An error leaves that method native and takes this mod's patch off it, so that other mods can still patch it.
        private static void Patch(MethodBase method, string transpiler)
        {
            if (method == null) { Fail("a patched game method is missing, so its part stays native", true); return; }
            try
            {
                patches.Patch(method, transpiler: new HarmonyMethod(typeof(LeanRecordUpdates), transpiler));
            }
            catch (Exception error)
            {
                Fail($"{method.DeclaringType.Name}.{method.Name} stays native after an error: {error}", true);
                try { patches.Unpatch(method, HarmonyPatchType.All, patches.Id); }
                catch (Exception unpatching) { Fail($"removing the patch from {method.DeclaringType.Name}.{method.Name} failed: {unpatching}", true); }
            }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchOwner();
            batch = record = null;
        }

        // Another mod's patch, not this mod's own: every part of this mod patches under an owner named after it.
        private static bool Patched(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Any(patch => patch.owner != patches.Id && !patch.owner.StartsWith(Plugin.Id, StringComparison.Ordinal));
        }

        // A part that stays native because the game changed is reported as a fallback; another mod's patch on the batch send is
        // expected beside Better Networking and Network Performance System, so that one is only noted.
        private static void Fail(string reason, bool fallback)
        {
            Failure = Failure == null ? reason : Failure + "; " + reason;
            if (fallback) Plugin.Fallback("Network records: " + reason);
            else Plugin.Log.LogWarning("Network records: " + reason + ".");
        }

        // The serializer writes each table with a writer of its own type instead of calling the game's writer delegate for each value.
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
                var writer = AccessTools.Method(typeof(LeanRecordUpdates), nameof(Write), new[] { typeof(ZPackage), typeof(FastIterableKeyValues<,>).MakeGenericType(typeof(int), type),
                    typeof(Action<,>).MakeGenericType(typeof(ZPackage), type) });
                if (writer == null) break;
                code[index].operand = writer;
                written++;
            }
            if (found == 1 && written == 7) return code;
            Fail("ZDO.Serialize no longer has the expected shape, so records are written natively", true);
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
            Fail("ZDO.Deserialize no longer has the expected shape, so records are read natively", true);
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
                Fail("ZDOMan.SendZDOs no longer has the expected shape, so batches use new packages", true);
                return native;
            }
            sites[0].opcode = sites[1].opcode = OpCodes.Call;
            sites[0].operand = AccessTools.Method(typeof(LeanRecordUpdates), nameof(BatchPackage));
            sites[1].operand = AccessTools.Method(typeof(LeanRecordUpdates), nameof(RecordPackage));
            return code;
        }

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

        // ZDOExtraData.GetData, counting the records written.
        private static void Values(ZDOID zid, out FastIterableKeyValues<int, float> floats, out FastIterableKeyValues<int, Vector3> vec3s, out FastIterableKeyValues<int, Quaternion> quats,
            out FastIterableKeyValues<int, int> ints, out FastIterableKeyValues<int, long> longs, out FastIterableKeyValues<int, string> strings, out FastIterableKeyValues<int, byte[]> byteArrays,
            out ZDOConnection connection)
        {
            Written++;
            ZDOExtraData.GetData(zid, out floats, out vec3s, out quats, out ints, out longs, out strings, out byteArrays, out connection);
        }

        // ZDODataHelper.WriteData for each value type: the item count, then each key and value as the native writers write them.
        private static void Write(ZPackage pkg, FastIterableKeyValues<int, float> data, Action<ZPackage, float> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, FastIterableKeyValues<int, Vector3> data, Action<ZPackage, Vector3> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, FastIterableKeyValues<int, Quaternion> data, Action<ZPackage, Quaternion> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, FastIterableKeyValues<int, int> data, Action<ZPackage, int> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, FastIterableKeyValues<int, long> data, Action<ZPackage, long> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, FastIterableKeyValues<int, string> data, Action<ZPackage, string> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
        }

        private static void Write(ZPackage pkg, FastIterableKeyValues<int, byte[]> data, Action<ZPackage, byte[]> native)
        {
            if (!Lean) { ZDODataHelper.WriteData(pkg, data, native); return; }
            if (data.Count == 0) return;
            pkg.WriteNumItems(data.Count);
            for (int index = 0; index < data.Count; index++) { var pair = data[index]; pkg.Write(pair.Key); pkg.Write(pair.Value); }
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
