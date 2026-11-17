using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace WorldPerformance
{
    // Since the chunked world saves, the server writes only the chunks changed since its previous save, yet before every save
    // it copied the values of every record in the world for the save thread, on the server's main thread: half a second at
    // the median with three million records, up to 9.6 s, while every player waited for the server. The save thread reads
    // those copies only for the records of the chunks it writes, so the server now copies only their values. The connection
    // data is still regenerated and copied whole, and the chunk files stay the game's own byte for byte.
    //
    // A save after most of the world changed, as after a slept night, still copied the values of two to three million records
    // on the main thread, and the clones and copies made the garbage collector run twice inside that frame: 1.5 s in
    // production and 3.0-3.5 s on the test server. The value tables are now copied on worker threads while the main thread
    // waits for them, so nothing else touches the records meanwhile, and the collector waits until the save's records are
    // copied and runs in the following frames, as it does after every save. The save thread looks values up by record, so
    // the copies and the files written from them are the same.
    internal static class SavedChunkValues
    {
        // The isolated test server switches the copy back to the game's own, the workers and the held collection off, to compare.
        internal static bool Copying = true, ParallelValues = true, HoldCollection = true;
        internal static long Saves, Records;
        private static readonly ParallelOptions Workers = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 4)) };
        private static FieldInfo saveData, objectsByChunk;
        private static PropertyInfo chunkRecords;
        private static MethodInfo regenerate;
        private static Table[] tables;
        private static FieldInfo connections, savedConnections;
        private static readonly HashSet<ZDOID> saved = new HashSet<ZDOID>();

        internal static void Initialize(PerformancePatches harmony)
        {
            saveData = AccessTools.Field(typeof(ZDOMan), "m_saveData") ?? throw new MissingFieldException("ZDOMan", "m_saveData");
            objectsByChunk = AccessTools.Field(saveData.FieldType, "m_objectsByChunk") ?? throw new MissingFieldException("SaveData", "m_objectsByChunk");
            var chunk = objectsByChunk.FieldType.GetGenericArguments()[0];
            chunkRecords = chunk.GetProperty("Item2");
            if (chunkRecords == null || chunkRecords.PropertyType != typeof(List<ZDO>)) throw new MissingMemberException("The save's chunk list changed.");
            regenerate = AccessTools.Method(typeof(ZDOExtraData), "RegenerateConnectionHashData") ?? throw new MissingMethodException("ZDOExtraData", "RegenerateConnectionHashData");
            tables = new Table[] { new Table<float>("s_floats", "s_saveFloats"), new Table<UnityEngine.Vector3>("s_vec3", "s_saveVec3s"),
                new Table<UnityEngine.Quaternion>("s_quats", "s_saveQuats"), new Table<int>("s_ints", "s_saveInts"), new Table<long>("s_longs", "s_saveLongs"),
                new Table<string>("s_strings", "s_saveStrings"), new Table<byte[]>("s_byteArrays", "s_saveByteArrays") };
            connections = AccessTools.Field(typeof(ZDOExtraData), "s_connectionsHashData");
            savedConnections = AccessTools.Field(typeof(ZDOExtraData), "s_saveConnections");
            if (connections == null || savedConnections == null || connections.FieldType != typeof(Dictionary<ZDOID, ZDOConnectionHashData>) ||
                savedConnections.FieldType != connections.FieldType)
                throw new MissingFieldException("ZDOExtraData", "s_saveConnections");
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.PrepareSave)), prefix: new HarmonyMethod(typeof(SavedChunkValues), nameof(Hold)),
                finalizer: new HarmonyMethod(typeof(SavedChunkValues), nameof(Release)));
            harmony.Patch(AccessTools.Method(typeof(ZDOExtraData), nameof(ZDOExtraData.PrepareSave)), prefix: new HarmonyMethod(typeof(SavedChunkValues), nameof(Prepare)));
        }

        private static bool holding;

        private static void Hold()
        {
            if (!HoldCollection || ZNet.instance == null || !ZNet.instance.IsServer() ||
                UnityEngine.Scripting.GarbageCollector.GCMode != UnityEngine.Scripting.GarbageCollector.Mode.Enabled) return;
            UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
            holding = true;
        }

        private static Exception Release(Exception __exception)
        {
            if (holding) { holding = false; UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled; }
            return __exception;
        }

        private static bool Prepare()
        {
            var network = ZNet.instance;
            if (!Copying || (object)network == null || !network.IsServer() || ZDOMan.instance == null) return true;
            var save = saveData.GetValue(ZDOMan.instance);
            if (save == null || !(objectsByChunk.GetValue(save) is IEnumerable chunks)) return true;
            var lists = new List<List<ZDO>>();
            long count = 0;
            foreach (object chunk in chunks)
            {
                var records = (List<ZDO>)chunkRecords.GetValue(chunk);
                lists.Add(records);
                count += records.Count;
            }
            regenerate.Invoke(null, null);
            bool copied = false;
            if (ParallelValues)
            {
                try
                {
                    var copies = new object[tables.Length];
                    System.Threading.Tasks.Parallel.For(0, tables.Length, Workers, index => copies[index] = tables[index].Copy(lists, count));
                    for (int index = 0; index < tables.Length; index++) tables[index].Store(copies[index]);
                    copied = true;
                }
                catch (AggregateException error)
                {
                    Plugin.Log.LogWarning("Saving copies the record values on the main thread after a worker failed: " + error.GetBaseException().Message);
                }
            }
            if (!copied)
            {
                saved.Clear();
                foreach (var records in lists)
                    foreach (var data in records) saved.Add(data.m_uid);
                foreach (var table in tables) table.Store(table.Copy(saved));
                saved.Clear();
            }
            savedConnections.SetValue(null, new Dictionary<ZDOID, ZDOConnectionHashData>((Dictionary<ZDOID, ZDOConnectionHashData>)connections.GetValue(null)));
            Saves++;
            Records += count;
            return false;
        }

        private abstract class Table
        {
            internal abstract object Copy(HashSet<ZDOID> records);
            internal abstract object Copy(List<List<ZDO>> chunks, long count);
            internal abstract void Store(object copy);
        }

        // The same copy as the game's, a shallow copy of each record's values, made only for the saved records.
        private sealed class Table<T> : Table
        {
            private readonly FieldInfo live, save;

            internal Table(string liveName, string saveName)
            {
                live = AccessTools.Field(typeof(ZDOExtraData), liveName);
                save = AccessTools.Field(typeof(ZDOExtraData), saveName);
                if (live == null || save == null || live.FieldType != typeof(Dictionary<ZDOID, BinarySearchDictionary<int, T>>) || save.FieldType != live.FieldType)
                    throw new MissingFieldException("ZDOExtraData", liveName);
            }

            internal override object Copy(HashSet<ZDOID> records)
            {
                var values = (Dictionary<ZDOID, BinarySearchDictionary<int, T>>)live.GetValue(null);
                var copy = new Dictionary<ZDOID, BinarySearchDictionary<int, T>>(Math.Min(records.Count, values.Count));
                foreach (var id in records)
                    if (values.TryGetValue(id, out var recordValues)) copy[id] = (BinarySearchDictionary<int, T>)recordValues.Clone();
                return copy;
            }

            // Each record is in exactly one chunk's list, so the copy holds the same entries as from the set of their identifiers.
            internal override object Copy(List<List<ZDO>> chunks, long count)
            {
                var values = (Dictionary<ZDOID, BinarySearchDictionary<int, T>>)live.GetValue(null);
                var copy = new Dictionary<ZDOID, BinarySearchDictionary<int, T>>((int)Math.Min(count, values.Count));
                foreach (var records in chunks)
                    foreach (var data in records)
                        if (values.TryGetValue(data.m_uid, out var recordValues)) copy[data.m_uid] = (BinarySearchDictionary<int, T>)recordValues.Clone();
                return copy;
            }

            internal override void Store(object copy) => save.SetValue(null, copy);
        }
    }
}
