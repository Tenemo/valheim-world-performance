using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Floating objects remember which water volume or liquid surface each collider they touched belongs to, by the collider's
    // instance id, and never forget one. Each remembered water volume keeps its zone's whole heightmap, with its height and
    // paint arrays, after the zone has unloaded: in a soak through four bases the cache held 97 MB after 48 minutes and kept
    // growing with every zone loaded again. A volume's entry now goes with the volume, and once the caches have doubled since
    // the last pass, entries whose object is gone are dropped. Instance ids are never reused while the game runs, so a
    // dropped entry is only looked up again for a collider that is still there and is then found as natively.
    internal static class FloatingWaterCache
    {
        private static IDictionary volumes, surfaces;
        private static int kept;

        internal static void Initialize(PerformancePatches harmony)
        {
            volumes = AccessTools.Field(typeof(Floating), "s_waterVolumeCache")?.GetValue(null) as Dictionary<int, WaterVolume>;
            surfaces = AccessTools.Field(typeof(Floating), "s_liquidSurfaceCache")?.GetValue(null) as Dictionary<int, LiquidSurface>;
            if (volumes == null || surfaces == null) throw new System.InvalidOperationException("Floating's water caches changed.");
            harmony.Patch(AccessTools.Method(typeof(WaterVolume), "OnDestroy"), postfix: new HarmonyMethod(typeof(FloatingWaterCache), nameof(Forget)));
        }

        private static void Forget(WaterVolume __instance)
        {
            foreach (var collider in __instance.GetComponents<Collider>()) volumes.Remove(collider.GetInstanceID());
            if (volumes.Count + surfaces.Count <= 2 * kept + 256) return;
            Sweep((Dictionary<int, WaterVolume>)volumes);
            Sweep((Dictionary<int, LiquidSurface>)surfaces);
            kept = volumes.Count + surfaces.Count;
        }

        // Drops entries whose component is gone; an entry for a collider with no such component holds null and is dropped too.
        private static void Sweep<T>(Dictionary<int, T> cache) where T : Object
        {
            List<int> gone = null;
            foreach (var entry in cache)
                if (!entry.Value) (gone ?? (gone = new List<int>())).Add(entry.Key);
            if (gone != null) foreach (int id in gone) cache.Remove(id);
        }
    }
}
