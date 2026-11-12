using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // When the loaded area moves, Valheim destroys every instance that left it in the same pass: leaving the base at once
    // removes about 8,450 instances in one frame of 345-523 ms. A player's game now destroys at most 250 departed instances
    // a pass and leaves the rest for the next passes, which run up to 30 times a second, so that departure takes about a
    // second. Each pass finds the departed instances again, so one that comes back before its turn simply stays. Teleports,
    // respawns and the first load keep the native single pass behind the loading screen, and the dedicated server keeps it.
    [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
    internal static class PacedRemovals
    {
        // The isolated test client sets 0 to compare the native single pass.
        internal static int PerPass = 250;
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
            foreach (var data in currentNearObjects) data.TempRemoveEarmark = mark;
            foreach (var data in currentDistantObjects) data.TempRemoveEarmark = mark;
            var loaded = instances(__instance);
            var departed = removed(__instance);
            departed.Clear();
            int count = 0;
            foreach (var view in loaded.Values)
                if (view.GetZDO().TempRemoveEarmark != mark && count++ < PerPass) departed.Add(view);
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
