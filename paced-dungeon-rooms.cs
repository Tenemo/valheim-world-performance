using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A player's game builds a dungeon's interior from its saved room list in the frame its last room model finishes
    // loading: 151-252 ms a dungeon in Tenemo's session of 2026-11-03. The rooms now go in over consecutive frames, at most
    // 4 ms of placing a frame, and the zone counts as loading until the last one, so teleports and respawns still wait for
    // the whole interior. Each room seeds its own random state from its position, so the interior comes out as it would in
    // one frame. A player inside that interior, teleporting or not yet spawned, and the dedicated server get the native
    // single frame.
    internal static class PacedDungeonRooms
    {
        // The isolated test client sets 0 to compare the native single frame.
        internal static float BudgetMilliseconds = 4f;
        internal static long Paced, Frames;
        private static readonly HashSet<DungeonGenerator> placing = new HashSet<DungeonGenerator>();
        private static readonly FieldInfo loadedRooms = AccessTools.Field(typeof(DungeonGenerator), "m_loadedRooms");
        private static readonly Type placement = AccessTools.Inner(typeof(DungeonGenerator), "RoomPlacementData");
        private static readonly FieldInfo roomData = AccessTools.Field(placement, "m_roomData"), position = AccessTools.Field(placement, "m_position"), rotation = AccessTools.Field(placement, "m_rotation");
        private static readonly Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room> placeRoom =
            AccessTools.MethodDelegate<Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room>>(
                AccessTools.Method(typeof(DungeonGenerator), "PlaceRoom", new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) }));
        private static readonly Action<DungeonGenerator> releaseHeldReferences = AccessTools.MethodDelegate<Action<DungeonGenerator>>(AccessTools.Method(typeof(DungeonGenerator), "ReleaseHeldReferences"));

        private static bool Native(DungeonGenerator generator)
        {
            var player = Player.m_localPlayer;
            if (BudgetMilliseconds <= 0f || ZNet.instance == null || ZNet.instance.IsDedicated() || player == null || player.IsTeleporting()) return true;
            Vector3 offset = player.transform.position - generator.transform.position;
            offset.y = 0f;
            return player.InInterior() && offset.sqrMagnitude < 128f * 128f;
        }

        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        private static class Spawn
        {
            private static bool Prefix(DungeonGenerator __instance)
            {
                if (Native(__instance)) return true;
                ZLog.Log("Spawning dungeon");
                placing.Add(__instance);
                __instance.StartCoroutine(Place(__instance));
                return false;
            }
        }

        // The game releases the room models and the zone's loading mark right after starting the spawn; that waits for the
        // last room unless the dungeon is destroyed first.
        [HarmonyPatch(typeof(DungeonGenerator), "ReleaseHeldReferences")]
        private static class Release
        {
            private static bool Prefix(DungeonGenerator __instance) => !placing.Contains(__instance);
        }

        [HarmonyPatch(typeof(DungeonGenerator), "OnDestroy")]
        private static class Destroyed
        {
            private static void Prefix(DungeonGenerator __instance) => placing.Remove(__instance);
        }

        private static IEnumerator Place(DungeonGenerator generator)
        {
            var rooms = (Array)loadedRooms.GetValue(generator);
            long budget = (long)(BudgetMilliseconds * Stopwatch.Frequency / 1000f), start = Stopwatch.GetTimestamp();
            Paced++;
            Frames++;
            for (int index = 0; index < rooms.Length; index++)
            {
                if (Stopwatch.GetTimestamp() - start > budget)
                {
                    yield return null;
                    if (!generator || !placing.Contains(generator)) yield break;
                    start = Stopwatch.GetTimestamp();
                    Frames++;
                }
                object room = rooms.GetValue(index);
                placeRoom(generator, (DungeonDB.RoomData)roomData.GetValue(room), (Vector3)position.GetValue(room), (Quaternion)rotation.GetValue(room), null, ZoneSystem.SpawnMode.Client);
            }
            SnapToGround.SnappAll();
            loadedRooms.SetValue(generator, null);
            placing.Remove(generator);
            releaseHeldReferences(generator);
            UnseenEffects.DungeonSpawned(generator);
        }
    }
}
