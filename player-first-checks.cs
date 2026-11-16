using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Three mods' hooks on every character, or every physics step, act only for the local player but test that last:
    // Equipment and Quick Slots 3.2.1's death checks on every character's step, Epic Loot 0.15.6's blood block shards on
    // every humanoid's block update, twice, and AzuCraftyBoxes 1.8.28's container cleanup while teleporting on each step.
    // Each now first compares the character with the local player, or reads whether the local player teleports, and stops
    // there as its own later test would.
    internal static class PlayerFirstChecks
    {
        internal static long Skipped;
        private static readonly AccessTools.FieldRef<Player, bool> teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var slots))
            {
                var type = slots.Instance.GetType().Assembly.GetType("EquipmentAndQuickSlots.Slots");
                harmony.Patch(type == null ? null : AccessTools.Method(type, "IsValidPlayer", new[] { typeof(Character) }), prefix: new HarmonyMethod(typeof(PlayerFirstChecks), nameof(ValidPlayer)));
                log.LogInfo("Equipment and Quick Slots' death checks compare the character with the local player first.");
            }
            if (PluginIds.TryGet(PluginIds.EpicLoot, out var epicLoot))
            {
                var type = epicLoot.Instance.GetType().Assembly.GetType("EpicLoot.src.Magic.MagicItemEffects.Helpers.BloodBlockSelfDamage");
                harmony.Patch(type == null ? null : AccessTools.Method(type, "OnBlockStart"), prefix: new HarmonyMethod(typeof(PlayerFirstChecks), nameof(BlockStart)));
                log.LogInfo("Epic Loot's blood block shards compare the blocking character with the local player first.");
            }
            if (PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var boxes))
            {
                var type = boxes.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Patches.PlayerUpdateTeleportPatchCleanupContainers");
                harmony.Patch(type == null ? null : AccessTools.Method(type, "Prefix"), prefix: new HarmonyMethod(typeof(PlayerFirstChecks), nameof(Teleporting)));
                log.LogInfo("AzuCraftyBoxes' teleport cleanup reads whether the player teleports first.");
            }
        }

        // Slots.IsValidPlayer requires a living character that is the local player: another object is never that, and a
        // destroyed one fails the test for a living character.
        private static bool ValidPlayer(Character character, ref bool __result)
        {
            if (ReferenceEquals(character, Player.m_localPlayer)) return true;
            Skipped++;
            __result = false;
            return false;
        }

        // BloodBlockSelfDamage.OnBlockStart(instance, effectType) does nothing unless instance is the living local player.
        private static bool BlockStart(Humanoid instance)
        {
            if (ReferenceEquals(instance, Player.m_localPlayer)) return true;
            Skipped++;
            return false;
        }

        // The cleanup does nothing unless the local player teleports.
        private static bool Teleporting()
        {
            var player = Player.m_localPlayer;
            if (player != null && teleporting(player)) return true;
            Skipped++;
            return false;
        }
    }
}
