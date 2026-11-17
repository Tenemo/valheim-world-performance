using System;
using System.Threading.Tasks;
using HarmonyLib;

namespace WorldPerformance
{
    // Every world load fills a 2,048 by 2,048 map of biomes and heights for the alternative biomes on one thread before the
    // game continues. The rows now fill on all cores with the same lookups, which the terrain builder's own thread already
    // makes alongside the main thread, so every point holds what the single thread would have written.
    [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints))]
    internal static class ParallelBiomePoints
    {
        // The isolated test client sets false to compare the single thread.
        internal static bool Parallel = true;
        private static readonly AccessTools.FieldRef<AltBiomeWorldData, World> owner = AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");

        private static bool Prefix(World world)
        {
            if (!Parallel || WorldGenerator.instance == null) return true;
            var data = new AltBiomeWorldData(2048);
            data.dt = DateTime.Now;
            var generator = WorldGenerator.instance;
            // A join of a world this game generated before reads the map back with the generator's rivers.
            if (!WorldGenerationCache.TryRestorePoints(world, data))
            {
                System.Threading.Tasks.Parallel.For(0, 2048, row =>
                {
                    float y = AltBiomeWorldData.MapSpaceToWorldSpace(row);
                    for (int column = 0; column < 2048; column++)
                    {
                        float x = AltBiomeWorldData.MapSpaceToWorldSpace(column);
                        if (x * x + y * y > 110250000f)
                        {
                            data.PointBiomes[column, row] = Heightmap.Biome.Ocean.ToBiomeIndex();
                            data.PointHeights[column, row] = -1000f;
                        }
                        else
                        {
                            var biome = generator.GetBiome(x, y);
                            data.PointBiomes[column, row] = biome.ToBiomeIndex();
                            data.PointHeights[column, row] = generator.GetBiomeHeight(biome, x, y, out _);
                        }
                    }
                });
                WorldGenerationCache.Generated(world, data);
            }
            data.PointsGenerated = true;
            world.m_biomeData = data;
            owner(data) = world;
            return false;
        }
    }
}
