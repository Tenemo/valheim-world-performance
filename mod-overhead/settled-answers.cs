using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Two mods add to the game's own window checks, which the game and other mods ask many times a frame: Epic Loot 0.15.6
    // also counts its compendium's search field as the chat having focus, and AAA_Crafting 2.1.12 counts its recipe search
    // field as the menu being open. Each looked up its field even when the game's answer already decided the result: the
    // chat without focus stays without it, and an open menu stays open. Those lookups are now skipped.
    internal static class SettledAnswers
    {
        internal static long Skipped;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (PluginIds.TryGet(PluginIds.EpicLoot, out var epicLoot))
            {
                var chat = epicLoot.Instance.GetType().Assembly.GetType("EpicLoot.Chat_HasFocus_Patch");
                harmony.Patch(chat == null ? null : AccessTools.Method(chat, "Postfix"), prefix: new HarmonyMethod(typeof(SettledAnswers), nameof(ChatFocused)));
                log.LogInfo("Epic Loot's chat focus check reads its search field only while the chat has focus.");
            }
            if (PluginIds.TryGet(PluginIds.AzuAntiArthriticCrafting, out var crafting))
            {
                var menu = crafting.Instance.GetType().Assembly.GetType("AzuAntiArthriticCrafting.Patches.PaginatorPatches+MenuIsVisiblePatch");
                harmony.Patch(menu == null ? null : AccessTools.Method(menu, "Postfix"), prefix: new HarmonyMethod(typeof(SettledAnswers), nameof(MenuHidden)));
                log.LogInfo("AAA_Crafting's menu check reads its search field only while the menu is closed.");
            }
        }

        // Epic Loot's postfix: __result &= !MagicPages.InSearchField().
        private static bool ChatFocused(ref bool __0)
        {
            if (__0) return true;
            Skipped++;
            return false;
        }

        // AAA_Crafting's postfix: __result |= the recipe search field has focus.
        private static bool MenuHidden(ref bool __0)
        {
            if (!__0) return true;
            Skipped++;
            return false;
        }
    }
}
