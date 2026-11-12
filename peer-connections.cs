using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace WorldPerformance
{
    // What Steam knows about each player's connection, read once a frame for each connection. The dedicated server talks
    // to Steam through its game server interface, a player's game through the client one. A player's delay is the round
    // trip plus the time the bytes already waiting for them need to leave, which is what update sizing, the ownership
    // order and the connection log all ask for; the bytes already in flight count among those waiting.
    public static class PeerConnections
    {
        private const int Unknown = -1;
        private static AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection> connection;
        private static AccessTools.FieldRef<object, ZNetPeer> peerOf;
        private static FieldInfo peerList;
        private static readonly Dictionary<uint, Reading> readings = new Dictionary<uint, Reading>();
        private static readonly Dictionary<Type, FieldInfo[]> wrappers = new Dictionary<Type, FieldInfo[]>();

        private struct Reading
        {
            internal int Frame;
            internal bool Known;
            internal SteamNetConnectionRealTimeStatus_t Status;
        }

        internal static void Initialize()
        {
            if (peerList != null) return;
            connection = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
            peerOf = AccessTools.FieldRefAccess<ZNetPeer>(AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), "m_peer");
            var field = AccessTools.Field(typeof(ZDOMan), "m_peers");
            if (field == null || !typeof(IList).IsAssignableFrom(field.FieldType)) throw new MissingFieldException("Valheim's list of update peers was not found.");
            peerList = field;
        }

        internal static HSteamNetConnection Connection(ZSteamSocket socket) => connection(socket);

        // The peers the server sends object updates to, in the order it serves them.
        internal static IList UpdatePeers() => ZDOMan.instance == null || peerList == null ? null : (IList)peerList.GetValue(ZDOMan.instance);

        internal static ZNetPeer Peer(object updatePeer) => peerOf(updatePeer);

        // Every mod that sends its configuration when a player joins replaces the player's socket with a buffering wrapper
        // of its own and leaves it there, so on this pack a player's socket is a stack of a dozen wrappers and not the
        // Steam connection. Each wrapper keeps the socket it replaced in a socket field, which leads down to Steam's own.
        internal static ZSteamSocket SteamSocket(ZNetPeer peer)
        {
            var socket = peer?.m_socket;
            for (int depth = 0; socket != null && depth < 64; depth++)
            {
                if (socket is ZSteamSocket steam) return steam;
                socket = Wrapped(socket);
            }
            return null;
        }

        private static ISocket Wrapped(ISocket socket)
        {
            var type = socket.GetType();
            if (!wrappers.TryGetValue(type, out var fields))
                wrappers[type] = fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => typeof(ISocket).IsAssignableFrom(field.FieldType)).ToArray();
            foreach (var field in fields)
                if (field.GetValue(socket) is ISocket wrapped && !ReferenceEquals(wrapped, socket)) return wrapped;
            return null;
        }

        internal static bool TryStatus(ZNetPeer peer, out SteamNetConnectionRealTimeStatus_t status) => TryStatus(SteamSocket(peer), out status);

        internal static bool TryStatus(ZSteamSocket socket, out SteamNetConnectionRealTimeStatus_t status)
        {
            status = default(SteamNetConnectionRealTimeStatus_t);
            if (socket == null || connection == null || ZNet.instance == null) return false;
            uint handle = connection(socket).m_HSteamNetConnection;
            if (readings.TryGetValue(handle, out var reading) && reading.Frame == Time.frameCount)
            {
                status = reading.Status;
                return reading.Known;
            }
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            var result = ZNet.instance.IsDedicated()
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection(socket), ref status, 0, ref lanes)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection(socket), ref status, 0, ref lanes);
            bool known = result == EResult.k_EResultOK && status.m_nPing >= 0 && status.m_nSendRateBytesPerSecond > 0;
            if (readings.Count > 64) readings.Clear();
            readings[handle] = new Reading { Frame = Time.frameCount, Known = known, Status = status };
            return known;
        }

        // The bytes of the connection's own queue, from Steam rather than through the socket, whose figure this mod and
        // Network Performance System both report as the game's smaller window would have left it.
        internal static int Waiting(SteamNetConnectionRealTimeStatus_t status) =>
            status.m_cbPendingReliable + status.m_cbPendingUnreliable + status.m_cbSentUnackedReliable;

        internal static int Delay(ZNetPeer peer)
        {
            if (!TryStatus(peer, out var status)) return Unknown;
            return status.m_nPing + (int)((long)Waiting(status) * 1000 / Math.Max(1, status.m_nSendRateBytesPerSecond));
        }

        // Milliseconds before what the server sends now reaches this player, or -1 when Steam has no measurement for the
        // connection. Progression Fix reads this to keep creatures off a player whose connection cannot carry them.
    }
}
