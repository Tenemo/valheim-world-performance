using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The grass system finds the ground under each of its candidate points, about a thousand a 10 m patch, with a ray cast
    // 1 km down through the scene's terrain layer, and builds one patch a frame while the player moves; the rays were about
    // two thirds of a patch. The only colliders on that layer are the zone heightmaps' collision meshes, so the mod
    // intersects the same vertical ray with the triangle under the point itself, from the vertices the collider was cooked
    // from. The point and the triangle are the engine's; the height agrees within 0.05 mm and the normal within 2e-7, so the
    // same grass grows in the same places. It keeps the vertices of the last few heightmaps it used and reads them again
    // after a heightmap's collision mesh is rebuilt. Points close to a triangle's edge, heightmaps whose collider is off,
    // replaced, moved, scaled or rotated, and heightmaps with any other collider on the terrain layer over them cast the
    // native ray.
    internal static class GrassGround
    {
        // The isolated test client sets false to compare the native rays.
        internal static bool Direct = true;
        internal static long Answered, Cast;
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".grass-ground");
        private static readonly AccessTools.FieldRef<Heightmap, MeshCollider> colliderOf = AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> collisionMeshOf = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_collisionMesh");
        // Points this close to a cell's edge or diagonal, in cells, cast natively: either triangle could answer there.
        private const float Margin = 1e-3f;
        private static int terrainMask;
        private static readonly Collider[] overlaps = new Collider[8];
        private static readonly List<Vector3> vertexList = new List<Vector3>();

        private sealed class Tile
        {
            internal Heightmap Map;
            internal Vector3 Position;
            internal Vector3[] Vertices;
            internal int Width, CheckedFrame = -1;
            internal float Scale, Corner, Half;
            internal bool Usable;
        }

        // The last heightmaps used, newest first: grass patches lie within one or two zones of the player.
        private static readonly Tile[] tiles = new Tile[4];

        internal static void Start()
        {
            try
            {
                terrainMask = LayerMask.GetMask("terrain");
                patches.Patch(AccessTools.Method(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo)),
                    prefix: new HarmonyMethod(typeof(GrassGround), nameof(Ground)) { priority = Priority.Last });
                patches.Patch(AccessTools.Method(typeof(Heightmap), "RebuildCollisionMesh"), postfix: new HarmonyMethod(typeof(GrassGround), nameof(Rebuilt)));
                patches.Patch(AccessTools.Method(typeof(Heightmap), "OnDestroy"), postfix: new HarmonyMethod(typeof(GrassGround), nameof(Destroyed)));
                Running = true;
            }
            catch (Exception error)
            {
                Failure = error.ToString();
                Stop();
                throw;
            }
        }

        internal static void Stop()
        {
            patches.UnpatchSelf();
            Array.Clear(tiles, 0, tiles.Length);
            Running = false;
        }

        // The collision mesh's vertices are read again at the next use.
        private static void Rebuilt(Heightmap __instance)
        {
            foreach (var tile in tiles)
                if (tile != null && ReferenceEquals(tile.Map, __instance)) tile.Vertices = null;
        }

        // A zone loaded again gets a new heightmap at the same place.
        private static void Destroyed(Heightmap __instance)
        {
            for (int index = 0; index < tiles.Length; index++)
                if (tiles[index] != null && ReferenceEquals(tiles[index].Map, __instance)) tiles[index] = null;
        }

        private static bool Ground(Vector3 p, ref Vector3 point, ref Vector3 normal, ref Heightmap hmap, ref Heightmap.Biome biome, ref bool __result)
        {
            if (!Direct) return true;
            var tile = TileAt(p);
            if (tile == null || !Hit(tile, p, out var hitPoint, out var hitNormal)) { Cast++; return true; }
            point = hitPoint;
            normal = hitNormal;
            hmap = tile.Map;
            biome = tile.Map.GetBiome(hitPoint);
            __result = true;
            Answered++;
            return false;
        }

        private static Tile TileAt(Vector3 p)
        {
            for (int index = 0; index < tiles.Length; index++)
            {
                var tile = tiles[index];
                if (tile == null || Math.Abs(p.x - tile.Position.x) > tile.Half || Math.Abs(p.z - tile.Position.z) > tile.Half) continue;
                if (!tile.Map) { tiles[index] = null; break; }
                if (index > 0) { tiles[index] = tiles[0]; tiles[0] = tile; }
                return Usable(tile) ? tile : null;
            }
            var map = Heightmap.FindHeightmap(p);
            if (!map) return null;
            var added = new Tile();
            Array.Copy(tiles, 0, tiles, 1, tiles.Length - 1);
            tiles[0] = added;
            added.Map = map;
            added.Position = map.transform.position;
            added.Width = map.m_width;
            added.Scale = map.m_scale;
            added.Corner = (float)((double)map.m_width * (double)map.m_scale * -0.5);
            added.Half = -added.Corner;
            added.Vertices = null;
            added.CheckedFrame = -1;
            return Usable(added) ? added : null;
        }

        // Checked once a frame: the heightmap's own collider, live, unmoved and holding its collision mesh, and no other collider
        // on the terrain layer inside the tile.
        private static bool Usable(Tile tile)
        {
            int frame = Time.frameCount;
            if (tile.CheckedFrame == frame) return tile.Usable;
            tile.CheckedFrame = frame;
            tile.Usable = false;
            var map = tile.Map;
            if (!map) return false;
            var collider = colliderOf(map);
            var mesh = collisionMeshOf(map);
            if (!collider || !mesh || !collider.enabled || !collider.gameObject.activeInHierarchy || !ReferenceEquals(collider.sharedMesh, mesh)) return false;
            var transform = collider.transform;
            if (transform.position != tile.Position || transform.rotation != Quaternion.identity || transform.lossyScale != Vector3.one) return false;
            int found = Physics.OverlapBoxNonAlloc(new Vector3(tile.Position.x, 0f, tile.Position.z), new Vector3(tile.Half - 0.01f, 100000f, tile.Half - 0.01f),
                overlaps, Quaternion.identity, terrainMask, QueryTriggerInteraction.UseGlobal);
            if (found != 1 || overlaps[0] != collider) return false;
            if (tile.Vertices == null)
            {
                int count = (tile.Width + 1) * (tile.Width + 1);
                if (mesh.vertexCount != count) return false;
                mesh.GetVertices(vertexList);
                tile.Vertices = vertexList.ToArray();
            }
            tile.Usable = true;
            return true;
        }

        // The vertical ray's hit on the cell's triangle, as the physics engine intersects a ray with a triangle in the mesh's
        // own space (Moller-Trumbore), with the face normal facing up.
        private static bool Hit(Tile tile, Vector3 p, out Vector3 point, out Vector3 normal)
        {
            point = p; normal = Vector3.up;
            var origin = p + Vector3.up * 500f;
            var position = tile.Position;
            var local = new Vector3(origin.x - position.x, origin.y - position.y, origin.z - position.z);
            float fx = (local.x - tile.Corner) / tile.Scale, fz = (local.z - tile.Corner) / tile.Scale;
            int width = tile.Width;
            if (!(fx >= 0f && fz >= 0f)) return false;
            int x = (int)fx, z = (int)fz;
            if (x >= width || z >= width) return false;
            float u = fx - x, v = fz - z;
            if (u < Margin || v < Margin || u > 1f - Margin || v > 1f - Margin || Math.Abs(u + v - 1f) < Margin) return false;
            var vertices = tile.Vertices;
            int row = width + 1;
            Vector3 v0, v1, v2;
            if (u + v < 1f) { v0 = vertices[z * row + x]; v1 = vertices[(z + 1) * row + x]; v2 = vertices[z * row + x + 1]; }
            else { v0 = vertices[z * row + x + 1]; v1 = vertices[(z + 1) * row + x]; v2 = vertices[(z + 1) * row + x + 1]; }
            // The distance in double precision, which matches the engine's hit height more often than single precision.
            double ax = v1.x - (double)v0.x, ay = v1.y - (double)v0.y, az = v1.z - (double)v0.z;
            double bx = v2.x - (double)v0.x, by = v2.y - (double)v0.y, bz = v2.z - (double)v0.z;
            double det = bx * az - ax * bz;
            if (Math.Abs(det) < 1e-12) return false;
            double tx = local.x - (double)v0.x, ty = local.y - (double)v0.y, tz = local.z - (double)v0.z;
            double t = (bx * (ty * az - tz * ay) + by * (tz * ax - tx * az) + bz * (tx * ay - ty * ax)) / det;
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            var face = Vector3.Cross(e1, e2);
            if (face.y < 0f) face = -face;
            normal = face * (1f / (float)Math.Sqrt(face.x * face.x + face.y * face.y + face.z * face.z));
            point = new Vector3(origin.x, (float)(origin.y - t), origin.z);
            return true;
        }
    }
}
