using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Jobs;
using UnityEngine;

namespace WorldPerformance
{
    // Every zone that loads rebuilds its terrain tile in full, as does every terrain edit and every forced build before ground
    // snapping; in the hidden client a zone tile took 3.9–4.2 ms, all on the main thread. Two of its steps did avoidable work
    // there. The render mesh asked for each of its 4,225 vertices' biome colours from the tile's four corner sectors, looking
    // up each corner's colour again for every vertex (0.7–1.0 ms); the corners' colours are now looked up once per rebuild and
    // blended exactly as the game blends them. Assigning the collision mesh to the tile's collider cooked it on the main thread
    // (1.5 ms); it is now baked on a job worker while the main thread builds the render mesh, and assigned before the rebuild
    // returns, so no other code sees the collider in between. A forced build and the frame's late rebuilds, which rebuild only
    // heightmaps one after another, bake all their tiles at once and assign them when the last is rebuilt, before the snapping
    // casts its rays or other objects update. The meshes and the collider's surface stay the game's own byte for byte.
    internal static class TerrainRebuilds
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".terrain-rebuilds");
        private static readonly AccessTools.FieldRef<Heightmap, BiomeSector[]> cornersOf = AccessTools.FieldRefAccess<Heightmap, BiomeSector[]>("m_cornerBiomes");
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> collisionMeshOf = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_collisionMesh");
        private static readonly Func<Heightmap, float, float, Color> nativeColour = AccessTools.MethodDelegate<Func<Heightmap, float, float, Color>>(NativeColourMethod());
        // Bakes started and not yet assigned to their colliders.
        private static readonly List<(MeshCollider collider, Mesh mesh, JobHandle bake)> baking = new List<(MeshCollider, Mesh, JobHandle)>();
        // The tile being coloured and its corners' colours, looked up again at the start of each render mesh rebuild.
        private static Heightmap colouring;
        private static BiomeSector[] colouringCorners;
        private static bool uniform;
        private static Color32 first, second, third, fourth;
        // Forced builds and late-rebuild passes under way, which assign their bakes when they end.
        private static int batching;
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        // Off works as natively; the isolated client switches each off and on for its checks and paired measurements.
        internal static bool Colouring { get; set; } = true;
        internal static bool Baking { get; set; } = true;
        internal static long Coloured { get; private set; }
        internal static long Baked { get; private set; }

        private struct Bake : IJob
        {
            internal int Mesh;
            internal bool Convex;
            internal MeshColliderCookingOptions Options;
            public void Execute() => Physics.BakeMesh(Mesh, Convex, Options);
        }

        private static System.Reflection.MethodInfo NativeColourMethod() => AccessTools.Method(typeof(Heightmap), "GetBiomeColor", new[] { typeof(float), typeof(float) });

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            try
            {
                var type = typeof(Heightmap);
                patches.Patch(AccessTools.Method(type, "RebuildRenderMesh"), prefix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(Recolour)),
                    transpiler: new HarmonyMethod(typeof(TerrainRebuilds), nameof(ColourOnce)));
                patches.Patch(AccessTools.Method(type, "RebuildCollisionMesh"), prefix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(Rebuilding)),
                    transpiler: new HarmonyMethod(typeof(TerrainRebuilds), nameof(BakeOnWorker)));
                patches.Patch(AccessTools.Method(type, nameof(Heightmap.Regenerate)), postfix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(Regenerated)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(TerrainRebuilds), nameof(RegenerateEnded)));
                patches.Patch(AccessTools.Method(type, nameof(Heightmap.ForceGenerateAll)), prefix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(Forcing)),
                    postfix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(Forced)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(TerrainRebuilds), nameof(ForceEnded)));
                patches.Patch(AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate)), prefix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(LateUpdating)),
                    postfix: new HarmonyMethod(typeof(TerrainRebuilds), nameof(LateUpdated)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(TerrainRebuilds), nameof(LateUpdateEnded)));
            }
            catch (Exception error)
            {
                // A changed game method leaves terrain rebuilds native.
                Failure = error.Message;
                Running = false;
                patches.UnpatchSelf();
                Plugin.Log.LogWarning("Terrain rebuilds stay native: " + error.Message);
            }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Finish();
            Running = false;
            batching = 0;
            colouring = null;
            colouringCorners = null;
            patches.UnpatchSelf();
        }

        // Valheim 1.0.15: s_tempColors.Add(GetBiomeColor(ix, iy)) for every vertex of a zone tile.
        private static IEnumerable<CodeInstruction> ColourOnce(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var native = NativeColourMethod();
            var calls = code.Where(instruction => instruction.Calls(native)).ToList();
            if (calls.Count != 1) throw new InvalidOperationException("Valheim's terrain render mesh changed; expected one biome colour per vertex.");
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(TerrainRebuilds), nameof(Colour));
            return code;
        }

        // Valheim 1.0.15: if ((bool)m_collider) m_collider.sharedMesh = m_collisionMesh;
        private static IEnumerable<CodeInstruction> BakeOnWorker(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var assign = AccessTools.PropertySetter(typeof(MeshCollider), nameof(MeshCollider.sharedMesh));
            var calls = code.Where(instruction => instruction.Calls(assign)).ToList();
            if (calls.Count != 1) throw new InvalidOperationException("Valheim's terrain collision mesh changed; expected one collider assignment.");
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(TerrainRebuilds), nameof(Cook));
            return code;
        }

        private static void Recolour() => colouring = null;

        // The game's GetBiomeColor(ix, iy) with the corners' colours looked up once per rebuild instead of for every vertex.
        private static Color Colour(Heightmap map, float ix, float iy)
        {
            if (!Colouring) return nativeColour(map, ix, iy);
            var corners = cornersOf(map);
            if (map != colouring || corners != colouringCorners)
            {
                colouring = map;
                colouringCorners = corners;
                uniform = corners[0] == corners[1] && corners[0] == corners[2] && corners[0] == corners[3];
                first = Heightmap.GetBiomeColor(corners[0]);
                if (!uniform)
                {
                    second = Heightmap.GetBiomeColor(corners[1]);
                    third = Heightmap.GetBiomeColor(corners[2]);
                    fourth = Heightmap.GetBiomeColor(corners[3]);
                }
                Coloured++;
            }
            if (uniform) return first;
            return Color32.Lerp(Color32.Lerp(first, second, ix), Color32.Lerp(third, fourth, ix), iy);
        }

        private static void Cook(MeshCollider collider, Mesh mesh)
        {
            if (!Baking || !mesh)
            {
                collider.sharedMesh = mesh;
                return;
            }
            var bake = new Bake { Mesh = mesh.GetInstanceID(), Convex = collider.convex, Options = collider.cookingOptions }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            baking.Add((collider, mesh, bake));
            Baked++;
        }

        // A tile rebuilt again while its earlier bake waits, within one forced build, lets that bake finish before its
        // collision mesh changes.
        private static void Rebuilding(Heightmap __instance)
        {
            var mesh = collisionMeshOf(__instance);
            if (mesh && baking.Any(entry => entry.mesh == mesh)) Finish();
        }

        // Assigns every baked collision mesh to its collider, which then uses the baked data instead of cooking it again.
        private static void Finish()
        {
            for (int index = 0; index < baking.Count; index++)
            {
                var (collider, mesh, bake) = baking[index];
                bake.Complete();
                if (collider) collider.sharedMesh = mesh;
            }
            baking.Clear();
        }

        private static void Regenerated()
        {
            if (batching == 0) Finish();
        }

        private static Exception RegenerateEnded(Exception __exception)
        {
            if (batching == 0) Finish();
            return __exception;
        }

        private static void Forcing() => batching++;

        private static void Forced()
        {
            if (batching == 1) Finish();
        }

        private static Exception ForceEnded(Exception __exception)
        {
            batching = Math.Max(0, batching - 1);
            if (batching == 0) Finish();
            return __exception;
        }

        // MonoUpdaters.LateUpdate runs every updater list through one dispatcher; only the heightmaps' pass is a batch.
        private static void LateUpdating(List<IMonoUpdater> source, out bool __state)
        {
            __state = source == Heightmap.Instances;
            if (__state) batching++;
        }

        private static void LateUpdated(bool __state)
        {
            if (__state && batching == 1) Finish();
        }

        private static Exception LateUpdateEnded(Exception __exception, bool __state)
        {
            if (__state)
            {
                batching = Math.Max(0, batching - 1);
                if (batching == 0) Finish();
            }
            return __exception;
        }
    }
}
