using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The distant terrain is nine regions that move together whenever the camera is 256 m from where they were last built:
    // once every region's heights are ready, the game rebuilds all nine in one frame, 17–25 ms in the hidden client, about
    // every 25 s at running speed and more often by ship. In play the mod builds each region's next version into a hidden
    // spare region instead, with the game's own rebuild, one a frame, and shows all nine spares and hides the old regions in
    // one frame once the last is built; the old regions become the next spares. The regions appear as the game builds them,
    // a few frames later. Under the loading screen and with the game menu open, the game rebuilds them at once as natively.
    internal static class StagedDistantTerrain
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".staged-distant-terrain");
        private static readonly AccessTools.FieldRef<TerrainLod, IList> regionsOf = AccessTools.FieldRefAccess<TerrainLod, IList>("m_heightmaps");
        private static readonly AccessTools.FieldRef<TerrainLod, Vector3> lastPointOf = AccessTools.FieldRefAccess<TerrainLod, Vector3>("m_lastPoint");
        private static readonly AccessTools.FieldRef<TerrainLod, Material> materialOf = AccessTools.FieldRefAccess<TerrainLod, Material>("m_material");
        private static readonly System.Reflection.FieldInfo stateOf = AccessTools.Field(typeof(TerrainLod), "m_heightmapState");
        private static readonly Type regionType = AccessTools.Inner(typeof(TerrainLod), "HeightmapWithOffset");
        private static readonly System.Reflection.FieldInfo mapOf = AccessTools.Field(regionType, "m_heightmap");
        private static readonly System.Reflection.FieldInfo offsetOf = AccessTools.Field(regionType, "m_offset");
        private static readonly System.Reflection.FieldInfo regionStateOf = AccessTools.Field(regionType, "m_state");
        private static readonly object done = Enum.Parse(AccessTools.Inner(typeof(TerrainLod), "HeightmapState"), "Done");
        private static readonly Dictionary<TerrainLod, SpareRegions> stagings = new Dictionary<TerrainLod, SpareRegions>();
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        // Off rebuilds every region at once, as natively; the isolated client switches it for its checks and paired measurements.
        internal static bool Staging { get; set; } = true;
        internal static long Staged { get; private set; }
        internal static long Shown { get; private set; }

        private sealed class SpareRegions
        {
            // One spare per region, in the regions' order, and how many are built for the current point.
            internal readonly List<Heightmap> Spares = new List<Heightmap>();
            internal Vector3 Point;
            internal int Built;
        }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            try
            {
                if (regionType == null || mapOf == null || offsetOf == null || regionStateOf == null || stateOf == null)
                    throw new InvalidOperationException("Valheim's distant terrain changed.");
                patches.Patch(AccessTools.Method(typeof(TerrainLod), "RebuildAllHeightmaps"), prefix: new HarmonyMethod(typeof(StagedDistantTerrain), nameof(Rebuild)));
                patches.Patch(AccessTools.Method(typeof(TerrainLod), "ResetMeshes"), prefix: new HarmonyMethod(typeof(StagedDistantTerrain), nameof(Reset)));
            }
            catch (Exception error)
            {
                Failure = error.Message;
                Running = false;
                patches.UnpatchSelf();
                Plugin.Log.LogWarning("Distant terrain rebuilds stay native: " + error.Message);
            }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            foreach (var staging in stagings.Values) Discard(staging);
            stagings.Clear();
        }

        // TerrainLod calls this once every region's heights are ready, and again every frame until its state is done.
        private static bool Rebuild(TerrainLod __instance)
        {
            var regions = regionsOf(__instance);
            if (!Staging || DeferredAssetUnload.NaturalPause())
            {
                // A native rebuild leaves spares for an earlier point unused; they are rebuilt from the start next time.
                if (stagings.TryGetValue(__instance, out var stale)) stale.Built = 0;
                return true;
            }
            if (!stagings.TryGetValue(__instance, out var staging)) stagings[__instance] = staging = new SpareRegions();
            var point = lastPointOf(__instance);
            if (staging.Point != point || staging.Spares.Count != regions.Count)
            {
                staging.Point = point;
                staging.Built = 0;
            }
            while (staging.Spares.Count < regions.Count) staging.Spares.Add(Spare(__instance, (Heightmap)mapOf.GetValue(regions[staging.Spares.Count])));
            if (staging.Built < regions.Count)
            {
                // TerrainLod.RebuildHeightmap on the spare: the region's place for the current point, then the game's full rebuild.
                var spare = staging.Spares[staging.Built];
                spare.transform.position = point + (Vector3)offsetOf.GetValue(regions[staging.Built]);
                spare.Regenerate();
                staging.Built++;
                Staged++;
                return false;
            }
            for (int index = 0; index < regions.Count; index++)
            {
                var region = regions[index];
                var shown = (Heightmap)mapOf.GetValue(region);
                var spare = staging.Spares[index];
                Renderer(spare).forceRenderingOff = false;
                Renderer(shown).forceRenderingOff = true;
                mapOf.SetValue(region, spare);
                staging.Spares[index] = shown;
                regionStateOf.SetValue(region, done);
            }
            stateOf.SetValue(__instance, done);
            staging.Built = 0;
            Shown++;
            return false;
        }

        private static MeshRenderer Renderer(Heightmap map) => map.GetComponent<MeshRenderer>();

        // TerrainLod.CreateMesh for a hidden region beside the shown one.
        private static Heightmap Spare(TerrainLod lod, Heightmap shown)
        {
            var spare = new GameObject("lodMesh");
            spare.transform.position = shown.transform.position;
            spare.transform.SetParent(lod.transform);
            var map = spare.AddComponent<Heightmap>();
            map.m_scale = shown.m_scale;
            map.m_width = shown.m_width;
            map.m_material = materialOf(lod);
            map.IsDistantLod = true;
            map.enabled = true;
            Renderer(map).forceRenderingOff = true;
            return map;
        }

        // TerrainLod.ResetMeshes destroys the shown regions, also when the distant terrain is disabled as a world closes; their
        // spares go with them.
        private static void Reset(TerrainLod __instance)
        {
            if (!stagings.TryGetValue(__instance, out var staging)) return;
            Discard(staging);
            stagings.Remove(__instance);
        }

        private static void Discard(SpareRegions staging)
        {
            foreach (var spare in staging.Spares)
                if (spare) UnityEngine.Object.Destroy(spare.gameObject);
            staging.Spares.Clear();
            staging.Built = 0;
        }
    }
}
