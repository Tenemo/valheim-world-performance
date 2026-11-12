using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Valheim refreshes the objects around a player up to 30 times a second, and each pass checks every record in the
    // loaded area: about 20,000 at a busy trader. While a player stands still, consecutive passes find the same objects.
    // A player's game now skips a pass for at most a quarter of a second while the previous pass created nothing, the
    // player is in the same zone within 4 m of where it ran, no record has arrived or gone, no zone has loaded, none
    // is still creating its objects and no departed instance waits to be destroyed. Moving farther or into another zone,
    // records arriving or going and zones loading keep the native rate. The dedicated server keeps every pass.
    [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
    internal static class LoadingPassThrottle
    {
        private const float MaxSkipSeconds = .25f;
        private const float MaxMove = 4f;
        private static ZoneSystem zoneSystem;
        private static ICollection zones, loadingZones;
        private static float lastPass = float.NegativeInfinity;
        private static Vector3 lastPosition;
        private static Vector2s lastZone;
        private static int lastRecords, lastZones;
        private static bool created;

        [HarmonyPriority(Priority.First)]
        private static bool Prefix()
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated() || ZoneSystem.instance == null || ZDOMan.instance == null) return true;
            if (zoneSystem != ZoneSystem.instance)
            {
                zoneSystem = ZoneSystem.instance;
                zones = (ICollection)AccessTools.Field(typeof(ZoneSystem), "m_zones").GetValue(zoneSystem);
                loadingZones = (ICollection)AccessTools.Field(typeof(ZoneSystem), "m_loadingObjectsInZones").GetValue(zoneSystem);
            }
            Vector3 position = ZNet.instance.GetReferencePosition();
            var zone = ZoneSystem.GetZone(position);
            int records = ZDOMan.instance.NrOfObjects(), loaded = zones.Count;
            float now = Time.time;
            if (!created && now - lastPass < MaxSkipSeconds && zone == lastZone && (position - lastPosition).sqrMagnitude < MaxMove * MaxMove &&
                records == lastRecords && loaded == lastZones && loadingZones.Count == 0 && PacedRemovals.Pending == 0)
                return false;
            created = false;
            lastPass = now;
            lastPosition = position;
            lastZone = zone;
            lastRecords = records;
            lastZones = loaded;
            return true;
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        private static class Creation
        {
            private static void Postfix(GameObject __result)
            {
                if (__result != null) created = true;
            }
        }
    }
}
