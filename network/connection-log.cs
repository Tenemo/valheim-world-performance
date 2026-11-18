using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // What a laggy fight looks like from the server, which the game itself records nowhere: how long its frames take, how
    // much of each player's share actually leaves, how much is waiting for them, and which player is simulating the
    // creatures. Three limits can each produce the same stutter, so a reading of all of them decides which one to raise.
    // A row for each player every five seconds costs nothing beside the game's own work and keeps a whole session. Each
    // row also asks the player's game how it ran since the last one, and waits up to four seconds for the answer.
    internal static class ConnectionLog
    {
        private const float SampleSeconds = 5f;
        internal const float AnswerSeconds = 4f;
        private enum Kind : byte { Other, Creature, Boss }
        private static AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> objects;
        private static readonly List<float> frames = new List<float>();
        private static readonly Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
        private static readonly List<ZDO> nearby = new List<ZDO>(), bossRecords = new List<ZDO>();
        private static readonly StringBuilder rows = new StringBuilder();
        // This row's players and their text so far, written once each has answered or the wait is over.
        private static readonly List<long> waiting = new List<long>();
        private static readonly List<string> pending = new List<string>();
        private static string folder;
        private static float nextSample, writeBy;
        private static int request;
        private static bool failed, reportedUnmeasured;

        // A boss altar's offering goes from the offering player's game through the server to the game that owns the altar,
        // which starts the boss's countdown and answers; the server passes these on, or drops one silently when the owner is
        // not a connected player. altars.csv records each with whom it went between, so a late or missing boss names the game
        // that ran its countdown.
        private static readonly Dictionary<int, string> altarRequests = new Dictionary<int, string>
        {
            ["RPC_SpawnBoss".GetStableHashCode()] = "offer",
            ["RPC_BossSpawnInitiated".GetStableHashCode()] = "accepted",
            ["RPC_RemoveBossSpawnInventoryItems".GetStableHashCode()] = "items taken",
        };

        internal static void Initialize(PerformancePatches patches)
        {
            PeerConnections.Initialize();
            objects = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
            patches.Patch(AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC"), prefix: new HarmonyMethod(typeof(ConnectionLog), nameof(Routed)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"), prefix: new HarmonyMethod(typeof(ConnectionLog), nameof(Handled)) { priority = Priority.First });
        }

        private static void Routed(ZRoutedRpc.RoutedRPCData __0) => WriteAltar(__0, false);
        private static void Handled(ZRoutedRpc.RoutedRPCData __0) => WriteAltar(__0, true);

        private static void WriteAltar(ZRoutedRpc.RoutedRPCData data, bool toServer)
        {
            if (data == null || !altarRequests.TryGetValue(data.m_methodHash, out string request) || failed || objects == null ||
                !Plugin.ConnectionLogging.Value || ZNet.instance == null || !ZNet.instance.IsDedicated() || ZDOMan.instance == null) return;
            try
            {
                Open();
                var altar = data.m_targetZDO.IsNone() ? null : ZDOMan.instance.GetZDO(data.m_targetZDO);
                var target = ZNet.instance.GetPeer(data.m_targetPeerID);
                // The server has no copy of an altar, so a request addressed to it goes no further.
                string delivery = toServer ? "kept by the server" : target == null ? "dropped, no such player" : target.IsReady() ? "passed on" : "dropped, not ready";
                var zone = altar == null ? default : ZoneSystem.GetZone(altar.GetPosition());
                rows.Length = 0;
                rows.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(',').Append(Quoted(request))
                    .Append(',').Append(Quoted(Name(data.m_senderPeerID)))
                    .Append(',').Append(Quoted(Name(data.m_targetPeerID)))
                    .Append(',').Append(Quoted(delivery))
                    .Append(',').Append(Quoted(altar == null ? "" : Name(altar.GetOwner())))
                    .Append(',').Append(altar == null ? "" : Count(zone.x))
                    .Append(',').Append(altar == null ? "" : Count(zone.y)).Append('\n');
                File.AppendAllText(Path.Combine(folder, "altars.csv"), rows.ToString());
                rows.Length = 0;
            }
            catch (Exception error)
            {
                failed = true;
                Plugin.Log.LogError("The connection log stopped: " + error.GetBaseException().Message);
            }
        }

        internal static void LateUpdate()
        {
            if (failed || objects == null || !Plugin.ConnectionLogging.Value || ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            frames.Add(Time.unscaledDeltaTime);
            try
            {
                if (waiting.Count > 0 && (Time.unscaledTime >= writeBy || AllAnswered())) Flush();
                if (Time.unscaledTime < nextSample) return;
                nextSample = Time.unscaledTime + SampleSeconds;
                var peers = PeerConnections.UpdatePeers();
                if (peers != null && peers.Count > 0 && ZDOMan.instance != null) Write(peers);
            }
            catch (Exception error)
            {
                failed = true;
                Plugin.Log.LogError("The connection log stopped: " + error.GetBaseException().Message);
            }
            frames.Clear();
        }

        private static void Open()
        {
            if (folder != null) return;
            folder = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "network-log",
                DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "connections.csv"),
                "time,players,frames,frame_mean_ms,frame_p95_ms,frame_max_ms,objects,player,delay_ms,ping_ms,quality_local,quality_remote,out_kib_s,in_kib_s,send_rate_kib_s,queued_bytes,queue_ms,creatures,bosses,zone_x,zone_z," + FrameReports.Columns + "\n");
            File.WriteAllText(Path.Combine(folder, "bosses.csv"), "time,boss,owner,creator,distance_m\n");
            File.WriteAllText(Path.Combine(folder, "altars.csv"), "time,request,sender,target,delivery,altar_owner,zone_x,zone_z\n");
            File.WriteAllText(Path.Combine(folder, "players.csv"), "time,player," + FrameReports.MachineColumns + "\n");
            Plugin.Log.LogInfo("The connection log is in " + folder + ".");
        }

        private static void Write(IList peers)
        {
            Open();
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            frames.Sort();
            string server = string.Join(",", new[]
            {
                time, Count(peers.Count), Count(frames.Count), Milliseconds(Mean()), Milliseconds(Percentile(0.95f)),
                Milliseconds(frames.Count == 0 ? 0f : frames[frames.Count - 1]), Count(objects(ZDOMan.instance).Count)
            });
            if (waiting.Count > 0) Flush();
            request++;
            foreach (var updatePeer in peers)
            {
                var peer = PeerConnections.Peer(updatePeer);
                if (peer == null) continue;
                bool known = PeerConnections.TryStatus(peer, out var status);
                if (!known && !reportedUnmeasured)
                {
                    reportedUnmeasured = true;
                    Plugin.Log.LogWarning($"Steam reports no connection status for {Name(peer.m_uid)}'s {peer.m_socket?.GetType().Name ?? "missing"} connection, so rate, ping and loss stay out of the log.");
                }
                CountSimulated(peer, out int creatures, out int bosses);
                var zone = ZoneSystem.GetZone(peer.GetRefPos());
                rows.Length = 0;
                rows.Append(server).Append(',').Append(Quoted(Name(peer.m_uid)))
                    .Append(',').Append(Count(PeerConnections.Delay(peer)))
                    .Append(',').Append(known ? Count(status.m_nPing) : "")
                    .Append(',').Append(known ? Fraction(status.m_flConnectionQualityLocal) : "")
                    .Append(',').Append(known ? Fraction(status.m_flConnectionQualityRemote) : "")
                    .Append(',').Append(known ? Kibibytes(status.m_flOutBytesPerSec) : "")
                    .Append(',').Append(known ? Kibibytes(status.m_flInBytesPerSec) : "")
                    .Append(',').Append(known ? Kibibytes(status.m_nSendRateBytesPerSecond) : "")
                    .Append(',').Append(known ? Count(PeerConnections.Waiting(status)) : "")
                    .Append(',').Append(known ? Count((int)(status.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000)) : "")
                    .Append(',').Append(Count(creatures)).Append(',').Append(Count(bosses))
                    .Append(',').Append(Count(zone.x)).Append(',').Append(Count(zone.y));
                pending.Add(rows.ToString());
                waiting.Add(peer.m_uid);
                FrameReports.Ask(peer, request);
            }
            writeBy = Time.unscaledTime + AnswerSeconds;
            rows.Length = 0;
            foreach (var data in bossRecords)
            {
                long owner = data.GetOwner();
                var peer = ZNet.instance.GetPeer(owner);
                float distance = peer == null ? -1f : Vector3.Distance(peer.GetRefPos(), data.GetPosition());
                // A record's ID carries the session of the game that created it, which stays when its owner changes.
                rows.Append(time).Append(',').Append(Quoted(ZNetScene.instance.GetPrefab(data.GetPrefab()).name)).Append(',').Append(Quoted(Name(owner)))
                    .Append(',').Append(Quoted(Name(data.m_uid.UserID)))
                    .Append(',').Append(distance.ToString("F0", CultureInfo.InvariantCulture)).Append('\n');
            }
            bossRecords.Clear();
            if (rows.Length > 0) File.AppendAllText(Path.Combine(folder, "bosses.csv"), rows.ToString());
        }

        // A player's machine and graphics settings, when they join and whenever they change.
        internal static void WriteMachine(long uid, string[] values)
        {
            if (folder == null || failed || !Plugin.ConnectionLogging.Value) return;
            try
            {
                var row = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(Quoted(Name(uid)));
                foreach (string value in values) row.Append(',').Append(Quoted(value.Replace('\n', ' ').Replace('\r', ' ')));
                File.AppendAllText(Path.Combine(folder, "players.csv"), row.Append('\n').ToString());
            }
            catch (Exception error)
            {
                failed = true;
                Plugin.Log.LogError("The connection log stopped: " + error.GetBaseException().Message);
            }
        }

        private static bool AllAnswered()
        {
            foreach (long uid in waiting)
                if (!FrameReports.Answered(uid, request)) return false;
            return true;
        }

        private static void Flush()
        {
            rows.Length = 0;
            for (int index = 0; index < waiting.Count; index++)
            {
                rows.Append(pending[index]).Append(',');
                FrameReports.Append(rows, waiting[index], request);
                rows.Append('\n');
            }
            waiting.Clear();
            pending.Clear();
            File.AppendAllText(Path.Combine(folder, "connections.csv"), rows.ToString());
            rows.Length = 0;
        }

        // A dedicated server instantiates no creatures, so the count comes from the records: those the player owns within
        // the area its game loads creatures in, as the game's ownership pass finds them. Bosses in that area are kept for
        // bosses.csv whoever owns them.
        private static void CountSimulated(ZNetPeer peer, out int creatures, out int bosses)
        {
            creatures = bosses = 0;
            var area = peer.m_simulationDistance;
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(peer.GetRefPos()), new SimulationDistance(area.NearSimulationDistance, 0, area.IsClassic), nearby);
            foreach (var data in nearby)
            {
                var kind = KindOf(data.GetPrefab());
                if (kind == Kind.Other) continue;
                if (kind == Kind.Boss && !bossRecords.Contains(data)) bossRecords.Add(data);
                if (data.GetOwner() != peer.m_uid) continue;
                creatures++;
                if (kind == Kind.Boss) bosses++;
            }
            nearby.Clear();
        }

        private static Kind KindOf(int prefab)
        {
            if (kinds.TryGetValue(prefab, out var kind)) return kind;
            var definition = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(prefab);
            if (definition == null) return Kind.Other;
            var character = definition.GetComponent<Character>();
            kinds[prefab] = kind = character == null || character is Player ? Kind.Other : character.IsBoss() ? Kind.Boss : Kind.Creature;
            return kind;
        }

        private static string Name(long uid)
        {
            if (uid == 0) return "";
            if (uid == ZDOMan.GetSessionID()) return "server";
            var peer = ZNet.instance.GetPeer(uid);
            return string.IsNullOrEmpty(peer?.m_playerName) ? uid.ToString(CultureInfo.InvariantCulture) : peer.m_playerName;
        }

        private static float Mean()
        {
            if (frames.Count == 0) return 0f;
            float total = 0f;
            foreach (float frame in frames) total += frame;
            return total / frames.Count;
        }

        private static float Percentile(float share) =>
            frames.Count == 0 ? 0f : frames[Mathf.Clamp((int)(frames.Count * share), 0, frames.Count - 1)];

        private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Milliseconds(float seconds) => (seconds * 1000f).ToString("F2", CultureInfo.InvariantCulture);
        private static string Kibibytes(float bytes) => (bytes / 1024f).ToString("F1", CultureInfo.InvariantCulture);
        private static string Fraction(float value) => value.ToString("F3", CultureInfo.InvariantCulture);
        private static string Quoted(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
