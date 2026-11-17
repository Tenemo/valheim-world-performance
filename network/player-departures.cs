using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // When a player leaves, the dedicated server stops for everyone: it waits 100 ms after flushing the player's socket
    // so Steam can still send what was queued, and it checks all three million records for objects nobody may own any
    // more, about 100 ms. Steam's linger now keeps sending the queued data after the close without the wait, except while
    // the server shuts down. The check runs over the world's sectors in the following frames, and the leaving player's
    // character, the record other players see, goes at once as before.
    internal static class PlayerDepartures
    {
        // The isolated test server's helper sets these false to compare the native departure.
        internal static bool Lingering = true;
        internal static bool Paced = true;
        private const int SectorsPerFrame = 8192;
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly System.Func<ZDOMan, long, bool> connected =
            AccessTools.MethodDelegate<System.Func<ZDOMan, long, bool>>(AccessTools.Method(typeof(ZDOMan), "IsPeerConnected"));
        private static int nextSector = -1;
        private static ZDOID leaving = ZDOID.None;

        private static bool Dedicated() => ZNet.instance != null && ZNet.instance.IsDedicated() && Game.instance != null && !Game.instance.IsShuttingDown();

        [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.Close))]
        private static class SocketClose
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                int sleep = code.FindIndex(instruction => instruction.Calls(AccessTools.Method(typeof(System.Threading.Thread), nameof(System.Threading.Thread.Sleep), new[] { typeof(int) })));
                int close = code.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == "CloseConnection" &&
                    method.GetParameters().LastOrDefault()?.ParameterType == typeof(bool));
                if (sleep < 0 || close < 0) throw new System.InvalidOperationException("The game's socket close changed.");
                code[sleep] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlayerDepartures), nameof(Wait))).MoveLabelsFrom(code[sleep]);
                // The close's last argument turns the linger on. Server Characters already sets it in players' games, so the
                // argument is combined rather than replaced.
                code.Insert(close, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlayerDepartures), nameof(KeepSending))).MoveLabelsFrom(code[close]));
                return code;
            }
        }

        private static void Wait(int milliseconds) { if (!Linger()) System.Threading.Thread.Sleep(milliseconds); }

        private static bool KeepSending(bool linger) => linger || Linger();

        private static bool Linger() => Lingering && Dedicated();

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemovePeer))]
        private static class Departure
        {
            private static void Prefix(ZNetPeer netPeer) => leaving = netPeer?.m_characterID ?? ZDOID.None;

            private static void Finalizer() => leaving = ZDOID.None;
        }

        [HarmonyPatch(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS")]
        private static class Sweep
        {
            private static bool Prefix(ZDOMan __instance)
            {
                if (!Paced || !Dedicated()) return true;
                var character = __instance.GetZDO(leaving);
                if (character != null) Abandoned(__instance, character);
                nextSector = 0;
                return false;
            }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Update))]
        private static class Continue
        {
            private static void Postfix(ZDOMan __instance)
            {
                if (nextSector < 0) return;
                var lists = sectors(__instance);
                int end = System.Math.Min(lists.Length, nextSector + SectorsPerFrame);
                for (int sector = nextSector; sector < end; sector++)
                {
                    var list = lists[sector];
                    if (list == null) continue;
                    // Destruction only queues the record, so the list stays as it is while it is read.
                    foreach (var record in list) Abandoned(__instance, record);
                }
                nextSector = end < lists.Length ? end : -1;
            }
        }

        // The game's own test and destruction for one record.
        private static void Abandoned(ZDOMan manager, ZDO record)
        {
            if (record.Persistent || (record.HasOwner() && connected(manager, record.GetOwner()))) return;
            ZLog.Log("Destroying abandoned non persistent zdo " + record.m_uid.ToString() + " owner " + record.GetOwner());
            record.SetOwner(ZDOMan.GetSessionID());
            manager.DestroyZDO(record);
        }
    }
}
