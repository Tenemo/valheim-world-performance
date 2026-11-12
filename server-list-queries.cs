using System;
using System.Collections;
using HarmonyLib;

namespace WorldPerformance
{
    // FastLink's prewarm also runs after the menu scene has gone, starting fresh Steam queries throughout gameplay.
    // Cancel the menu's remaining queries once on leaving it, then resume ordinary status checks when it returns.
    internal static class ServerListQueries
    {
        private static Action clear;
        private static IDictionary retries;
        private static bool paused;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.FastLink, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var status = assembly.GetType("FastLink.Net.ServerStatusService", true);
            var prewarm = AccessTools.Method(assembly.GetType("FastLink.Util.QuickJoin", true), "Prewarm");
            var reset = AccessTools.Method(status, "ClearCache");
            retries = AccessTools.Field(status, "_retries")?.GetValue(null) as IDictionary;
            if (reset == null || retries == null) throw new InvalidOperationException("FastLink's status queries changed.");
            clear = AccessTools.MethodDelegate<Action>(reset);
            var prefix = new HarmonyMethod(typeof(ServerListQueries), nameof(MenuOnly));
            harmony.Patch(prewarm, prefix: prefix);
            harmony.Patch(AccessTools.Method(status, "Tick"), prefix: prefix);
        }

        private static bool MenuOnly()
        {
            if (FejdStartup.instance && !Game.instance) { paused = false; return true; }
            if (!paused) { paused = true; clear(); retries.Clear(); }
            return false;
        }
    }
}
