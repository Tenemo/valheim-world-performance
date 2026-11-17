using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A player's game generates the server's world when it joins: the world generator places the lakes, rivers and
    // streams, 2.2-2.9 s on the main thread in the hidden test client, and the biome map fills 2,048 by 2,048 biomes and
    // heights, 1.0-1.4 s on all cores, both in one frozen frame on every join. The client now keeps what both produced
    // under BepInEx/cache, one file per world identity, seed, generator version, game version and build, and a
    // later join of the same world reads it back instead: the same lakes, rivers, streams, river cells in their order,
    // the generator's one-cell river lookup as generation left it, and every biome and height bit for bit. The generator
    // still draws its offsets and seeds itself, and a file whose stored draws differ is ignored. Building the biome
    // sectors and the alternative biomes stays native. Hosts, single-player worlds and worlds another mod's patches shape
    // generate as before.
    internal static class WorldGenerationCache
    {
        // The isolated test client sets false to compare native generation.
        internal static bool Caching = true;
        internal static int Restored, Written;
        private const int Format = 1;
        private const int Magic = 0x47575057, End = 0x444E4557;
        private static readonly AccessTools.FieldRef<WorldGenerator, float> offset0 = AccessTools.FieldRefAccess<WorldGenerator, float>("m_offset0");
        private static readonly AccessTools.FieldRef<WorldGenerator, float> offset1 = AccessTools.FieldRefAccess<WorldGenerator, float>("m_offset1");
        private static readonly AccessTools.FieldRef<WorldGenerator, float> offset2 = AccessTools.FieldRefAccess<WorldGenerator, float>("m_offset2");
        private static readonly AccessTools.FieldRef<WorldGenerator, float> offset3 = AccessTools.FieldRefAccess<WorldGenerator, float>("m_offset3");
        private static readonly AccessTools.FieldRef<WorldGenerator, float> offset4 = AccessTools.FieldRefAccess<WorldGenerator, float>("m_offset4");
        private static readonly AccessTools.FieldRef<WorldGenerator, int> riverSeed = AccessTools.FieldRefAccess<WorldGenerator, int>("m_riverSeed");
        private static readonly AccessTools.FieldRef<WorldGenerator, int> streamSeed = AccessTools.FieldRefAccess<WorldGenerator, int>("m_streamSeed");
        private static readonly AccessTools.FieldRef<WorldGenerator, List<Vector2>> lakes = AccessTools.FieldRefAccess<WorldGenerator, List<Vector2>>("m_lakes");
        private static readonly AccessTools.FieldRef<WorldGenerator, List<WorldGenerator.River>> rivers = AccessTools.FieldRefAccess<WorldGenerator, List<WorldGenerator.River>>("m_rivers");
        private static readonly AccessTools.FieldRef<WorldGenerator, List<WorldGenerator.River>> streams = AccessTools.FieldRefAccess<WorldGenerator, List<WorldGenerator.River>>("m_streams");
        private static readonly AccessTools.FieldRef<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>> riverPoints =
            AccessTools.FieldRefAccess<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>>("m_riverPoints");
        private static readonly AccessTools.FieldRef<WorldGenerator, WorldGenerator.RiverPoint[]> cachedPoints = AccessTools.FieldRefAccess<WorldGenerator, WorldGenerator.RiverPoint[]>("m_cachedRiverPoints");
        private static readonly AccessTools.FieldRef<WorldGenerator, Vector2i> cachedGrid = AccessTools.FieldRefAccess<WorldGenerator, Vector2i>("m_cachedRiverGrid");
        // The generator's river state read from a file, waiting for the biome map of the same world.
        private static World restoredWorld;
        private static byte[] restoredHeights, restoredBiomes;
        // The generator's state of a fresh generation, written with the biome map once that is filled.
        private static Snapshot pending;
        private static string foreignOwners;

        private sealed class Snapshot
        {
            internal World World;
            internal string Path;
            internal float[] Offsets;
            internal int RiverSeed, StreamSeed;
            internal Vector2[] Lakes;
            internal WorldGenerator.River[] Rivers, Streams;
            internal Vector2i[] Cells;
            internal WorldGenerator.RiverPoint[][] Points;
            internal Vector2i CachedGrid;
            internal WorldGenerator.RiverPoint[] CachedPoints;
            internal bool CachedIsCell;
        }

        private static bool Applies(World world) =>
            Caching && world != null && !world.m_menu && ZNet.instance != null && !ZNet.instance.IsServer() && ForeignOwners() == "";

        // Another mod's patch on the generation could make the same key produce a different world.
        private static string ForeignOwners()
        {
            if (foreignOwners != null) return foreignOwners;
            var owners = new SortedSet<string>();
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                bool generation = method.DeclaringType == typeof(WorldGenerator) && method.Name != ".ctor" && method.Name != nameof(WorldGenerator.Initialize) ||
                    method.DeclaringType == typeof(AltBiomeWorldData) && method.Name == nameof(AltBiomeWorldData.GenerateBiomePoints);
                if (!generation) continue;
                var info = Harmony.GetPatchInfo(method);
                foreach (var owner in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Select(patch => patch.owner))
                    if (!owner.StartsWith(Plugin.Id, StringComparison.Ordinal)) owners.Add(owner + " (" + method.DeclaringType.Name + "." + method.Name + ")");
            }
            foreignOwners = string.Join(", ", owners);
            if (foreignOwners != "") Plugin.Fallback("the world generation is not kept, since other patches change it: " + foreignOwners);
            return foreignOwners;
        }

        private static string FilePath(World world)
        {
            string key = string.Join("_", world.m_uid, world.m_seed, world.m_worldGenVersion, global::Version.CurrentVersion,
                typeof(WorldGenerator).Module.ModuleVersionId.ToString("N"), Application.unityVersion, Format);
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars()) key = key.Replace(invalid, '-');
            return System.IO.Path.Combine(Paths.CachePath, "WorldPerformance", "world-generation", key + ".bin");
        }

        [HarmonyPatch(typeof(WorldGenerator), "Pregenerate")]
        private static class Pregeneration
        {
            private static bool Prefix(WorldGenerator __instance, out bool __state)
            {
                __state = false;
                restoredWorld = null;
                restoredHeights = restoredBiomes = null;
                pending = null;
                var world = __instance.m_world;
                if (!Applies(world)) return true;
                string path = FilePath(world);
                if (File.Exists(path) && Restore(__instance, path)) return false;
                __state = true;
                return true;
            }

            // The river cells are taken now, since the next world's generator clears this one's collections in place.
            private static void Postfix(WorldGenerator __instance, bool __state)
            {
                if (!__state) return;
                var cells = riverPoints(__instance);
                var grid = cachedGrid(__instance);
                var cached = cachedPoints(__instance);
                pending = new Snapshot
                {
                    World = __instance.m_world, Path = FilePath(__instance.m_world),
                    Offsets = new[] { offset0(__instance), offset1(__instance), offset2(__instance), offset3(__instance), offset4(__instance) },
                    RiverSeed = riverSeed(__instance), StreamSeed = streamSeed(__instance),
                    Lakes = lakes(__instance).ToArray(), Rivers = rivers(__instance).ToArray(), Streams = streams(__instance).ToArray(),
                    Cells = cells.Keys.ToArray(), Points = cells.Values.ToArray(),
                    CachedGrid = grid, CachedPoints = cached, CachedIsCell = cached != null && cells.TryGetValue(grid, out var current) && current == cached
                };
            }
        }

        private static bool Restore(WorldGenerator generator, string path)
        {
            try
            {
                using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Format) return false;
                    var drawn = new[] { offset0(generator), offset1(generator), offset2(generator), offset3(generator), offset4(generator) };
                    foreach (float value in drawn)
                        if (reader.ReadSingle() != value) return false;
                    if (reader.ReadInt32() != riverSeed(generator) || reader.ReadInt32() != streamSeed(generator)) return false;
                    int lakeCount = reader.ReadInt32();
                    var lakeList = new List<Vector2>(lakeCount);
                    for (int index = 0; index < lakeCount; index++) lakeList.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
                    var riverList = ReadRivers(reader);
                    var streamList = ReadRivers(reader);
                    int count = reader.ReadInt32();
                    var cells = new Dictionary<Vector2i, WorldGenerator.RiverPoint[]>(count);
                    for (int cell = 0; cell < count; cell++)
                        cells.Add(new Vector2i(reader.ReadInt32(), reader.ReadInt32()), ReadPoints(reader));
                    var grid = new Vector2i(reader.ReadInt32(), reader.ReadInt32());
                    byte kind = reader.ReadByte();
                    var cached = kind == 0 ? null : kind == 1 ? cells[grid] : ReadPoints(reader);
                    int size = reader.ReadInt32();
                    byte[] heights = reader.ReadBytes(size * size * 4), biomes = reader.ReadBytes(size * size);
                    if (size != 2048 || heights.Length != size * size * 4 || biomes.Length != size * size || reader.ReadInt32() != End) return false;
                    lakes(generator) = lakeList;
                    rivers(generator) = riverList;
                    streams(generator) = streamList;
                    var target = riverPoints(generator);
                    target.Clear();
                    foreach (var entry in cells) target.Add(entry.Key, entry.Value);
                    cachedGrid(generator) = grid;
                    cachedPoints(generator) = cached;
                    restoredWorld = generator.m_world;
                    restoredHeights = heights;
                    restoredBiomes = biomes;
                }
                Restored++;
                return true;
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("The kept world generation could not be read and is generated again: " + error.Message);
                return false;
            }
        }

        private static List<WorldGenerator.River> ReadRivers(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var list = new List<WorldGenerator.River>(count);
            for (int index = 0; index < count; index++)
                list.Add(new WorldGenerator.River
                {
                    p0 = new Vector2(reader.ReadSingle(), reader.ReadSingle()), p1 = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    center = new Vector2(reader.ReadSingle(), reader.ReadSingle()), widthMin = reader.ReadSingle(), widthMax = reader.ReadSingle(),
                    curveWidth = reader.ReadSingle(), curveWavelength = reader.ReadSingle()
                });
            return list;
        }

        private static WorldGenerator.RiverPoint[] ReadPoints(BinaryReader reader)
        {
            var points = new WorldGenerator.RiverPoint[reader.ReadInt32()];
            for (int index = 0; index < points.Length; index++)
                points[index] = new WorldGenerator.RiverPoint { p = new Vector2(reader.ReadSingle(), reader.ReadSingle()), w = reader.ReadSingle(), w2 = reader.ReadSingle() };
            return points;
        }

        // Called by the biome map's fill: true when the map came from the file, which also ends the generator's one-cell
        // river lookup the way the fill's own lookups would have, by moving it off the cell generation left it on.
        internal static bool TryRestorePoints(World world, AltBiomeWorldData data)
        {
            if (restoredWorld == null || restoredWorld != world || WorldGenerator.instance == null) return false;
            Buffer.BlockCopy(restoredHeights, 0, data.PointHeights, 0, restoredHeights.Length);
            var handle = GCHandle.Alloc(data.PointBiomes, GCHandleType.Pinned);
            try { Marshal.Copy(restoredBiomes, 0, handle.AddrOfPinnedObject(), restoredBiomes.Length); }
            finally { handle.Free(); }
            var generator = WorldGenerator.instance;
            cachedGrid(generator) = new Vector2i(-999999, -999999);
            cachedPoints(generator) = null;
            restoredWorld = null;
            restoredHeights = restoredBiomes = null;
            return true;
        }

        // Called once the biome map is filled natively: writes the generator's state and the map on a worker.
        internal static void Generated(World world, AltBiomeWorldData data)
        {
            var snapshot = pending;
            pending = null;
            if (snapshot == null || snapshot.World != world) return;
            var heights = new byte[data.PointHeights.Length * 4];
            Buffer.BlockCopy(data.PointHeights, 0, heights, 0, heights.Length);
            var biomes = new byte[data.PointBiomes.Length];
            var handle = GCHandle.Alloc(data.PointBiomes, GCHandleType.Pinned);
            try { Marshal.Copy(handle.AddrOfPinnedObject(), biomes, 0, biomes.Length); }
            finally { handle.Free(); }
            ThreadPool.QueueUserWorkItem(_ => Write(snapshot, heights, biomes));
        }

        private static void Write(Snapshot snapshot, byte[] heights, byte[] biomes)
        {
            string temporary = snapshot.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(snapshot.Path));
                using (var writer = new BinaryWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20)))
                {
                    writer.Write(Magic);
                    writer.Write(Format);
                    foreach (float value in snapshot.Offsets) writer.Write(value);
                    writer.Write(snapshot.RiverSeed);
                    writer.Write(snapshot.StreamSeed);
                    writer.Write(snapshot.Lakes.Length);
                    foreach (var lake in snapshot.Lakes) { writer.Write(lake.x); writer.Write(lake.y); }
                    WriteRivers(writer, snapshot.Rivers);
                    WriteRivers(writer, snapshot.Streams);
                    writer.Write(snapshot.Cells.Length);
                    for (int cell = 0; cell < snapshot.Cells.Length; cell++)
                    {
                        writer.Write(snapshot.Cells[cell].x);
                        writer.Write(snapshot.Cells[cell].y);
                        WritePoints(writer, snapshot.Points[cell]);
                    }
                    writer.Write(snapshot.CachedGrid.x);
                    writer.Write(snapshot.CachedGrid.y);
                    writer.Write((byte)(snapshot.CachedPoints == null ? 0 : snapshot.CachedIsCell ? 1 : 2));
                    if (snapshot.CachedPoints != null && !snapshot.CachedIsCell) WritePoints(writer, snapshot.CachedPoints);
                    writer.Write(2048);
                    writer.Write(heights);
                    writer.Write(biomes);
                    writer.Write(End);
                }
                if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path);
                File.Move(temporary, snapshot.Path);
                Interlocked.Increment(ref Written);
                // A player keeps the worlds of the last three servers joined.
                foreach (var old in new DirectoryInfo(System.IO.Path.GetDirectoryName(snapshot.Path)).GetFiles("*.bin").OrderByDescending(file => file.LastWriteTimeUtc).Skip(3))
                    old.Delete();
            }
            catch (Exception error)
            {
                try { File.Delete(temporary); } catch (Exception) { }
                Plugin.Log.LogWarning("The world generation could not be kept: " + error.Message);
            }
        }

        private static void WriteRivers(BinaryWriter writer, WorldGenerator.River[] list)
        {
            writer.Write(list.Length);
            foreach (var river in list)
            {
                writer.Write(river.p0.x); writer.Write(river.p0.y); writer.Write(river.p1.x); writer.Write(river.p1.y);
                writer.Write(river.center.x); writer.Write(river.center.y); writer.Write(river.widthMin); writer.Write(river.widthMax);
                writer.Write(river.curveWidth); writer.Write(river.curveWavelength);
            }
        }

        private static void WritePoints(BinaryWriter writer, WorldGenerator.RiverPoint[] points)
        {
            writer.Write(points.Length);
            foreach (var point in points) { writer.Write(point.p.x); writer.Write(point.p.y); writer.Write(point.w); writer.Write(point.w2); }
        }
    }
}
