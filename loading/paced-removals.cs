using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // When the loaded area moves, Valheim destroys every instance that left it in the same pass: leaving the base at once
    // removes about 8,450 instances in one frame of 345-523 ms. A player's game now destroys at most 250 departed instances
    // a pass and leaves the rest for the next passes, which run up to 30 times a second, so that departure takes about a
    // second. Each pass finds the departed instances again, so one that comes back before its turn simply stays. An
    // instance can be gone before its turn, destroyed with a parent an earlier pass removed or by a mod that bypassed the
    // scene; its entry leaves the table without it, and every departed entry leaves before its instance is touched, so
    // one such instance can no longer make every later pass throw and stop all removals for the rest of the session. Unity
    // destroys them in the next frame, about 1 ms for every thousand objects in their hierarchies, and a location's scenery
    // or a dungeon can hold thousands: while travelling, 250 instances carried up to 23,000 objects, a following frame of
    // 16-32 ms each time the loaded area moved on. A pass therefore takes only instances that keep it within 2,000 objects,
    // besides its first. Teleports, respawns and the first load keep the native single pass behind the loading
    // screen, and the dedicated server keeps it.
    // Most passes find nothing departed. A record is marked created only as its instance joins the scene's table, and every
    // removal from the table, the game's and the pack's mods', first clears that mark through ZNetView.ResetZDO, so every
    // created record the pass marks has a loaded instance. When as many are marked as instances are loaded, every instance
    // stays and the pass skips its walk over them, 0.3-0.6 ms at a large base; an instance whose record lost its mark could
    // only make the count fall short, and the walk runs.
    // An instance destroyed without the scene, by a mod or with an object it was attached to, used to keep its entry while
    // its record stayed in range: the scene counted that record as created and never made it again, and ZNetScene.IsAreaReady
    // stayed false around it, so a short teleport there, as through a dungeon door, held its loading screen until the player
    // left the world. As it is destroyed, its entry now leaves the table the way the scene's own removal takes one, so a
    // record still in range is made again, and the log names its prefab once a session.
    [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
    internal static class PacedRemovals
    {
        // The isolated test client sets 0 to compare the native single pass.
        internal static int PerPass = 250;
        // The isolated test client sets 0 to leave the passes unbounded by their objects.
        internal static int ObjectsPerPass = 2000;
        // The isolated test client sets false to compare walking the instances on every pass.
        internal static bool SkipFullPasses = true;
        internal static int Pending { get; private set; }
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> instances = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        private static readonly AccessTools.FieldRef<ZNetScene, List<ZNetView>> removed = AccessTools.FieldRefAccess<ZNetScene, List<ZNetView>>("m_tempRemoved");
        private static readonly List<ZDO> departedRecords = new List<ZDO>(), gone = new List<ZDO>();

        private static readonly int morkhalla = "DG_MorkHalla".GetStableHashCode();

        // Last, so other mods' prefixes have added what they keep to the lists.
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            var player = Player.m_localPlayer;
            if (PerPass <= 0 || ZNet.instance == null || ZNet.instance.IsDedicated() || player == null || player.IsTeleporting())
            {
                Pending = 0;
                return true;
            }
            byte mark = (byte)(Time.frameCount & 0xFF);
            // Each created record is counted once, the first time it is marked; one that already held this mark is not
            // counted, which can only make the count fall short.
            int staying = 0;
            foreach (var data in currentNearObjects)
            {
                if (data.TempRemoveEarmark != mark && data.Created) staying++;
                data.TempRemoveEarmark = mark;
            }
            foreach (var data in currentDistantObjects)
            {
                if (data.TempRemoveEarmark != mark && data.Created) staying++;
                data.TempRemoveEarmark = mark;
            }
            var loaded = instances(__instance);
            var departed = removed(__instance);
            departed.Clear();
            if (staying == loaded.Count && SkipFullPasses)
            {
                Pending = 0;
                return false;
            }
            int count = 0, objects = 0;
            departedRecords.Clear();
            gone.Clear();
            foreach (var entry in loaded)
            {
                if (entry.Key.TempRemoveEarmark == mark) continue;
                var view = entry.Value;
                // Its ZNetView.OnDestroy has already taken it out of the spatial renderer, which reads the departed list.
                if (!view) { gone.Add(entry.Key); continue; }
                count++;
                if (departed.Count >= PerPass) continue;
                int size = view.transform.hierarchyCount;
                if (ObjectsPerPass > 0 && departed.Count > 0 && objects + size > ObjectsPerPass) continue;
                departed.Add(view);
                departedRecords.Add(entry.Key);
                objects += size;
            }
            Pending = count - departed.Count;
            foreach (var data in gone)
            {
                loaded.Remove(data);
                data.Created = false;
                Release(data);
            }
            for (int i = 0; i < departed.Count; i++)
            {
                var data = departedRecords[i];
                loaded.Remove(data);
                departed[i].ResetZDO();
                if (data.GetPrefab() == morkhalla)
                    DungeonRoomDisposal.Retire(__instance, departed[i].transform);
                Object.Destroy(departed[i].gameObject);
                Release(data);
            }
            return false;
        }

        private static void Release(ZDO data)
        {
            if (!data.Persistent && data.IsOwner()) ZDOMan.instance.DestroyZDO(data);
        }

        [HarmonyPatch(typeof(ZNetView), "OnDestroy")]
        private static class DestroyedOutside
        {
            private static readonly HashSet<int> reported = new HashSet<int>();

            // The scene's own removals and its shutdown clear the record first, so only an instance destroyed without the
            // scene still holds one here.
            private static void Prefix(ZNetView __instance)
            {
                var data = __instance.GetZDO();
                var scene = ZNetScene.instance;
                if (data == null || scene == null || ZDOMan.instance == null) return;
                var loaded = instances(scene);
                if (!loaded.TryGetValue(data, out var view) || !ReferenceEquals(view, __instance)) return;
                loaded.Remove(data);
                data.Created = false;
                int prefab = data.GetPrefab();
                Release(data);
                if (reported.Add(prefab))
                    Plugin.Log.LogWarning((scene.GetPrefab(prefab)?.name ?? __instance.name) + " was destroyed without the scene; its entry left the scene's table.");
            }
        }
    }
}
