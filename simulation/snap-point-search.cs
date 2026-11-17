using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // While a building piece is held, the game looks for the closest pair of snap points between the placement ghost and the
    // pieces within 10 m every frame. For every piece collider in reach it finds the collider's piece and lists that piece's
    // snap points by testing the tag of each child, once per collider, and then reads every listed point's position again for
    // each of the ghost's own snap points. At Tenemo's base that search was most of the 1 ms a frame the hammer adds to
    // LateUpdate. Here each collider's piece and each piece's snap points are kept, and each point's position is read once a
    // search. Pieces are visited in the game's order and compared with the game's distances, and a point another collider of
    // the same piece would list again can never be strictly closer than its first listing, so the chosen pair, the result and
    // the list of nearby pieces equal the game's. Only the internal list of candidate points no longer repeats them.
    internal static class SnapPointSearch
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".snap-point-search");
        private static readonly AccessTools.FieldRef<Player, List<Transform>> ghostPointLists = AccessTools.FieldRefAccess<Player, List<Transform>>("m_tempSnapPoints1");
        private static readonly AccessTools.FieldRef<Player, List<Transform>> candidateLists = AccessTools.FieldRefAccess<Player, List<Transform>>("m_tempSnapPoints2");
        private static readonly AccessTools.FieldRef<Player, List<Piece>> pieceLists = AccessTools.FieldRefAccess<Player, List<Piece>>("m_tempPieces");
        private static readonly AccessTools.FieldRef<Player, int> manualPoints = AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");
        // The game's own buffer size, so a crowded search keeps the same colliders.
        private static readonly Collider[] colliders = new Collider[2000];
        private static readonly Dictionary<int, Piece> colliderPieces = new Dictionary<int, Piece>();
        private static readonly Dictionary<int, Transform[]> piecePoints = new Dictionary<int, Transform[]>();
        private static readonly HashSet<int> visited = new HashSet<int>();
        private static readonly List<Transform> listed = new List<Transform>();
        private static Vector3[] positions = new Vector3[1024];
        private static int mask;
        internal static bool Running { get; private set; }
        // Off searches natively; the isolated client switches it for checks and paired measurements.
        internal static bool Searching = true;
        internal static long Searches;

        internal static void Start()
        {
            if (Running) return;
            mask = LayerMask.GetMask("piece", "piece_nonsolid");
            var original = AccessTools.Method(typeof(Player), "FindClosestSnapPoints", new[] { typeof(Transform), typeof(float), typeof(Transform).MakeByRefType(), typeof(Transform).MakeByRefType(), typeof(List<Piece>) })
                ?? throw new MissingMethodException("Player.FindClosestSnapPoints changed.");
            patches.Patch(original, prefix: new HarmonyMethod(typeof(SnapPointSearch), nameof(Search)));
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            colliderPieces.Clear();
            piecePoints.Clear();
        }

        private static bool Search(Player __instance, Transform ghost, float maxSnapDistance, ref Transform a, ref Transform b, ref bool __result)
        {
            if (!Searching) return true;
            Searches++;
            var ghostPoints = ghostPointLists(__instance);
            ghostPoints.Clear();
            ghost.GetComponent<Piece>().GetSnapPoints(ghostPoints);
            var candidates = candidateLists(__instance);
            candidates.Clear();
            var pieces = pieceLists(__instance);
            pieces.Clear();
            // Destroyed pieces leave their entries behind; a long session rebuilds the tables now and then.
            if (colliderPieces.Count > 20000) { colliderPieces.Clear(); piecePoints.Clear(); }
            int count = Physics.OverlapSphereNonAlloc(ghost.position, 10f, colliders, mask);
            visited.Clear();
            for (int index = 0; index < count; index++)
            {
                var collider = colliders[index];
                int id = collider.GetInstanceID();
                if (!colliderPieces.TryGetValue(id, out var piece) || !piece) colliderPieces[id] = piece = collider.GetComponentInParent<Piece>();
                if (!piece) continue;
                pieces.Add(piece);
                if (!visited.Add(piece.GetInstanceID())) continue;
                var points = PointsOf(piece);
                if (candidates.Count + points.Length > positions.Length) Array.Resize(ref positions, (candidates.Count + points.Length) * 2);
                foreach (var point in points)
                {
                    positions[candidates.Count] = point.position;
                    candidates.Add(point);
                }
            }
            a = null;
            b = null;
            int manual = manualPoints(__instance);
            if (manual >= 0)
            {
                var closest = Closest(candidates, ghostPoints[manual].position, maxSnapDistance, out _);
                if (closest == null)
                {
                    __result = false;
                    return false;
                }
                a = ghostPoints[manual];
                b = closest;
                __result = true;
                return false;
            }
            float best = 9999999f;
            foreach (var point in ghostPoints)
            {
                var closest = Closest(candidates, point.position, maxSnapDistance, out float distance);
                if (closest != null && distance < best)
                {
                    best = distance;
                    a = point;
                    b = closest;
                }
            }
            __result = a != null;
            return false;
        }

        // The game's comparison: the first candidate wins a tie.
        private static Transform Closest(List<Transform> candidates, Vector3 point, float maxDistance, out float distance)
        {
            Transform closest = null;
            distance = 999999f;
            for (int index = 0; index < candidates.Count; index++)
            {
                float candidate = Vector3.Distance(positions[index], point);
                if (!(candidate > maxDistance) && candidate < distance)
                {
                    closest = candidates[index];
                    distance = candidate;
                }
            }
            return closest;
        }

        // A piece's snap points are its tagged children; a kept list with a destroyed point is read again.
        private static Transform[] PointsOf(Piece piece)
        {
            int id = piece.GetInstanceID();
            if (piecePoints.TryGetValue(id, out var points) && Array.TrueForAll(points, point => point)) return points;
            listed.Clear();
            piece.GetSnapPoints(listed);
            piecePoints[id] = points = listed.ToArray();
            return points;
        }
    }
}
