using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // A streamed terrain compiler can request a full rebuild even when it only replays the
    // same changes. Keep the live tile's generated mesh after verifying its inputs and output.
    internal static class TerrainMeshReuse
    {
        private sealed class MeshState
        {
            private Mesh mesh;
            private Bounds bounds;
            private SubMeshDescriptor submesh;
            private IndexFormat format;
            private VertexAttributeDescriptor[] attributes;
            private byte[][] streams;
            private byte[] indices;
            private static readonly List<VertexAttributeDescriptor> layout = new List<VertexAttributeDescriptor>();

            internal void Record(Mesh current)
            {
                mesh = null;
                if (!Supported(current)) return;
                using (var snapshot = Mesh.AcquireReadOnlyMeshData(current))
                {
                    var data = snapshot[0];
                    if (streams == null || streams.Length != data.vertexBufferCount) streams = new byte[data.vertexBufferCount][];
                    for (int i = 0; i < streams.Length; i++) Copy(data.GetVertexData<byte>(i), ref streams[i]);
                    Copy(data.GetIndexData<byte>(), ref indices);
                    submesh = data.GetSubMesh(0);
                    format = data.indexFormat;
                }
                bounds = current.bounds;
                attributes = current.GetVertexAttributes();
                mesh = current;
            }

            private static bool Supported(Mesh current) => current && current.isReadable && current.subMeshCount == 1 &&
                current.blendShapeCount == 0 && current.bindposeCount == 0 && current.vertexCount > 0 &&
                (current.vertexBufferTarget & ~(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.CopySource)) == 0 &&
                (current.indexBufferTarget & ~(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.CopySource)) == 0;

            private static void Copy(NativeArray<byte> data, ref byte[] target)
            {
                if (target == null || target.Length != data.Length) target = new byte[data.Length];
                data.CopyTo(target);
            }

            private static unsafe bool Matches(NativeArray<byte> data, byte[] expected)
            {
                if (data.Length != expected.Length) return false;
                fixed (byte* saved = expected) return data.Length == 0 || UnsafeUtility.MemCmp(data.GetUnsafeReadOnlyPtr(), saved, data.Length) == 0;
            }

            internal bool Unchanged(Mesh current)
            {
                if (!ReferenceEquals(mesh, current) || !Supported(current) || !bounds.Equals(current.bounds)) return false;
                current.GetVertexAttributes(layout);
                if (layout.Count != attributes.Length) return false;
                for (int i = 0; i < layout.Count; i++) if (!layout[i].Equals(attributes[i])) return false;
                using (var snapshot = Mesh.AcquireReadOnlyMeshData(current))
                {
                    var data = snapshot[0];
                    if (data.vertexBufferCount != streams.Length || data.indexFormat != format ||
                        !submesh.Equals(data.GetSubMesh(0)) || !Matches(data.GetIndexData<byte>(), indices)) return false;
                    for (int i = 0; i < streams.Length; i++) if (!Matches(data.GetVertexData<byte>(i), streams[i])) return false;
                }
                return true;
            }
        }

        private sealed class Tile
        {
            internal float[] heights;
            internal int width;
            internal float scale;
            internal Vector3 position;
            internal readonly MeshState collision = new MeshState(), render = new MeshState();
            internal MeshCollider collider;
            internal MeshFilter filter;
            internal bool convex, uniform, renderCurrent;
            internal MeshColliderCookingOptions cooking;
            internal readonly Color32[] colors = new Color32[4];
        }
        private static ConditionalWeakTable<Heightmap, Tile> tiles = new ConditionalWeakTable<Heightmap, Tile>();
        private static readonly AccessTools.FieldRef<Heightmap, List<float>> Heights = AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");
        private static readonly AccessTools.FieldRef<List<float>, float[]> Items = AccessTools.FieldRefAccess<List<float>, float[]>("_items");
        private static readonly AccessTools.FieldRef<Heightmap, BiomeSector[]> Corners = AccessTools.FieldRefAccess<Heightmap, BiomeSector[]>("m_cornerBiomes");
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> Collision = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_collisionMesh");
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> Render = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly AccessTools.FieldRef<Heightmap, MeshFilter> Filter = AccessTools.FieldRefAccess<Heightmap, MeshFilter>("m_meshFilter");
        private static readonly AccessTools.FieldRef<Heightmap, MeshCollider> Collider = AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");
        private static readonly AccessTools.FieldRef<Heightmap, int> Width = AccessTools.FieldRefAccess<Heightmap, int>("m_width");
        private static readonly AccessTools.FieldRef<Heightmap, float> Scale = AccessTools.FieldRefAccess<Heightmap, float>("m_scale");
        internal static long ReusedCollision { get; private set; }
        internal static long ReusedRender { get; private set; }

        internal static void Install(Harmony patches)
        {
            void Patch(string method, string before, string after) => patches.Patch(AccessTools.Method(typeof(Heightmap), method),
                prefix: new HarmonyMethod(typeof(TerrainMeshReuse), before) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(TerrainMeshReuse), after) { priority = Priority.Last });
            Patch("RebuildCollisionMesh", nameof(BuildCollision), nameof(RecordCollision));
            Patch("RebuildRenderMesh", nameof(BuildRender), nameof(RecordRender));
        }
        internal static void Clear() => tiles = new ConditionalWeakTable<Heightmap, Tile>();

        private static bool ClientTile(Heightmap tile) => !tile.IsDistantLod && ZNet.instance && !ZNet.instance.IsDedicated();

        private static bool BuildCollision(Heightmap __instance, out bool __state)
        {
            __state = ClientTile(__instance);
            if (!__state || !tiles.TryGetValue(__instance, out var tile)) return true;
            var heights = Heights(__instance);
            var collider = Collider(__instance);
            var mesh = Collision(__instance);
            if (tile.heights == null || tile.heights.Length != heights.Count || Width(__instance) != tile.width ||
                Scale(__instance) != tile.scale || !__instance.transform.position.Equals(tile.position) ||
                !collider || !ReferenceEquals(tile.collider, collider) || collider.sharedMesh != mesh ||
                tile.convex != collider.convex || tile.cooking != collider.cookingOptions ||
                !MemoryMarshal.AsBytes(new ReadOnlySpan<float>(tile.heights)).SequenceEqual(
                    MemoryMarshal.AsBytes(new ReadOnlySpan<float>(Items(heights), 0, heights.Count))) || !tile.collision.Unchanged(mesh)) return true;
            __state = false;
            ReusedCollision++;
            return false;
        }

        private static void RecordCollision(Heightmap __instance, bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal) return;
            var tile = tiles.GetValue(__instance, _ => new Tile());
            var heights = Heights(__instance);
            if (tile.heights == null || tile.heights.Length != heights.Count) tile.heights = new float[heights.Count];
            heights.CopyTo(tile.heights);
            tile.width = Width(__instance);
            tile.scale = Scale(__instance);
            tile.position = __instance.transform.position;
            tile.collider = Collider(__instance);
            if (tile.collider) { tile.convex = tile.collider.convex; tile.cooking = tile.collider.cookingOptions; }
            tile.collision.Record(Collision(__instance));
            tile.renderCurrent = false;
        }

        private static bool BuildRender(Heightmap __instance, out bool __state)
        {
            __state = ClientTile(__instance) && tiles.TryGetValue(__instance, out _);
            if (!__state) return true;
            var tile = tiles.GetValue(__instance, _ => new Tile());
            var mesh = Render(__instance);
            var corners = Corners(__instance);
            bool uniform = corners[0] == corners[1] && corners[0] == corners[2] && corners[0] == corners[3];
            var filter = Filter(__instance);
            if (!tile.renderCurrent || tile.width != Width(__instance) || tile.scale != Scale(__instance) ||
                !tile.position.Equals(__instance.transform.position) || !filter || filter != tile.filter || filter.sharedMesh != mesh ||
                uniform != tile.uniform || !tile.collision.Unchanged(Collision(__instance)) || !tile.render.Unchanged(mesh)) return true;
            for (int i = 0; i < 4; i++) if (!tile.colors[i].Equals(Heightmap.GetBiomeColor(corners[i]))) return true;
            __state = false;
            ReusedRender++;
            return false;
        }

        private static void RecordRender(Heightmap __instance, bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal || !tiles.TryGetValue(__instance, out var tile)) return;
            var corners = Corners(__instance);
            tile.uniform = corners[0] == corners[1] && corners[0] == corners[2] && corners[0] == corners[3];
            for (int i = 0; i < 4; i++) tile.colors[i] = Heightmap.GetBiomeColor(corners[i]);
            tile.filter = Filter(__instance);
            tile.render.Record(Render(__instance));
            tile.renderCurrent = tile.collision.Unchanged(Collision(__instance));
        }
    }
}
