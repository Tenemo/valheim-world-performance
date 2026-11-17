using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;

namespace WorldPerformance
{
    // Valheim sends each connection at a fixed 150 KB/s and starts no new object updates for a player while 10,240 bytes of
    // earlier ones wait unacknowledged, so a player's download falls with ping: about 110 KB/s at 40 ms, 75 KB/s at 60 to
    // 100 ms and 38 KB/s at 200 ms, while a busy base streams more. The waiting limit now covers what the connection sends
    // in its ping plus 100 ms, the time the server takes to come back to the player, between 10,240 bytes and 256 KiB. The
    // dedicated server sends each player at the configured rate, and never more than an equal share of the configured
    // upload budget: Steam's connections have no bandwidth estimation, so a combined rate above the server's upload only
    // fills the router's queue and raises everyone's ping. Players' games keep sending to the server at the game's 150 KB/s.
    internal static class SendWindow
    {
        private const int GameWindow = 10240;
        private const int LargestWindow = 256 * 1024;
        private const int ReturnMilliseconds = 100;
        private const int SmallestRate = 64;
        private static ManualLogSource logger;
        private static MethodInfo networkBudget;
        private static int sharedRate;
        private static bool handedOverRate, sendingUpdates;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            logger = log;
            PeerConnections.Initialize();
            var budgetType = AccessTools.TypeByName("DIT.BetterNetworking10.QueueBudgetPatch");
            networkBudget = budgetType == null ? null : AccessTools.Method(budgetType, "GetBudget", Type.EmptyTypes);
            if (budgetType != null && (networkBudget == null || !networkBudget.IsStatic || networkBudget.ReturnType != typeof(int)))
                throw new InvalidOperationException("Better Networking's object update budget changed.");
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "SendZDOs"), transpiler: new HarmonyMethod(typeof(SendWindow), nameof(SizeWindow)),
                prefix: new HarmonyMethod(typeof(SendWindow), nameof(BeginSending)), finalizer: new HarmonyMethod(typeof(SendWindow), nameof(EndSending)));
            harmony.Patch(AccessTools.Method(typeof(ZSteamSocket), nameof(ZSteamSocket.GetSendQueueSize)),
                postfix: new HarmonyMethod(typeof(SendWindow), nameof(ReportGameBacklog)));
            harmony.Patch(AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks"),
                postfix: new HarmonyMethod(typeof(SendWindow), nameof(ShareUpload)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.AddPeer)), postfix: new HarmonyMethod(typeof(SendWindow), nameof(ShareUpload)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.RemovePeer)), postfix: new HarmonyMethod(typeof(SendWindow), nameof(ShareUpload)));
        }

        // Network Performance System's window replaces the same two limits, so this runs first and leaves it none, also when
        // Better Networking removes its budget at shutdown and Harmony applies the remaining transpilers again.
        [HarmonyAfter("DIT.BetterNetworking10"), HarmonyBefore(ForeignOwnership.NetworkPerformance)]
        private static IEnumerable<CodeInstruction> SizeWindow(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var limits = code.Where(x => x.Is(OpCodes.Ldc_I4, GameWindow)).ToList();
            var replaced = networkBudget == null ? new List<CodeInstruction>() : code.Where(x => x.Calls(networkBudget)).ToList();
            // Better Networking 1.3 replaces the same two limits with its configured budget, even when disabled.
            if (limits.Count == 0 && replaced.Count == 2) limits = replaced;
            else if (limits.Count != 2 || replaced.Count != 0)
                throw new InvalidOperationException("Object update sending changed; expected two native or Better Networking budget limits.");
            foreach (var limit in limits)
            {
                // The second limit is a branch target, so the instruction that takes its place keeps its labels.
                limit.opcode = OpCodes.Ldarg_1;
                limit.operand = null;
                code.Insert(code.IndexOf(limit) + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SendWindow), nameof(WindowFor))));
            }
            return code;
        }

        // The game sets its rate when the dedicated server opens a Steam socket, before any player connects, and a
        // connection keeps the rate it was created with, so each player's share is applied again whenever the number of
        // players changes. A refused setting leaves the game's rate and must not stop the server opening.
        private static void ShareUpload()
        {
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
            if (ForeignOwnership.SetsSendRate())
            {
                if (!handedOverRate)
                {
                    handedOverRate = true;
                    logger.LogInfo("Network Performance System paces the connections, so it sets the rate each player is sent at; update windows still follow each connection.");
                }
                return;
            }
            var peers = PeerConnections.UpdatePeers();
            int players = Math.Max(1, peers == null ? 0 : peers.Count);
            int share = Math.Max(SmallestRate, Math.Min(Plugin.ServerSendRate.Value, Plugin.UploadBudget.Value / players));
            if (share == sharedRate) return;
            bool applied = SetRate(ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, share);
            if (peers != null)
                foreach (var updatePeer in peers)
                {
                    var socket = PeerConnections.SteamSocket(PeerConnections.Peer(updatePeer));
                    if (socket == null) continue;
                    applied &= SetRate(ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                        new IntPtr((long)PeerConnections.Connection(socket).m_HSteamNetConnection), share);
                }
            if (!applied) logger.LogError("Steam refused the server send rate; players keep the rate their connections already had.");
            else
            {
                sharedRate = share;
                logger.LogInfo($"Object updates are sent to each of {players} player(s) at {share} KiB/s.");
            }
        }

        private static bool SetRate(ESteamNetworkingConfigScope scope, IntPtr target, int kibibytes)
        {
            var rate = GCHandle.Alloc(kibibytes * 1024, GCHandleType.Pinned);
            try
            {
                // The maximum first, so the minimum never exceeds it.
                return Set(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, scope, target, rate) &&
                    Set(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, scope, target, rate);
            }
            finally { rate.Free(); }
        }

        private static bool Set(ESteamNetworkingConfigValue setting, ESteamNetworkingConfigScope scope, IntPtr target, GCHandle rate) =>
            SteamGameServerNetworkingUtils.SetConfigValue(setting, scope, target, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                rate.AddrOfPinnedObject());

        private static int WindowFor(object updatePeer) => Window(PeerConnections.SteamSocket(PeerConnections.Peer(updatePeer)));

        private static int Window(ZSteamSocket socket)
        {
            if (!PeerConnections.TryStatus(socket, out var status)) return GameWindow;
            long window = (long)status.m_nSendRateBytesPerSecond * (status.m_nPing + ReturnMilliseconds) / 1000;
            return (int)Math.Min(LargestWindow, Math.Max(GameWindow, window));
        }

        private static void BeginSending() => sendingUpdates = true;

        private static void EndSending() => sendingUpdates = false;

        // Every mod that sends its own configuration or a saved character waits for a player's connection to hold less
        // than 20,000 bytes before sending the next part, and disconnects that player after thirty seconds if it never
        // does. This window deliberately leaves more than that waiting on a distant player's connection, so every reader
        // but the sending above sees the backlog the game's own window would have left. Network Performance System makes
        // the same adjustment for its window and stands it down whenever that window is not applied, which it is not here.
        private static void ReportGameBacklog(ZSteamSocket __instance, ref int __result)
        {
            if (sendingUpdates || __result <= GameWindow) return;
            __result = Math.Max(0, __result - (Window(__instance) - GameWindow));
        }
    }
}
