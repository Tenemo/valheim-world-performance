using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Epic Loot 0.15.6 runs its six animation speed handlers after every character's animation update in every physics step.
    // Its wrapper reads the animator's speed twice and allocates a closure, a delegate and an array enumerator for each call.
    // The attack speed, magic and bow fire rate, shield throw, launch throw and enemy slow handlers change the speed only while
    // the character attacks, and the stagger duration handler only while it staggers, so for a character doing neither the
    // wrapper now returns at once.
    internal static class AttackSpeedHandlers
    {
        // Off runs Epic Loot's wrapper for every character; the isolated client switches it for paired measurements.
        internal static bool Lean = true;
        // Set once the game's start has shown that Epic Loot's own wrapper carries exactly its six handlers.
        internal static bool Active { get; private set; }
        internal static long Skipped;
        private static FieldInfo handlers, external;
        private static MethodInfo wrapper;
        private static Type attackSpeed;
        private static readonly HashSet<MethodInfo> expected = new HashSet<MethodInfo>();

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            if (plugin.Metadata.Version.ToString() != "0.15.6")
                throw new InvalidOperationException("Epic Loot " + plugin.Metadata.Version + "'s animation speed handlers are unread.");
            var assembly = plugin.Instance.GetType().Assembly;
            var manager = assembly.GetType("EpicLoot.AnimationSpeedManager", true);
            wrapper = AccessTools.Method(manager, "wrapper", new[] { typeof(Character), typeof(Animator) });
            handlers = AccessTools.Field(manager, "handlers");
            external = AccessTools.Field(manager, "_useExternal");
            attackSpeed = assembly.GetType("EpicLoot.MagicItemEffects.ModifyAttackSpeed_ApplyAnimationHandler_Patch", true);
            foreach (var (type, name) in new[] { ("ModifyStaggerDuration_AnimationHandler_Patch", "ModifyStaggerSpeed"), ("ModifyEnemyAttackSpeed_AnimationHandler_Patch", "ModifyAttackSpeed"),
                ("ModifyMagicFireRate", "ModifyFireAnimationSpeed"), ("OverwhelmingLaunch", "ModifyThrowSpeed"), ("ShieldThrow", "ModifyThrowSpeed") })
                expected.Add(AccessTools.Method(assembly.GetType("EpicLoot.MagicItemEffects." + type, true), name, new[] { typeof(Character), typeof(double) }) ??
                    throw new MissingMethodException("Epic Loot's " + type + " handler changed."));
            if (wrapper == null || handlers == null || external == null) throw new MissingMemberException("Epic Loot's animation speed manager changed.");
            harmony.Patch(wrapper, prefix: new HarmonyMethod(typeof(AttackSpeedHandlers), nameof(Skip)));
            // The only writer of the handler list; Epic Loot adds its handlers when the game starts.
            harmony.Patch(AccessTools.Method(manager, "Add"), postfix: new HarmonyMethod(typeof(AttackSpeedHandlers), nameof(Verify)));
            harmony.Patch(AccessTools.Method(typeof(Game), "Awake"), postfix: new HarmonyMethod(typeof(AttackSpeedHandlers), nameof(Started)) { priority = Priority.Last });
        }

        // The handlers of Epic Loot's single priority group, if each is one of its six.
        private static int Known()
        {
            var groups = (Array)handlers.GetValue(null);
            if (groups.Length != 1) return -1;
            var methods = ((Array)groups.GetValue(0)).Cast<Delegate>().Select(handler => handler.Method).ToList();
            bool known = methods.All(method => expected.Contains(method) || method.DeclaringType?.DeclaringType == attackSpeed) && methods.Distinct().Count() == methods.Count;
            return known ? methods.Count : -1;
        }

        private static void Verify()
        {
            if (Active && Known() != 6) Stop("Epic Loot's animation speed handlers changed after the game started.");
        }

        private static void Started()
        {
            if (Active) return;
            var patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(CharacterAnimEvent), "CustomFixedUpdate"));
            if (!Equals(external.GetValue(null), false) || patches == null || !patches.Postfixes.Any(patch => patch.PatchMethod == wrapper))
                Stop("Epic Loot routes its animation speed handlers elsewhere.");
            else if (Known() != 6) Stop("Epic Loot's animation speed handlers changed.");
            else Active = true;
        }

        private static void Stop(string reason)
        {
            Active = false;
            Plugin.Fallback(reason);
        }

        // Each handler returns the speed it is given unless the character attacks or staggers, and the wrapper then writes nothing.
        private static bool Skip(Character __0)
        {
            if (!Lean || !Active || !__0 || __0.InAttack() || __0.IsStaggering()) return true;
            Skipped++;
            return false;
        }
    }
}
