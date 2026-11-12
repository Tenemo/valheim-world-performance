using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // When a location or dungeon spawns, its ground snappers place objects on the terrain with raycasts, so the game first
    // rebuilds in full every heightmap waiting for its late rebuild: the heights with the ground's edits, the collision mesh
    // the raycasts and physics hit, and the render mesh with its biome colors, normals and tangents, almost half of each
    // heightmap's 3.5–5 ms. Under the loading screen that costs nothing a player sees, but a spawn while traveling took 20 ms
    // in one frame in the hidden client. In play the mod lets those rebuilds skip the render mesh, which only the camera
    // reads, and rebuilds the waiting render meshes with the game's own code one a frame from the next frame; a full rebuild
    // meanwhile ends the wait, and until then the camera shows the heightmap's previous render mesh. The heights and
    // collision meshes, and so every snapped position, stay the game's own.
    internal static class SnappingRenderMeshes
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".snapping-render-meshes");
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> renderMeshOf = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly Action<Heightmap> rebuildRenderMesh = AccessTools.MethodDelegate<Action<Heightmap>>(AccessTools.Method(typeof(Heightmap), "RebuildRenderMesh"));
        // Heightmaps whose render mesh waits, with the frame it was held back in.
        private static readonly List<(Heightmap map, int frame)> waiting = new List<(Heightmap, int)>();
        private static bool snapping, rebuilding;
        internal static bool Running { get; private set; }
        // Off rebuilds render meshes at once, as natively; the isolated client switches it for its checks and paired measurements.
        internal static bool Holding { get; set; } = true;
        internal static int Waiting => waiting.Count;
        internal static long Held { get; private set; }
        internal static long Rebuilt { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(SnapToGround), nameof(SnapToGround.SnappAll)), prefix: new HarmonyMethod(typeof(SnappingRenderMeshes), nameof(Snapping)),
                finalizer: new HarmonyMethod(typeof(SnappingRenderMeshes), nameof(Snapped)));
            patches.Patch(AccessTools.Method(typeof(Heightmap), "RebuildRenderMesh"), prefix: new HarmonyMethod(typeof(SnappingRenderMeshes), nameof(Hold)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            while (waiting.Count > 0) RebuildFirst();
            Running = false;
            patches.UnpatchSelf();
        }

        private static void Snapping() => snapping = true;

        private static Exception Snapped(Exception __exception)
        {
            snapping = false;
            return __exception;
        }

        // A render mesh that a ground snapping's forced build rebuilds in play waits, where the heightmap already has one; any
        // other rebuild runs and ends the heightmap's wait.
        private static bool Hold(Heightmap __instance)
        {
            if (!rebuilding && snapping && Holding && renderMeshOf(__instance) && !DeferredAssetUnload.NaturalPause())
            {
                if (waiting.FindIndex(entry => entry.map == __instance) < 0) waiting.Add((__instance, Time.frameCount));
                Held++;
                return false;
            }
            int index = waiting.FindIndex(entry => entry.map == __instance);
            if (index >= 0) waiting.RemoveAt(index);
            return true;
        }

        // Rebuilds the first waiting render mesh held back in an earlier frame.
        internal static void LateUpdate()
        {
            while (waiting.Count > 0 && !waiting[0].map) waiting.RemoveAt(0);
            if (waiting.Count > 0 && waiting[0].frame < Time.frameCount) RebuildFirst();
        }

        private static void RebuildFirst()
        {
            var map = waiting[0].map;
            waiting.RemoveAt(0);
            if (!map) return;
            rebuilding = true;
            try { rebuildRenderMesh(map); }
            catch (Exception error) { Plugin.Log.LogError(error); }
            finally { rebuilding = false; }
            Rebuilt++;
        }
    }
}
