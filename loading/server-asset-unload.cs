using System;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Once an hour the dedicated server unloads unused assets on its main thread whoever is playing: about a second at the
    // median and up to 9.3 s, while every player waited for the server, and on the production server it freed none to a few
    // assets most hours, since a server loads hardly any. The server now holds the hourly unload while players are connected
    // and makes it once nobody has been connected for a minute, or once it has waited 12 hours. Unloads the game makes for
    // other reasons stay native.
    internal static class ServerAssetUnload
    {
        private static readonly TimeSpan EmptyFor = TimeSpan.FromSeconds(60), Longest = TimeSpan.FromHours(12);
        // The isolated test server switches the hold off to compare, and can shorten the wait for an empty server.
        internal static bool Holding = true;
        internal static TimeSpan EmptyWait = EmptyFor;
        internal static long Held, Unloaded;
        private static bool owed, running;
        private static DateTime owedSince, emptySince;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.CollectResourcesCheckPeriodic)), prefix: new HarmonyMethod(typeof(ServerAssetUnload), nameof(Check)));
            running = true;
        }

        private static int Players => ZNet.instance == null ? 0 : ZNet.instance.GetConnectedPeers().Count;

        private static bool Check()
        {
            if (!Holding || owed && DateTime.Now - owedSince >= Longest || Players == 0) { owed = false; return true; }
            if (!owed)
            {
                owed = true;
                owedSince = DateTime.Now;
                Plugin.Log.LogInfo("The hourly unload of unused assets waits until no player is connected.");
            }
            Held++;
            return false;
        }

        internal static void LateUpdate()
        {
            if (!running || !owed) return;
            var now = DateTime.Now;
            if (Players > 0 && now - owedSince < Longest) { emptySince = now; return; }
            if (Players == 0 && now - emptySince < EmptyWait) return;
            owed = false;
            Unloaded++;
            if (Game.instance != null) Game.instance.CollectResources(displayMessage: true);
        }
    }
}
