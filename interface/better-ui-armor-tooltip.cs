using System;
using HarmonyLib;

namespace WorldPerformance
{
    // BetterUI rebuilds its armour tooltip's text every frame the inventory is open, but a tooltip reads its text only when
    // the pointer or the gamepad comes to rest on it. The text is now built when that happens and while it is shown.
    internal static class ArmorTooltipText
    {
        // The shipped feature; the isolated client switches it off and on for checks and paired measurements.
        internal static bool Lean = true;
        private static Func<UITooltip> tooltip;
        private static Action<Player> build;
        private static readonly AccessTools.FieldRef<UITooltip> Current = AccessTools.StaticFieldRefAccess<UITooltip>(AccessTools.Field(typeof(UITooltip), "m_current"));
        private static Player waiting;
        private static bool building;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            var armor = plugin.Instance.GetType().Assembly.GetType("BetterUI.Patches.InventoryArmorTooltip", true);
            var field = AccessTools.Field(armor, "tooltip");
            var update = AccessTools.DeclaredMethod(armor, "Update", new[] { typeof(Player) });
            if (field?.FieldType != typeof(UITooltip) || update == null) throw new InvalidOperationException("BetterUI's armour tooltip changed.");
            tooltip = () => (UITooltip)field.GetValue(null);
            build = AccessTools.MethodDelegate<Action<Player>>(update);
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(ArmorTooltipText), nameof(Later)));
            harmony.Patch(AccessTools.Method(typeof(UITooltip), nameof(UITooltip.OnHoverStart)), prefix: new HarmonyMethod(typeof(ArmorTooltipText), nameof(Shown)));
        }

        private static bool Later(Player player)
        {
            if (!Lean || building) return true;
            var shown = tooltip();
            if (shown == null || Current() == shown) return true;
            waiting = player;
            return false;
        }

        private static void Shown(UITooltip __instance)
        {
            if (waiting == null || __instance != tooltip()) return;
            var player = waiting;
            waiting = null;
            building = true;
            try { build(player); }
            finally { building = false; }
        }
    }
}
