using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Since the chunked world saves, the server writes only the chunks marked since its previous save. It marks a chunk for its
    // own changes and for saved records moving between zones, but applies the changes and new records a player's game sends
    // without marking anything, so a chest filled, a sign written or a piece built after a save stayed unsaved until something
    // else marked the chunk, and a restart before that lost it. The server now marks the chunk of a saved record whose position,
    // flags, prefab, rotation, connection or values such an update actually changes; an update that changes nothing, as when a
    // value went back to what it was, leaves the chunk as it was, so the save copies no chunk the changes do not need.
    internal static class ReceivedRecordSaves
    {
        private static AccessTools.FieldRef<ZDO, Vector3> rotationOf;
        private static Values[] tables;
        private static bool persistent, distant;
        private static ZDO.ObjectType type;
        private static int prefab;
        private static Vector3 rotation;
        private static ZDOConnection connection;
        internal static long Compared, Unchanged, Marked;

        internal static void Initialize(PerformancePatches harmony)
        {
            rotationOf = AccessTools.FieldRefAccess<ZDO, Vector3>("m_rotation");
            tables = new Values[] { new Values<float>("s_floats"), new Values<Vector3>("s_vec3"), new Values<Quaternion>("s_quats"),
                new Values<int>("s_ints"), new Values<long>("s_longs"), new Values<string>("s_strings"), new Values<byte[]>("s_byteArrays", new SameBytes()) };
            harmony.Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.InternalSetPosition)), prefix: new HarmonyMethod(typeof(ReceivedRecordSaves), nameof(Moving)));
            harmony.Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.Deserialize)), prefix: new HarmonyMethod(typeof(ReceivedRecordSaves), nameof(Reading)),
                postfix: new HarmonyMethod(typeof(ReceivedRecordSaves), nameof(Read)));
        }

        private static bool Server
        {
            get
            {
                var network = ZNet.instance;
                return (object)network != null && network.IsServer();
            }
        }

        // A move within a zone; one into another zone also marks both through the zone lists, and the server's own moves mark
        // through their revision.
        private static void Moving(ZDO __instance, Vector3 __0)
        {
            if (__instance.Persistent && !(__instance.GetPosition() == __0) && !__instance.IsOwner() && Server) Mark(__instance);
        }

        private static void Reading(ZDO __instance, out bool __state)
        {
            __state = Server;
            if (!__state) return;
            persistent = __instance.Persistent; distant = __instance.Distant; type = __instance.Type; prefab = __instance.GetPrefab();
            rotation = rotationOf(__instance);
            connection = ZDOExtraData.GetConnection(__instance.m_uid);
            foreach (var values in tables) values.Keep(__instance.m_uid);
        }

        private static void Read(ZDO __instance, bool __state)
        {
            if (!__state) return;
            Compared++;
            // The values are compared in every case, so that no kept copy outlives this update.
            bool changed = false;
            foreach (var values in tables) changed |= values.Changed(__instance.m_uid);
            // The connection is replaced only when it changes.
            changed |= persistent != __instance.Persistent || distant != __instance.Distant || type != __instance.Type || prefab != __instance.GetPrefab() ||
                !rotation.Equals(rotationOf(__instance)) || !ReferenceEquals(connection, ZDOExtraData.GetConnection(__instance.m_uid));
            connection = null;
            if (!changed) Unchanged++;
            else if (__instance.Persistent) Mark(__instance);
        }

        // Portals are saved apart from the chunks.
        private static void Mark(ZDO record)
        {
            if (Game.instance != null && Game.instance.PortalPrefabHash.Contains(record.GetPrefab())) ZDOMan.instance.SetDirtyPortals();
            else ZDOMan.instance.SetDirtySector(record);
            Marked++;
        }

        private abstract class Values
        {
            internal abstract void Keep(ZDOID id);
            internal abstract bool Changed(ZDOID id);
        }

        // One of ZDOExtraData's value tables; a record's values are kept in arrays reused for the next record.
        private sealed class Values<T> : Values
        {
            private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, int[]> keysOf = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, int[]>("m_keys");
            private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, T[]> itemsOf = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, T[]>("m_values");
            private static readonly AccessTools.FieldRef<BinarySearchDictionary<int, T>, ushort> lengthOf = AccessTools.FieldRefAccess<BinarySearchDictionary<int, T>, ushort>("m_length");
            private readonly Dictionary<ZDOID, BinarySearchDictionary<int, T>> records;
            private readonly IEqualityComparer<T> same;
            private int[] keys = new int[32];
            private T[] items = new T[32];
            private int length;

            internal Values(string field, IEqualityComparer<T> same = null)
            {
                records = (Dictionary<ZDOID, BinarySearchDictionary<int, T>>)AccessTools.Field(typeof(ZDOExtraData), field).GetValue(null);
                this.same = same ?? EqualityComparer<T>.Default;
            }

            internal override void Keep(ZDOID id)
            {
                length = records.TryGetValue(id, out var values) ? lengthOf(values) : 0;
                if (length == 0) return;
                if (keys.Length < length) { keys = new int[length * 2]; items = new T[length * 2]; }
                Array.Copy(keysOf(values), keys, length);
                Array.Copy(itemsOf(values), items, length);
            }

            // A missing table and an empty one save the same.
            internal override bool Changed(ZDOID id)
            {
                bool changed;
                if (!records.TryGetValue(id, out var values) || lengthOf(values) == 0) changed = length != 0;
                else if (lengthOf(values) != length) changed = true;
                else
                {
                    changed = false;
                    int[] nowKeys = keysOf(values);
                    T[] nowItems = itemsOf(values);
                    for (int index = 0; index < length && !changed; index++)
                        changed = nowKeys[index] != keys[index] || !same.Equals(nowItems[index], items[index]);
                }
                Array.Clear(items, 0, length);
                length = 0;
                return changed;
            }
        }

        // A received byte array is always a new array; only its bytes matter.
        private sealed class SameBytes : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] first, byte[] second)
            {
                if (ReferenceEquals(first, second)) return true;
                if (first == null || second == null || first.Length != second.Length) return false;
                for (int index = 0; index < first.Length; index++)
                    if (first[index] != second[index]) return false;
                return true;
            }

            public int GetHashCode(byte[] bytes) => bytes == null ? 0 : bytes.Length;
        }
    }
}
