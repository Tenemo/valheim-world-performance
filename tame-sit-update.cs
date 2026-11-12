using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // TameRidingOverhaul 0.1.20 checks after every MonsterAI.UpdateAI whether the creature was told to sit, looking up its
    // Character and then its network view by component search each time. The AI and the creature hold both: the AI's
    // character is its own Character, and the creature's view is its own unless it has an override, which keeps the search.
    // A prefix on the sit update, such as Progression Fix's release of a sitting tame without a saddle, runs first as before;
    // any other patch on it turns this off and fails mod-compatibility.
    internal static class TameSitUpdate
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Standing, Sitting;
        private static readonly int sit = "uk.ainzworth.tameridingoverhaul.sit".GetStableHashCode();
        private static readonly AccessTools.FieldRef<BaseAI, Character> owner = AccessTools.FieldRefAccess<BaseAI, Character>("m_character");
        private static readonly AccessTools.FieldRef<Character, ZNetView> view = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
        private static Func<ConfigEntry<bool>> enabled, sitEnabled;
        private static Action<Character> update;
        private static MethodInfo sitUpdate;
        private static Action<Character>[] prefixes;
        // 0 until the first call looks at the sit update's patches, then 1 when its prefixes can run here, -1 when not.
        private static int ready;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.TameRidingOverhaul, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var config = assembly.GetType("Ainzworth.TameRidingOverhaul.Config.ModConfig");
            var system = assembly.GetType("Ainzworth.TameRidingOverhaul.Runtime.TameSitSystem");
            var patch = assembly.GetType("Ainzworth.TameRidingOverhaul.Patches.MonsterAISitUpdatePatch");
            MethodInfo Setting(string name) => config == null ? null : AccessTools.PropertyGetter(config, name);
            var enabledGetter = Setting("Enabled");
            var sitGetter = Setting("SitEnabled");
            sitUpdate = system == null ? null : AccessTools.Method(system, "Update", new[] { typeof(Character) });
            var postfix = patch == null ? null : AccessTools.Method(patch, "Postfix", new[] { typeof(MonsterAI) });
            if (enabledGetter == null || sitGetter == null || enabledGetter.ReturnType != typeof(ConfigEntry<bool>) || sitGetter.ReturnType != typeof(ConfigEntry<bool>) ||
                sitUpdate == null || postfix == null)
                throw new InvalidOperationException("TameRidingOverhaul's sit update changed.");
            enabled = AccessTools.MethodDelegate<Func<ConfigEntry<bool>>>(enabledGetter);
            sitEnabled = AccessTools.MethodDelegate<Func<ConfigEntry<bool>>>(sitGetter);
            update = AccessTools.MethodDelegate<Action<Character>>(sitUpdate);
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(TameSitUpdate), nameof(Updated)));
            log.LogInfo("TameRidingOverhaul's sit check reads the creature and its view from the AI.");
        }

        private static bool Updated(MonsterAI __0)
        {
            var ai = __0;
            if (!Shortcut) return true;
            if (!enabled().Value || !sitEnabled().Value || !ai) return false;
            var character = owner(ai);
            if (!character) return false;
            if (!Ready()) return true;
            // A view override is not what the mod's search finds, so that creature runs the mod's own update.
            if (character.m_nViewOverride) { update(character); return false; }
            foreach (var prefix in prefixes) prefix(character);
            if (character.HaveRider() || !IsSitting(character)) { Standing++; return false; }
            // The rest of TameSitSystem.Update for a sitting creature.
            Sitting++;
            var monster = character.GetComponent<MonsterAI>();
            if ((bool)monster && (bool)monster.GetFollowTarget())
            {
                monster.SetFollowTarget(null);
                monster.SetPatrolPoint();
            }
            character.SetMoveDir(Vector3.zero);
            character.SetRun(false);
            return false;
        }

        private static bool Ready()
        {
            if (ready != 0) return ready > 0;
            string problem = Problem();
            ready = problem == null ? 1 : -1;
            if (problem != null) Plugin.Fallback("TameSitUpdate unavailable: " + problem);
            return ready > 0;
        }

        // Read once all mods have loaded: Progression Fix loads after this mod. The prefixes must not be able to skip the
        // update and must take only the creature.
        internal static string Problem()
        {
            var info = Harmony.GetPatchInfo(sitUpdate);
            if (info == null) { prefixes = new Action<Character>[0]; return null; }
            if (info.Postfixes.Count > 0 || info.Transpilers.Count > 0 || info.Finalizers.Count > 0 || info.Prefixes.Count > 1)
                return "another patch changes TameRidingOverhaul's sit update";
            var methods = info.Prefixes.Select(patch => patch.PatchMethod).ToList();
            if (methods.Any(method => method.ReturnType != typeof(void) || method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(Character)))
                return "a prefix on TameRidingOverhaul's sit update can skip it or reads more than the creature";
            prefixes = methods.Select(method => AccessTools.MethodDelegate<Action<Character>>(method)).ToArray();
            return null;
        }

        // TameSitSystem.IsSitting with the creature's own view, which its Awake took from the same search.
        private static bool IsSitting(Character character)
        {
            var own = view(character);
            return ((bool)own && own.IsValid() ? own.GetZDO() : null)?.GetBool(sit) ?? false;
        }
    }
}
