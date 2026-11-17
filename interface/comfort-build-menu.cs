using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // ComfortMe 1.3.1 redraws the comfort badge of every build menu button on every frame the menu is open, from its
    // Hud.UpdatePieceList postfix. A badge depends only on its piece and on ComfortMe's snapshot of the comfort around the
    // player, which ComfortMe takes again at most five times a second, or at once when the player moves or shelter changes.
    // The badges are now redrawn when that snapshot changes or the menu's buttons change, and look the same.
    internal static class ComfortChipRefresh
    {
        private static object drawnSnapshot;
        private static Hud drawnHud;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var badge = assembly.GetType("ComfortMe.BuildHudBadge");
            var snapshot = assembly.GetType("ComfortMe.ComfortSnapshot");
            var refresh = badge == null || snapshot == null ? null : AccessTools.Method(badge, "Refresh", new[] { typeof(Hud), snapshot });
            var draw = badge == null ? null : AccessTools.Method(badge, "RefreshBuildUi");
            var hide = badge == null ? null : AccessTools.Method(badge, "HideAll", new[] { typeof(Hud) });
            if (refresh == null || draw == null || hide == null) throw new InvalidOperationException("ComfortMe's build menu badges changed.");
            harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(ComfortChipRefresh), nameof(SnapshotChanged)));
            harmony.Patch(draw, postfix: new HarmonyMethod(typeof(ComfortChipRefresh), nameof(Drawn)));
            harmony.Patch(hide, postfix: new HarmonyMethod(typeof(ComfortChipRefresh), nameof(Forget)));
            // The game sets up the menu's buttons again when the category or the search changes.
            harmony.Patch(AccessTools.Method(typeof(BuildUi), "UpdatePieceButtons"), prefix: new HarmonyMethod(typeof(ComfortChipRefresh), nameof(Forget)));
            log.LogInfo("ComfortMe's build menu badges are redrawn only when comfort around the player changes.");
        }

        private static bool SnapshotChanged(Hud __0, object __1) => __1 == null || !ReferenceEquals(__1, drawnSnapshot) || __0 != drawnHud;

        private static void Drawn(Hud __0, object __1)
        {
            drawnHud = __0;
            drawnSnapshot = __1;
        }

        private static void Forget() => drawnSnapshot = null;
    }

    // ComfortMe 1.3.1 hides the group plate behind every build menu button on every frame its comfort tab is not selected,
    // also while the menu is closed: for each button it reads its fields by reflection, reads image names, which makes
    // garbage, and looks the plate up by name, about 0.6 ms a frame. A plate shows only after ComfortMe lifts it, so the
    // buttons it lifted are remembered and the hiding is skipped while there are none; every plate stays as ComfortMe leaves it.
    internal static class ComfortPlateHiding
    {
        private static readonly HashSet<BuildUiPieceButton> lifted = new HashSet<BuildUiPieceButton>();
        // Plates lifted before the patches went in are unknown, so the first hiding runs.
        private static bool known;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.ComfortMe, out var plugin)) return;
            var highlight = plugin.Instance.GetType().Assembly.GetType("ComfortMe.GroupHoverHighlight");
            var lift = highlight == null ? null : AccessTools.Method(highlight, "SetLift", new[] { typeof(BuildUiPieceButton), typeof(bool) });
            var hide = highlight == null ? null : AccessTools.Method(highlight, "HideAll", Type.EmptyTypes);
            if (lift == null || hide == null) throw new InvalidOperationException("ComfortMe's group plates changed.");
            harmony.Patch(lift, postfix: new HarmonyMethod(typeof(ComfortPlateHiding), nameof(Lifted)));
            harmony.Patch(hide, prefix: new HarmonyMethod(typeof(ComfortPlateHiding), nameof(AnyLifted)));
            log.LogInfo("ComfortMe hides its build menu plates only while one is lifted.");
        }

        // Lifting shows the button's plate; lowering hides it.
        private static void Lifted(BuildUiPieceButton __0, bool __1)
        {
            if (__1) lifted.Add(__0);
            else lifted.Remove(__0);
        }

        // Hiding lowers the plate of each button in the menu; a lifted button that has left the menu keeps it running.
        private static bool AnyLifted()
        {
            if (!known) { known = true; return true; }
            lifted.RemoveWhere(button => !button);
            return lifted.Count > 0;
        }
    }
}
