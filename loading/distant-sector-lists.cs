using System;
using System.Collections.Generic;
using HarmonyLib;

namespace WorldPerformance
{
    // Up to 30 times a second the game lists the records around the player, and for the ring of sectors beyond the near area
    // it walks every record of each sector to pick out the distant ones, such as buildings: tens of thousands of records a
    // pass around a large base, most of them not distant. The mod keeps each sector's distant records in the sector list's
    // order and walks the sector again only when its list has changed or one of its records turned distant or back; the
    // pass gets the same records in the same order.
    internal static class DistantSectorLists
    {
        // The isolated test client sets false to compare the native walk.
        internal static bool Keeping = true;
        internal static long Kept, Walked;
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".distant-sector-lists");
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> sectorsOf = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static AccessTools.FieldRef<List<ZDO>, int> versionOf;

        private sealed class Entry
        {
            internal List<ZDO> Source;
            internal int Version;
            internal bool Valid;
            internal readonly List<ZDO> Distant = new List<ZDO>();
        }

        private static readonly Dictionary<uint, Entry> entries = new Dictionary<uint, Entry>();

        internal static void Start()
        {
            try
            {
                versionOf = AccessTools.FieldRefAccess<List<ZDO>, int>("_version");
                patches.Patch(AccessTools.Method(typeof(ZDOMan), "FindDistantObjects"), prefix: new HarmonyMethod(typeof(DistantSectorLists), nameof(Find)) { priority = Priority.Last });
                // Every place the game changes a record's distant flag; a change forgets the record's sector.
                var watch = new HarmonyMethod(typeof(DistantSectorLists), nameof(Before));
                var check = new HarmonyMethod(typeof(DistantSectorLists), nameof(After));
                foreach (var method in new[] { AccessTools.Method(typeof(ZDO), nameof(ZDO.Deserialize)), AccessTools.Method(typeof(ZDO), nameof(ZDO.SetDistant)),
                    AccessTools.Method(typeof(ZDO), nameof(ZDO.Load)) })
                    patches.Patch(method, prefix: watch, postfix: check);
                patches.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), prefix: new HarmonyMethod(typeof(DistantSectorLists), nameof(Waking)),
                    postfix: new HarmonyMethod(typeof(DistantSectorLists), nameof(Woken)));
                patches.Patch(AccessTools.Method(typeof(ZDOMan), "Load"), postfix: new HarmonyMethod(typeof(DistantSectorLists), nameof(Clear)));
                patches.Patch(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.ShutDown)), postfix: new HarmonyMethod(typeof(DistantSectorLists), nameof(Clear)));
                Running = true;
            }
            catch (Exception error)
            {
                Failure = error.ToString();
                Stop();
                throw;
            }
        }

        internal static void Stop()
        {
            patches.UnpatchOwner();
            entries.Clear();
            Running = false;
        }

        private static void Clear() => entries.Clear();

        private static void Before(ZDO __instance, out bool __state) => __state = __instance.Distant;

        private static void After(ZDO __instance, bool __state)
        {
            if (__instance.Distant != __state) Forget(__instance);
        }

        private static void Waking(out ZDO __state) => __state = ZNetView.m_useInitZDO ? ZNetView.m_initZDO : null;

        private static void Woken(ZNetView __instance, ZDO __state)
        {
            // The view sets its record's flag from its prefab; any record it took or made is checked.
            var record = __instance.GetZDO() ?? __state;
            if (record != null) Forget(record);
        }

        private static void Forget(ZDO record)
        {
            if (entries.TryGetValue(ZoneSystem.SectorToIndex(record.GetSector()).Sector, out var entry)) entry.Valid = false;
        }

        private static bool Find(ZDOMan __instance, Vector2s sector, List<ZDO> objects, HashSet<ZoneSystem.SectorIndex> visitedSectorIndices)
        {
            if (!Keeping) return true;
            var index = ZoneSystem.SectorToIndex(sector);
            if (visitedSectorIndices.Contains(index)) return false;
            var list = sectorsOf(__instance)[index.Sector];
            if (list == null) return false;
            if (!entries.TryGetValue(index.Sector, out var entry)) entries.Add(index.Sector, entry = new Entry());
            int version = versionOf(list);
            if (entry.Valid && ReferenceEquals(entry.Source, list) && entry.Version == version) Kept++;
            else
            {
                Walked++;
                entry.Distant.Clear();
                foreach (var record in list)
                    if (record.Distant) entry.Distant.Add(record);
                entry.Source = list;
                entry.Version = version;
                entry.Valid = true;
            }
            objects.AddRange(entry.Distant);
            visitedSectorIndices.Add(index);
            return false;
        }
    }
}
