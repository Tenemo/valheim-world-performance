using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Every full terrain build on the dedicated server, for each zone that loads around a player and each terrain edit, also
    // built the tile's render mesh with its biome colours, normals and tangents, which only a camera draws. The server now
    // builds the heights and the collision mesh its raycasts and physics use, and leaves the render mesh out.
    [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
    internal static class ServerTerrainMeshes
    {
        // The isolated test server's helper sets false to compare the native build.
        internal static bool Skipping = true;
        // A process keeps its graphics device for its lifetime.
        private static readonly bool drawing = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        [HarmonyPriority(Priority.First)]
        private static bool Prefix() => !Skipping || drawing;
    }
}
