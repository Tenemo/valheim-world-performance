using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // When the loaded area moves, Valheim destroys every instance that left it in the same pass: leaving the base at once
    // removes about 8,450 instances in one frame of 345-523 ms. A player's game now destroys at most 250 departed instances
    // a pass and leaves the rest for the next passes, which run up to 30 times a second, so that departure takes about a
    // second. Each pass finds the departed instances again, so one that comes back before its turn simply stays. Unity
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
            foreach (var view in loaded.Values)
            {
                if (view.GetZDO().TempRemoveEarmark == mark) continue;
                count++;
                if (departed.Count >= PerPass) continue;
                int size = view ? view.transform.hierarchyCount : 0;
                if (ObjectsPerPass > 0 && departed.Count > 0 && objects + size > ObjectsPerPass) continue;
                departed.Add(view);
                objects += size;
            }
            Pending = count - departed.Count;
            foreach (var view in departed)
            {
                var data = view.GetZDO();
                view.ResetZDO();
                Object.Destroy(view.gameObject);
                if (!data.Persistent && data.IsOwner()) ZDOMan.instance.DestroyZDO(data);
                loaded.Remove(data);
            }
            return false;
        }
    }
}
