using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Logging out leaves the world's objects to the collector, but anything that still refers to one of them keeps the whole
    // world: a destroyed character's last ground tile holds its terrain build, the build holds the world generator, and the
    // generator holds the world with its biome map, about 85 MB of biome sectors, biome points and heights. The terrain
    // builder alone kept up to 192 finished builds of the last world, and stale references in the game and in other mods kept
    // a different world each, so in one game process every session started larger than the last, and the asset unload and
    // every collection took longer. A destroyed terrain tile now lets go of its build and of the heights terrain mesh reuse
    // kept for it, which nothing reads again. When a client's world ends, the builder forgets that world's queued and
    // finished builds, and once its thread has finished the one it may be building, the world's biome map, sectors and points
    // and its generator's rivers and lakes go, and a tile still loaded at the start menu no longer names the generator in its
    // build, which it rebuilds for any other generator anyway; nothing reads them after the world is left, and the next world
    // builds its own. At the start menu the world, record manager and routed calls the network left behind are set back to
    // what they were before the first join, this mod's features let go of what they kept of the world, and so do other mods'
    // and the game's static fields that ModWorldLeftovers names.
    internal static class WorldTeardown
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".world-teardown");
        private static readonly AccessTools.FieldRef<HeightmapBuilder> builderInstance = AccessTools.StaticFieldRefAccess<HeightmapBuilder>(AccessTools.Field(typeof(HeightmapBuilder), "m_instance"));
        private static readonly AccessTools.FieldRef<HeightmapBuilder, object> builderLock = AccessTools.FieldRefAccess<HeightmapBuilder, object>("m_lock");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> toBuild = AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_toBuild");
        private static readonly AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> ready = AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_ready");
        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> tileBuild = AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static readonly AccessTools.FieldRef<WorldGenerator, List<Vector2>> lakes = AccessTools.FieldRefAccess<WorldGenerator, List<Vector2>>("m_lakes");
        private static readonly AccessTools.FieldRef<World> networkWorld = AccessTools.StaticFieldRefAccess<World>(AccessTools.Field(typeof(ZNet), "m_world"));
        private static readonly AccessTools.FieldRef<ZDOMan> records = AccessTools.StaticFieldRefAccess<ZDOMan>(AccessTools.Field(typeof(ZDOMan), "s_instance"));
        private static readonly AccessTools.FieldRef<ZRoutedRpc> routedCalls = AccessTools.StaticFieldRefAccess<ZRoutedRpc>(AccessTools.Field(typeof(ZRoutedRpc), "s_instance"));
        // Worlds left and not yet released, with their generators.
        private static readonly List<(World world, WorldGenerator generator)> departed = new List<(World, WorldGenerator)>();
        internal static bool Running { get; private set; }
        // Set when a world ends, until the start menu is up.
        private static bool menuDue;
        // Off keeps every world's data for the collector to find unreachable, as natively; the isolated client switches it to
        // compare.
        internal static bool Releasing { get; set; } = true;
        internal static int Released { get; private set; }
        internal static int BuildsDropped { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            patches.Patch(AccessTools.Method(typeof(ZNet), "OnDestroy"), postfix: new HarmonyMethod(typeof(WorldTeardown), nameof(Left)));
            patches.Patch(AccessTools.Method(typeof(Heightmap), "OnDestroy"), postfix: new HarmonyMethod(typeof(WorldTeardown), nameof(TileDestroyed)));
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchOwner();
            departed.Clear();
        }

        // A client's world ends with its network object, before the start menu's generator replaces the world's.
        private static void Left(ZNet __instance)
        {
            if (!Releasing || ZNet.instance != null) return;
            menuDue = true;
            if (__instance.IsServer()) return;
            var generator = WorldGenerator.instance;
            var world = ZNet.World;
            if (generator != null && generator.m_world != null && !generator.m_world.m_menu) departed.Add((generator.m_world, generator));
            if (world != null && !world.m_menu && (generator == null || generator.m_world != world)) departed.Add((world, null));
        }

        // Each frame while a world waits to be released: drops builds of generators other than the current one and releases the
        // world once its builds are gone from the builder's queue.
        // A destroyed tile is never built or read again; its build holds the world's generator.
        private static void TileDestroyed(Heightmap __instance)
        {
            if (!Releasing) return;
            tileBuild(__instance) = null;
            TerrainMeshReuse.Forget(__instance);
        }

        internal static void LateUpdate()
        {
            // The world's objects are all destroyed once the start menu is up.
            if (menuDue && Releasing && FejdStartup.instance != null && ZNet.instance == null)
            {
                menuDue = false;
                Forget();
            }
            if (departed.Count == 0) return;
            var current = WorldGenerator.instance;
            for (int index = departed.Count - 1; index >= 0; index--)
            {
                var (world, generator) = departed[index];
                if (current != null && (ReferenceEquals(current, generator) || ReferenceEquals(current.m_world, world))) continue;
                if (generator != null && !DropBuilds(generator)) continue;
                Release(world, generator);
                departed.RemoveAt(index);
            }
        }

        // True once no build of the generator is queued: the builder's thread takes the first queued build, builds it outside its
        // lock and only then moves it to the finished ones, so the first is left to finish and the rest are removed.
        private static bool DropBuilds(WorldGenerator generator)
        {
            var builder = builderInstance();
            if (builder == null) return true;
            var gate = builderLock(builder);
            if (gate == null) return true;
            lock (gate)
            {
                var queued = toBuild(builder);
                bool building = queued.Count > 0 && ReferenceEquals(queued[0].m_worldGen, generator);
                for (int index = queued.Count - 1; index >= (building ? 1 : 0); index--)
                    if (ReferenceEquals(queued[index].m_worldGen, generator)) { queued.RemoveAt(index); BuildsDropped++; }
                BuildsDropped += ready(builder).RemoveAll(data => ReferenceEquals(data.m_worldGen, generator));
                return !building;
            }
        }

        private static void Release(World world, WorldGenerator generator)
        {
            if (generator != null)
            {
                generator.CleanCachedRiverData();
                lakes(generator) = null;
            }
            var data = world.m_biomeData;
            world.m_biomeData = null;
            if (data != null)
            {
                foreach (var info in data.Biomes.Values)
                {
                    info.Sectors = new List<BiomeSector>();
                    info.AllPoints = new List<BiomePointCoordinate>();
                    info.AllPointsAboveSeaLevel = new List<BiomePointCoordinate>();
                }
                foreach (var sector in data.Sectors) sector.Neighbors = new List<BiomeSector>();
                data.Sectors = new List<BiomeSector>();
                data.PointHeights = null;
                data.PointBiomes = null;
                data.PointSectors = null;
            }
            if (generator != null)
                foreach (var tile in UnityEngine.Object.FindObjectsByType<Heightmap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var build = tileBuild(tile);
                    if (build != null && ReferenceEquals(build.m_worldGen, generator)) build.m_worldGen = null;
                }
            // The network's world, record manager and routed calls stay set after the world ends until the next join replaces
            // them; the start menu had none before the first join.
            if (ZNet.instance == null)
            {
                if (ReferenceEquals(networkWorld(), world)) networkWorld() = null;
                records() = null;
                routedCalls() = null;
            }
            Released++;
        }

        // What this mod's own features kept of the world.
        private static void Forget()
        {
            RestingCreatures.ForgetWorld();
            TerrainRebuilds.ForgetWorld();
            StaticGround.ForgetWorld();
            LoadingPassThrottle.ForgetWorld();
            UnseenEffects.ForgetWorld();
            CharacterStepTrims.ForgetWorld();
            SharedWaterTime.ForgetWorld();
            GrassGround.ForgetWorld();
            RestingFloaters.ForgetWorld();
            ComfortPanelRefresh.ForgetWorld();
            DamageNumberReuse.ForgetWorld();
            InventoryTooltips.ForgetWorld();
            SharedParticleMeshes.ForgetWorld();
            UnusedParticleMeshes.ForgetWorld();
            SpatialBuildings.ForgetWorld();
            IdleEffects.ForgetWorld();
            RestingDoors.ForgetWorld();
            StatusEffectParts.ForgetWorld();
            ModWorldLeftovers.ForgetWorld();
        }
    }
}
