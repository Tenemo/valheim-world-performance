using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;
using NetworkingUtils;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Joining a dedicated server by its address first asks PlayFab whether a crossplay server is listening there, and the
    // game connects through Steam only once PlayFab answers that none is: about 7 s of every join to a server without
    // crossplay. The game now keeps each address it has joined through Steam under BepInEx/cache, and for those addresses
    // the search ends at once without an answer, as the game's own search does for a local address, so the join goes
    // through Steam as it would after PlayFab's answer. A join that the search skipped and that fails before the server
    // accepts the player forgets the address, so the next join asks PlayFab again, as for a server that has turned on
    // crossplay since. The server list's status checks still ask PlayFab.
    internal static class SteamServerJoins
    {
        // The isolated test client sets false to compare the native search.
        internal static bool Skipping = true;
        internal static int Skipped;
        private const int Kept = 16;
        private static readonly FieldInfo serverHost = AccessTools.Field(typeof(ZNet), "m_serverHost");
        private static readonly FieldInfo serverHostPort = AccessTools.Field(typeof(ZNet), "m_serverHostPort");
        private static List<string> known;
        // The address of a join the search skipped, until the server accepts the player.
        private static string unconfirmed;

        private static string FilePath => Path.Combine(Paths.CachePath, "WorldPerformance", "steam-servers.txt");

        internal static void Initialize(PerformancePatches patches)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            string problem = Problem();
            if (problem != null) throw new InvalidOperationException(problem);
            patches.Patch(AccessTools.Method(typeof(ZPlayFabMatchmaking), "FindHostByIp"), prefix: new HarmonyMethod(typeof(SteamServerJoins), nameof(Search)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"), postfix: new HarmonyMethod(typeof(SteamServerJoins), nameof(Accepted)));
            patches.Patch(AccessTools.Method(typeof(FejdStartup), "ShowConnectError"), prefix: new HarmonyMethod(typeof(SteamServerJoins), nameof(Failed)));
        }

        // The join's search is the one call that asks to join the lobby it finds, and its failure callback connects through
        // Steam. A game update that searches another way or connects elsewhere after a failed search fails this check
        // instead of leaving the join on an untested path.
        private static string Problem()
        {
            var search = AccessTools.Method(typeof(ZPlayFabMatchmaking), "FindHostByIp");
            var setHost = AccessTools.Method(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(string), typeof(int), typeof(OnlineBackendType) });
            if (search == null || !search.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(new[] { typeof(IPEndPoint), typeof(ZPlayFabMatchmakingSuccessCallback), typeof(ZPlayFabMatchmakingFailedCallback), typeof(bool) }))
                return "ZPlayFabMatchmaking.FindHostByIp changed.";
            if (setHost == null || serverHost?.FieldType != typeof(string) || serverHostPort?.FieldType != typeof(int) ||
                AccessTools.Method(typeof(ZNet), "RPC_PeerInfo") == null || AccessTools.Method(typeof(FejdStartup), "ShowConnectError", new[] { typeof(ZNet.ConnectionStatus) }) == null)
                return "the server address, the server's peer information or the join error changed.";
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var joins = typeof(FejdStartup).GetNestedTypes(all).Where(type => type.Name.StartsWith("<>c", StringComparison.Ordinal))
                .SelectMany(type => type.GetMethods(all)).Where(method => method.Name.Contains("<JoinServer>") && method.GetMethodBody() != null)
                .Select(method => PatchProcessor.GetOriginalInstructions(method)).Where(code => code.Any(instruction => instruction.Calls(search))).ToList();
            if (joins.Count != 1) return "the join no longer searches PlayFab once.";
            var join = joins[0];
            int call = join.FindIndex(instruction => instruction.Calls(search));
            if (!join[call - 1].LoadsConstant(1)) return "the join's search no longer asks to join the lobby.";
            int failed = join.FindIndex(instruction => instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo constructor &&
                constructor.DeclaringType == typeof(ZPlayFabMatchmakingFailedCallback));
            if (failed < 1 || failed > call || join[failed - 1].opcode != OpCodes.Ldftn || !(join[failed - 1].operand is MethodInfo onFailure))
                return "the join's search no longer has its own failure callback.";
            var failure = PatchProcessor.GetOriginalInstructions(onFailure);
            int connect = failure.FindIndex(instruction => instruction.Calls(setHost));
            if (connect < 1 || !failure[connect - 1].LoadsConstant((int)OnlineBackendType.Steamworks)) return "a failed search no longer connects through Steam.";
            return null;
        }

        private static bool Search(IPEndPoint hostIp, ZPlayFabMatchmakingFailedCallback failedAction, bool joinLobby)
        {
            if (!joinLobby || failedAction == null) return true;
            unconfirmed = null;
            string address = Address(hostIp.m_address.ToString(), hostIp.m_port);
            if (!Skipping || !Known().Contains(address)) return true;
            unconfirmed = address;
            Skipped++;
            Plugin.Log.LogInfo("Joining through Steam without asking PlayFab: this game joined the server at this address through Steam before.");
            failedAction(ZPLayFabMatchmakingFailReason.Cancelled);
            return false;
        }

        // The server sends its peer information once it has accepted the player.
        private static void Accepted(ZNet __instance)
        {
            if (__instance.IsServer() || ZNet.m_onlineBackend != OnlineBackendType.Steamworks) return;
            string host = (string)serverHost.GetValue(null);
            if (string.IsNullOrEmpty(host)) return;
            unconfirmed = null;
            string address = Address(host, (int)serverHostPort.GetValue(null));
            var list = Known();
            if (list.Count > 0 && list[0] == address) return;
            list.Remove(address);
            list.Insert(0, address);
            if (list.Count > Kept) list.RemoveRange(Kept, list.Count - Kept);
            Save(list);
        }

        // The menu shows why a join ended; an override or the connection's own status, as the game reads it.
        private static void Failed(ZNet.ConnectionStatus statusOverride)
        {
            if (unconfirmed == null) return;
            var status = statusOverride != ZNet.ConnectionStatus.None ? statusOverride : ZNet.GetConnectionStatus();
            if (status == ZNet.ConnectionStatus.None || status == ZNet.ConnectionStatus.Connecting || status == ZNet.ConnectionStatus.Connected) return;
            var list = Known();
            if (list.Remove(unconfirmed)) Save(list);
            Plugin.Log.LogInfo("The next join to this address asks PlayFab again: the join through Steam alone failed (" + status + ").");
            unconfirmed = null;
        }

        private static string Address(string host, int port) => host + ":" + port;

        internal static List<string> Known()
        {
            if (known != null) return known;
            known = new List<string>();
            try
            {
                if (File.Exists(FilePath))
                    known.AddRange(File.ReadAllLines(FilePath).Select(line => line.Trim()).Where(line => line.Length > 0).Distinct().Take(Kept));
            }
            catch (Exception error) { Plugin.Log.LogWarning("Could not read the servers joined through Steam: " + error.Message); }
            return known;
        }

        private static void Save(List<string> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, list);
            }
            catch (Exception error) { Plugin.Log.LogWarning("Could not keep the servers joined through Steam: " + error.Message); }
        }
    }
}
