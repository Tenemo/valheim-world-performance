using System;
using HarmonyLib;

namespace WorldPerformance
{
    // Once an hour the game unloads unused assets wherever the player is, one frame of 280 ms in Tenemo's game and up to
    // 470 ms in the test client. The sleep fade and waiting to respawn unload too once 20 minutes have passed since the last
    // unload, but a client of a dedicated server never pauses, and sleeping waits for every player in bed. The mod holds back
    // an hourly unload that falls due until the first natural pause: a frame in which the loading screen covers the whole view,
    // as while teleporting, sleeping, dead or waiting to respawn, or the game menu is open. It then calls the game's same
    // unload. If no pause comes before the next hourly check, that check unloads natively, so unloads stay at most about two
    // hours apart.
    internal static class DeferredAssetUnload
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".deferred-asset-unload");
        private static readonly AccessTools.FieldRef<Game, DateTime> lastUnloadOf = AccessTools.FieldRefAccess<Game, DateTime>("m_lastCollectResources");
        // The hourly check's own rule for an unload being due.
        private static readonly TimeSpan hour = TimeSpan.FromSeconds(3599.0);
        internal static bool Running { get; private set; }
        // Off unloads at the hourly check, as natively; the isolated client switches it for its checks.
        internal static bool Deferring { get; set; } = true;
        // An hourly unload waits for a natural pause.
        internal static bool Due { get; private set; }
        internal static long Deferred { get; private set; }
        internal static long Paused { get; private set; }
        internal static long Overdue { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            Due = false;
            patches.Patch(AccessTools.Method(typeof(Game), nameof(Game.CollectResourcesCheckPeriodic)), prefix: new HarmonyMethod(typeof(DeferredAssetUnload), nameof(Defer)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            Due = false;
        }

        // The hourly check holds back a due unload once; the next check unloads natively if it is still waiting.
        private static bool Defer(Game __instance)
        {
            if (!Deferring || DateTime.Now - hour <= lastUnloadOf(__instance)) return true;
            if (Due)
            {
                Due = false;
                Overdue++;
                return true;
            }
            Due = true;
            Deferred++;
            return false;
        }

        // Unloads in the first frame of a natural pause, unless the game has unloaded meanwhile.
        internal static void LateUpdate()
        {
            if (!Due) return;
            var game = Game.instance;
            if (!Deferring || !game || DateTime.Now - hour <= lastUnloadOf(game))
            {
                Due = false;
                return;
            }
            if (game.IsShuttingDown() || !NaturalPause()) return;
            Due = false;
            Paused++;
            game.CollectResources(displayMessage: true);
        }

        // Whether the player is in a natural pause: the loading screen covers the whole view, or the game menu is open.
        internal static bool NaturalPause()
        {
            var screen = Hud.instance ? Hud.instance.m_loadingScreen : null;
            return screen && screen.gameObject.activeInHierarchy && screen.alpha >= 1f || Menu.IsActive();
        }
    }
}
