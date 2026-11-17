using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A portal trip keeps the loading screen up for at least 8 s, because a player's game cannot tell whether the server
    // has sent the destination yet and its own area check passes on buildings it has not heard of. The player's game now
    // asks the server, which answers once the zones of the game's near area around the destination exist and it has sent
    // each of their building and terrain records; the answer follows those records on the same connection. With the answer,
    // the trip ends once the game's own area check passes and nothing in that near area is still being created or loading,
    // such as a dungeon's rooms. Without one, as from a server without this mod, the 8 s stay. Joining at the logout point
    // and respawning at a bed hold the screen for the same 8 s, and end the same way.
    internal static class ArrivalConfirmation
    {
        // The isolated test client sets false to compare the native wait.
        internal static bool Confirming = true;
        private const string Request = "WorldPerformance ArrivalRequest", Answer = "WorldPerformance ArrivalSent";
        private static readonly AccessTools.FieldRef<ZoneSystem, SimulationDistance> simulation = AccessTools.FieldRefAccess<ZoneSystem, SimulationDistance>("m_simulationDistance");
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, List<ZDO>>> loading =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<Vector2s, List<ZDO>>>("m_loadingObjectsInZones");
        private static readonly List<(ZNetPeer peer, int id, Vector2s zone, float since)> waiting = new List<(ZNetPeer, int, Vector2s, float)>();
        private static readonly List<ZDO> records = new List<ZDO>();
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> generated = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
        private static System.Reflection.FieldInfo peerRecords;
        private static Func<object, ZDOID, bool> known;
        private static int asked, answered, lastLoading, spawnAsked;
        private static readonly AccessTools.FieldRef<Game, float> respawnWait = AccessTools.FieldRefAccess<Game, float>("m_respawnWait");
        private static readonly AccessTools.FieldRef<Game, bool> respawnAfterDeath = AccessTools.FieldRefAccess<Game, bool>("m_respawnAfterDeath");
        private static float nextCheck;
        // The isolated test client sets false to leave the reference position to the game's 2 s update.
        internal static bool SendNow = true;
        private static readonly Action<ZNet, ZNetPeer> sendPlayerData = AccessTools.MethodDelegate<Action<ZNet, ZNetPeer>>(AccessTools.Method(typeof(ZNet), "SendServerSyncPlayerData"));

        private static bool Knows<TInfo>(object map, ZDOID id) => ((Dictionary<ZDOID, TInfo>)map).ContainsKey(id);

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connection
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer()) peer.m_rpc.Register<int, Vector3>(Request, (rpc, id, position) => Asked(peer, id, position));
                else peer.m_rpc.Register<int>(Answer, (rpc, id) => answered = id);
            }
        }

        private static void Asked(ZNetPeer peer, int id, Vector3 position)
        {
            waiting.RemoveAll(entry => entry.peer == peer);
            waiting.Add((peer, id, ZoneSystem.GetZone(position), Time.unscaledTime));
        }

        // After the update's records went out, so that an answer follows them.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update))]
        private static class Sent
        {
            private static void Postfix(ZDOMan __instance)
            {
                if (waiting.Count == 0 || Time.unscaledTime < nextCheck) return;
                nextCheck = Time.unscaledTime + 0.1f;
                if (known == null)
                {
                    PeerConnections.Initialize();
                    peerRecords = AccessTools.Field(AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), "m_zdos");
                    known = (Func<object, ZDOID, bool>)Delegate.CreateDelegate(typeof(Func<object, ZDOID, bool>),
                        AccessTools.Method(typeof(ArrivalConfirmation), nameof(Knows)).MakeGenericMethod(peerRecords.FieldType.GetGenericArguments()[1]));
                }
                var peers = PeerConnections.UpdatePeers();
                for (int index = waiting.Count - 1; index >= 0; index--)
                {
                    var entry = waiting[index];
                    object updatePeer = null;
                    if (peers != null)
                        foreach (var candidate in peers)
                            if (PeerConnections.Peer(candidate) == entry.peer) { updatePeer = candidate; break; }
                    if (updatePeer == null || Time.unscaledTime - entry.since > 60f) waiting.RemoveAt(index);
                    else if (AllSent(__instance, updatePeer, entry.zone))
                    {
                        entry.peer.m_rpc.Invoke(Answer, entry.id);
                        waiting.RemoveAt(index);
                    }
                }
            }
        }

        private static bool AllSent(ZDOMan manager, object updatePeer, Vector2s zone)
        {
            var zones = generated(ZoneSystem.instance);
            var setting = simulation(ZoneSystem.instance);
            int near = setting.NearSimulationDistance;
            for (int y = zone.y - near; y <= zone.y + near; y++)
                for (int x = zone.x - near; x <= zone.x + near; x++)
                {
                    var other = new Vector2s(x, y);
                    if ((setting.IsClassic || ZoneSystem.instance.ZonesWithinRadius(zone, other, near, false)) && !zones.Contains(other)) return false;
                }
            records.Clear();
            manager.FindSectorObjects(zone, new SimulationDistance(near, 0, setting.IsClassic), records);
            object map = peerRecords.GetValue(updatePeer);
            bool sent = true;
            foreach (var record in records)
                if (record.Type >= ZDO.ObjectType.Solid && !known(map, record.m_uid)) { sent = false; break; }
            records.Clear();
            return sent;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static class Started
        {
            private static void Postfix(Player __instance, Vector3 pos, bool distantTeleport, bool __result)
            {
                if (!Confirming || !__result || !distantTeleport || __instance != Player.m_localPlayer || ZNet.instance == null || ZNet.instance.IsServer()) return;
                ZNet.instance.GetServerPeer()?.m_rpc?.Invoke(Request, ++asked, pos);
            }
        }

        // The last frame an object was created or a zone finished loading, such as a dungeon placing its rooms.
        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        private static class Created
        {
            private static void Postfix() => lastLoading = Time.frameCount;
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.UnsetLoadingInZone))]
        private static class Loaded
        {
            private static void Postfix() => lastLoading = Time.frameCount;
        }

        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        private static class Arrived
        {
            // Skips only the game's fixed floor; the game's area check and floor search still decide when the trip ends.
            private static void Prefix(Player __instance, bool ___m_teleporting, bool ___m_distantTeleport, ref float ___m_teleportTimer, Vector3 ___m_teleportTargetPos)
            {
                if (Confirming && ___m_teleporting && ___m_distantTeleport && asked != 0 && answered == asked && ___m_teleportTimer > 2f && ___m_teleportTimer < 8f &&
                    __instance == Player.m_localPlayer && Time.frameCount - lastLoading > 2 && ZoneSystem.instance.IsActiveAreaLoaded() && Settled(___m_teleportTargetPos) &&
                    ZNetScene.instance.IsAreaReady(___m_teleportTargetPos))
                    ___m_teleportTimer = 8f;
            }
        }

        [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
        private static class Spawning
        {
            // Asks at a respawn's first search and skips only the game's fixed floor, as for a portal trip; the game's area
            // check and its ground and bed checks still decide.
            private static void Prefix(Game __instance)
            {
                if (!Confirming || ZNet.instance == null || ZNet.instance.IsServer() || ZNetScene.instance == null || ZoneSystem.instance == null) return;
                var profile = __instance.GetPlayerProfile();
                Vector3 point;
                if (!respawnAfterDeath(__instance) && profile.HaveLogoutPoint()) point = profile.GetLogoutPoint();
                else if (profile.HaveCustomSpawnPoint()) point = profile.GetCustomSpawnPoint();
                else return;
                float wait = respawnWait(__instance);
                if (wait == 0f)
                {
                    var server = ZNet.instance.GetServerPeer();
                    if (server?.m_rpc == null) { spawnAsked = 0; return; }
                    // The game sends the server its reference position every 2 s, and the server sends and hands out the
                    // world around the last one it heard; the search sets it to the spawn point now, and the game's own
                    // update carries it to the server at once instead of up to 2 s later.
                    if (SendNow) { ZNet.instance.SetReferencePosition(point); sendPlayerData(ZNet.instance, server); }
                    server.m_rpc.Invoke(Request, spawnAsked = ++asked, point);
                    return;
                }
                if (spawnAsked != 0 && answered == spawnAsked && wait > 2f && wait < __instance.m_respawnLoadDuration && Time.frameCount - lastLoading > 2 &&
                    ZoneSystem.instance.IsActiveAreaLoaded() && Settled(point) && ZNetScene.instance.IsAreaReady(point))
                    respawnWait(__instance) = __instance.m_respawnLoadDuration;
            }
        }

        // No zone of the near area is waiting for a location's or a dungeon's models or rooms.
        private static bool Settled(Vector3 target)
        {
            var zone = ZoneSystem.GetZone(target);
            int near = simulation(ZoneSystem.instance).NearSimulationDistance;
            foreach (var marked in loading(ZoneSystem.instance).Keys)
                if (Math.Abs(marked.x - zone.x) <= near && Math.Abs(marked.y - zone.y) <= near) return false;
            return true;
        }
    }
}
