using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace WorldPerformance
{
    // The game joins its 32 m navigation tiles with one link per metre of each tile's north and east edges and per
    // walkable level found there, about a hundred links a tile, and removing a link costs Unity more the more links it
    // holds. While the player travels, three things made that work pile up until the game was restarted:
    // - the ground hits under each edge point came in the undefined order of Physics.RaycastNonAlloc, which changes while
    //   colliders stream in, and the game's height filter depends on that order, so rebuilt edges seemed to change;
    // - any changed point replaced every link along the edge;
    // - an expired tile's mesh was removed only in a frame with no link removal queued, which during travel never came,
    //   so expired meshes stayed loaded by the thousand and slowed every later navigation operation.
    // Each expired tile also left its mesh data in memory until the hourly unloading of unused assets, and a tile could
    // expire just as its build started, leaving the links and first mesh of that build in place for good.
    // Ground hits are now read from the top down, a rebuilt edge replaces only the links whose points changed, an expired
    // tile's mesh and data are removed as soon as the links queued before it are gone, and the tile being built can expire
    // only once its build has finished. Links now stand 2 m apart, which holds half as many.
    [HarmonyPatch(typeof(Pathfinding), "FindGround")]
    internal static class PathfindingGroundHits
    {
        private static readonly Type Settings = AccessTools.Inner(typeof(Pathfinding), "AgentSettings");
        internal static readonly AccessTools.FieldRef<object, NavMeshBuildSettings> Build = AccessTools.FieldRefAccess<NavMeshBuildSettings>(Settings, "m_build");
        internal static readonly AccessTools.FieldRef<object, bool> CanSwim = AccessTools.FieldRefAccess<bool>(Settings, "m_canSwim");
        private static readonly AccessTools.FieldRef<object, float> SwimDepth = AccessTools.FieldRefAccess<float>(Settings, "m_swimDepth");

        // Valheim 1.0.15: FindGround(Vector3 p, bool testWater, List<Vector3> hits, AgentSettings settings)
        private static bool Prefix(Pathfinding __instance, Vector3 p, bool testWater, List<Vector3> hits, object settings, RaycastHit[] ___tempHitArray)
        {
            Find(__instance, p, testWater, hits, settings, ___tempHitArray);
            return false;
        }

        // The native search from 6000 m down, keeping each hit at least an agent's height below the previous one kept.
        internal static void Find(Pathfinding pathfinding, Vector3 point, bool testWater, List<Vector3> hits, object settings, RaycastHit[] buffer)
        {
            point.y = 6000f;
            int water = pathfinding.m_waterLayers.value;
            int count = Physics.RaycastNonAlloc(point, Vector3.down, buffer, 10000f, testWater ? pathfinding.m_layers.value | water : pathfinding.m_layers.value);
            for (int index = 1; index < count; index++)
            {
                var hit = buffer[index];
                int at = index - 1;
                for (; at >= 0 && Above(hit, buffer[at]); at--) buffer[at + 1] = buffer[at];
                buffer[at + 1] = hit;
            }
            float agentHeight = Build(settings).agentHeight, level = point.y;
            for (int index = 0; index < count; index++)
            {
                Vector3 ground = buffer[index].point;
                if (Mathf.Abs(ground.y - level) < agentHeight) continue;
                level = ground.y;
                if (((1 << buffer[index].collider.gameObject.layer) & water) != 0) ground.y -= SwimDepth(settings);
                hits.Add(ground);
            }
        }

        // Nearer the start of the downward ray is higher; equal distances keep a fixed order by collider.
        private static bool Above(RaycastHit hit, RaycastHit other) =>
            hit.distance < other.distance || hit.distance == other.distance && hit.colliderInstanceID < other.colliderInstanceID;
    }

    // Valheim 1.0.21: the scene's Pathfinding spaces edge links by m_linkWidth, 1 m, and makes each link that wide, so links
    // 2 m apart still cover the whole edge. A herd and a trip away from it have held the navigation mesh's limit of 65,535
    // links, past which it refuses new ones and tiles stay unconnected, and each removal costs more the more links are held.
    [HarmonyPatch(typeof(Pathfinding), "Awake")]
    internal static class PathfindingLinkSpacing
    {
        private const float Metres = 2f;

        private static void Postfix(Pathfinding __instance) => __instance.m_linkWidth = Metres;
    }

    [HarmonyPatch(typeof(Pathfinding), "ConnectAlongEdge")]
    internal static class PathfindingEdgeLinks
    {
        private static readonly List<Vector3> Points = new List<Vector3>();
        private static readonly Dictionary<Vector3, NavMeshLinkInstance> Previous = new Dictionary<Vector3, NavMeshLinkInstance>();
        private static readonly List<KeyValuePair<Vector3, NavMeshLinkInstance>> Current = new List<KeyValuePair<Vector3, NavMeshLinkInstance>>();

        // Valheim 1.0.15: ConnectAlongEdge(List<KeyValuePair<Vector3, NavMeshLinkInstance>> links, Vector3 p0, Vector3 p1,
        // float step, AgentSettings settings) finds the edge's points, returns when CompareLinks finds them unchanged, and
        // otherwise queues every link for removal and adds one for each point.
        private static bool Prefix(Pathfinding __instance, List<KeyValuePair<Vector3, NavMeshLinkInstance>> links, Vector3 p0, Vector3 p1, float step,
            object settings, RaycastHit[] ___tempHitArray, Queue<NavMeshLinkInstance> ___m_linkRemoveQueue)
        {
            Vector3 along = (p1 - p0).normalized, across = Vector3.Cross(Vector3.up, along);
            float length = Vector3.Distance(p0, p1);
            bool swim = PathfindingGroundHits.CanSwim(settings);
            Points.Clear();
            for (float distance = step / 2f; distance <= length; distance += step)
                PathfindingGroundHits.Find(__instance, p0 + along * distance, swim, Points, settings, ___tempHitArray);
            if (Unchanged(links)) return false;
            Previous.Clear();
            foreach (var link in links)
            {
                if (Previous.ContainsKey(link.Key)) ___m_linkRemoveQueue.Enqueue(link.Value);
                else Previous.Add(link.Key, link.Value);
            }
            int agent = PathfindingGroundHits.Build(settings).agentTypeID;
            Current.Clear();
            PathfindingRecords.BeforeAdding();
            foreach (var point in Points)
            {
                if (Previous.TryGetValue(point, out var kept))
                {
                    Previous.Remove(point);
                    Current.Add(new KeyValuePair<Vector3, NavMeshLinkInstance>(point, kept));
                    continue;
                }
                var added = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = point - across * 0.1f, endPosition = point + across * 0.1f, width = step,
                    costModifier = __instance.m_linkCost, bidirectional = true, agentTypeID = agent, area = 2,
                });
                if (NavMesh.IsLinkValid(added)) Current.Add(new KeyValuePair<Vector3, NavMeshLinkInstance>(point, added));
            }
            foreach (var removed in Previous.Values) ___m_linkRemoveQueue.Enqueue(removed);
            links.Clear();
            links.AddRange(Current);
            return false;
        }

        // The native CompareLinks: the same points in the same order.
        private static bool Unchanged(List<KeyValuePair<Vector3, NavMeshLinkInstance>> links)
        {
            if (links.Count != Points.Count) return false;
            for (int index = 0; index < Points.Count; index++)
                if (Points[index] != links[index].Key) return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(Pathfinding))]
    internal static class PathfindingRemovals
    {
        private static readonly Type Tile = AccessTools.Inner(typeof(Pathfinding), "NavMeshTile");
        private static readonly AccessTools.FieldRef<object, float> PokeTime = AccessTools.FieldRefAccess<float>(Tile, "m_pokeTime");
        private static readonly AccessTools.FieldRef<object, NavMeshData> Data = AccessTools.FieldRefAccess<NavMeshData>(Tile, "m_data");
        private static readonly AccessTools.FieldRef<object, NavMeshDataInstance> Mesh = AccessTools.FieldRefAccess<NavMeshDataInstance>(Tile, "m_instance");
        private static readonly AccessTools.FieldRef<object, List<KeyValuePair<Vector3, NavMeshLinkInstance>>> EastLinks =
            AccessTools.FieldRefAccess<List<KeyValuePair<Vector3, NavMeshLinkInstance>>>(Tile, "m_links1");
        private static readonly AccessTools.FieldRef<object, List<KeyValuePair<Vector3, NavMeshLinkInstance>>> NorthLinks =
            AccessTools.FieldRefAccess<List<KeyValuePair<Vector3, NavMeshLinkInstance>>>(Tile, "m_links2");
        private static readonly Action<Pathfinding, object, object, Queue<NavMeshLinkInstance>, Queue<NavMeshDataInstance>> ExpireOne =
            (Action<Pathfinding, object, object, Queue<NavMeshLinkInstance>, Queue<NavMeshDataInstance>>)Delegate.CreateDelegate(
                typeof(Action<Pathfinding, object, object, Queue<NavMeshLinkInstance>, Queue<NavMeshDataInstance>>),
                AccessTools.Method(typeof(PathfindingRemovals), nameof(Expire)).MakeGenericMethod(Tile));
        // Each queued tile mesh with its data and the total of removed links at which the links queued before it are gone.
        private static readonly Queue<KeyValuePair<long, NavMeshData>> Expired = new Queue<KeyValuePair<long, NavMeshData>>();
        private static long removedLinks;

        // Valheim 1.0.15: TimeoutTiles drops the first tile not poked within m_tileTimeout and queues its links and mesh for
        // removal. It left the tile's NavMeshData to the hourly unloading of unused assets, and it could also drop the tile
        // whose build had just started, so the finished build added links, and the first time a mesh, that nothing removed.
        [HarmonyPrefix, HarmonyPatch("TimeoutTiles")]
        private static bool TimeoutTiles(Pathfinding __instance, object ___m_tiles, object ___m_buildTile,
            Queue<NavMeshLinkInstance> ___m_linkRemoveQueue, Queue<NavMeshDataInstance> ___m_tileRemoveQueue)
        {
            ExpireOne(__instance, ___m_tiles, ___m_buildTile, ___m_linkRemoveQueue, ___m_tileRemoveQueue);
            return false;
        }

        private static void Expire<T>(Pathfinding pathfinding, object tiles, object building, Queue<NavMeshLinkInstance> links,
            Queue<NavMeshDataInstance> meshes) where T : class
        {
            float now = Time.time;
            foreach (var pair in (Dictionary<Vector3Int, T>)tiles)
            {
                T tile = pair.Value;
                if (now - PokeTime(tile) <= pathfinding.m_tileTimeout || ReferenceEquals(tile, building)) continue;
                foreach (var edge in new[] { EastLinks(tile), NorthLinks(tile) })
                {
                    foreach (var link in edge) links.Enqueue(link.Value);
                    edge.Clear();
                }
                if (Mesh(tile).valid)
                {
                    meshes.Enqueue(Mesh(tile));
                    Expired.Enqueue(new KeyValuePair<long, NavMeshData>(removedLinks + links.Count, Data(tile)));
                }
                else if (Data(tile) != null) UnityEngine.Object.Destroy(Data(tile));
                ((Dictionary<Vector3Int, T>)tiles).Remove(pair.Key);
                return;
            }
        }

        // Valheim 1.0.15: DestroyQueuedNavmeshData removes a fortieth of the queued links, or up to 25 within a millisecond,
        // and only when no link is queued, one queued tile. The same link removal now runs, then one tile whose links are gone.
        [HarmonyPrefix, HarmonyPatch("DestroyQueuedNavmeshData")]
        private static bool RemoveQueued(Queue<NavMeshLinkInstance> ___m_linkRemoveQueue, Queue<NavMeshDataInstance> ___m_tileRemoveQueue)
        {
            var links = ___m_linkRemoveQueue;
            if (links.Count > 0)
            {
                long started = Stopwatch.GetTimestamp(), budget = Stopwatch.Frequency / 1000;
                int minimum = links.Count / 40, removed = 0;
                while (links.Count > 0 && (removed < minimum || removed < 25 && Stopwatch.GetTimestamp() - started < budget))
                {
                    NavMesh.RemoveLink(links.Dequeue());
                    removed++;
                }
                removedLinks += removed;
                PathfindingRecords.Removed();
            }
            var tiles = ___m_tileRemoveQueue;
            // TimeoutTiles queues each mesh with its entry; a mesh queued some other way would go first, without one.
            bool paired = tiles.Count <= Expired.Count;
            if (tiles.Count == 0 || paired && Expired.Peek().Key > removedLinks) return false;
            NavMesh.RemoveNavMeshData(tiles.Dequeue());
            if (paired)
            {
                var data = Expired.Dequeue().Value;
                if (data != null) UnityEngine.Object.Destroy(data);
            }
            return false;
        }
    }
}
