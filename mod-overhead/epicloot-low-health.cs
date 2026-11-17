using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Epic Loot 0.15.6 sums the local player's regeneration and noise enchantments each frame through a callback it makes
    // anew for every sum, and names each effect's low-health version by joining "LowHealth" to the effect's name every time.
    // The same sums are now added in the same order without the callback, and each low-health name is made once. Another
    // mod's patch on these sums turns this off and fails mod-compatibility.
    internal static class EpicLootLowHealth
    {
        private delegate void Regen(Player player, string magicEffect, ref float regenValue);

        internal static long Summed;
        private static Func<Player, string, float, ItemDrop.ItemData, float> total;
        private static Func<Player, bool> lowHealth;
        private static Regen giantsBlood;
        private static AccessTools.FieldRef<string> noiseEffect;
        private static readonly AccessTools.FieldRef<SEMan, Character> character = AccessTools.FieldRefAccess<SEMan, Character>("m_character");
        private static readonly Dictionary<string, string> lowNames = new Dictionary<string, string>();
        private static readonly List<MethodBase> replaced = new List<MethodBase>();
        // 0 until the first sum reads the patches, then 1 when the sums run here, -1 when Epic Loot's own run.
        private static int ready;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var regen = assembly.GetType("EpicLoot.MagicItemEffects.ModifyPlayerRegen");
            var noise = assembly.GetType("EpicLoot.MagicItemEffects.ModifyNoise+ModifyNoise_SEMan_ModifyNoise_Patch");
            var low = assembly.GetType("EpicLoot.MagicItemEffects.ModifyWithLowHealth");
            var giants = assembly.GetType("EpicLoot.MagicItemEffects.GiantsBlood");
            var player = assembly.GetType("EpicLoot.PlayerExtensions");
            var effects = assembly.GetType("EpicLoot.MagicEffectType");
            var regenValue = regen == null ? null : AccessTools.Method(regen, "GetModifyRegenValue", new[] { typeof(Player), typeof(string) });
            var noisePostfix = noise == null ? null : AccessTools.Method(noise, "Postfix");
            var totalValue = player == null ? null : AccessTools.Method(player, "GetTotalActiveMagicEffectValue", new[] { typeof(Player), typeof(string), typeof(float), typeof(ItemDrop.ItemData) });
            var hasLowHealth = low == null ? null : AccessTools.Method(low, "PlayerHasLowHealth", new[] { typeof(Player) });
            var apply = low == null ? null : AccessTools.Method(low, "Apply");
            var lowName = low == null ? null : AccessTools.Method(low, "EffectNameWithLowHealth");
            var healthRegen = giants == null ? null : AccessTools.Method(giants, "ApplyHealthRegen");
            var noiseName = effects == null ? null : AccessTools.Field(effects, "ModifyNoise");
            if (regenValue == null || noisePostfix == null || totalValue == null || hasLowHealth == null || apply == null || lowName == null || healthRegen == null ||
                noiseName == null || !noiseName.IsStatic || noiseName.FieldType != typeof(string))
                throw new InvalidOperationException("Epic Loot's regeneration and noise sums changed.");
            total = AccessTools.MethodDelegate<Func<Player, string, float, ItemDrop.ItemData, float>>(totalValue);
            lowHealth = AccessTools.MethodDelegate<Func<Player, bool>>(hasLowHealth);
            giantsBlood = AccessTools.MethodDelegate<Regen>(healthRegen);
            noiseEffect = AccessTools.StaticFieldRefAccess<string>(noiseName);
            replaced.AddRange(new MethodBase[] { regenValue, noisePostfix, apply, lowName });
            harmony.Patch(regenValue, prefix: new HarmonyMethod(typeof(EpicLootLowHealth), nameof(RegenValue)));
            harmony.Patch(noisePostfix, prefix: new HarmonyMethod(typeof(EpicLootLowHealth), nameof(Noise)));
            log.LogInfo("Epic Loot's regeneration and noise enchantments are summed without new callbacks.");
        }

        private static bool Ready()
        {
            if (ready != 0) return ready > 0;
            var problems = replaced.Select(method => PatchSets.Unknown(method)).Where(problem => problem != null).ToList();
            ready = problems.Count == 0 ? 1 : -1;
            if (problems.Count > 0) Plugin.Fallback("EpicLootLowHealth unavailable: " + string.Join("; ", problems));
            return ready > 0;
        }

        // ModifyWithLowHealth.EffectNameWithLowHealth.
        private static string LowName(string name)
        {
            if (name == null) return name + "LowHealth";
            if (!lowNames.TryGetValue(name, out string low))
            {
                if (lowNames.Count >= 256) lowNames.Clear();
                lowNames[name] = low = name + "LowHealth";
            }
            return low;
        }

        // ModifyWithLowHealth.Apply(player, name, effect => value += player.GetTotalActiveMagicEffectValue(effect, 0.01f)).
        private static float Sum(Player player, string name)
        {
            Summed++;
            float value = 0f;
            value += total(player, name, 0.01f, null);
            if (lowHealth(player)) value += total(player, LowName(name), 0.01f, null);
            return value;
        }

        // ModifyPlayerRegen.GetModifyRegenValue.
        private static bool RegenValue(Player player, string magicEffect, ref float __result)
        {
            if (!Ready()) return true;
            float regenValue = Sum(player, magicEffect);
            giantsBlood(player, magicEffect, ref regenValue);
            __result = regenValue;
            return false;
        }

        // ModifyNoise_SEMan_ModifyNoise_Patch.Postfix(SEMan __instance, float baseNoise, ref float noise).
        private static bool Noise(SEMan __0, float __1, ref float __2)
        {
            if (!Ready()) return true;
            var owner = character(__0);
            if (owner.IsPlayer())
            {
                float noiseValue = Sum(owner as Player, noiseEffect());
                __2 -= __1 * Mathf.Clamp01(noiseValue);
            }
            return false;
        }
    }
}
