using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A shield generator that goes before any generator has found the camera's shield effect, as one that loads and unloads
    // before its network record arrives, throws in OnDestroy before leaving the game's list of shields, so the error was
    // logged and the destroyed generator stayed in the list, which every shield check walks, until a later check dropped it
    // with a warning. It now leaves the list as the game's own OnDestroy does, without the shield effect it never reached.
    internal static class ShieldRemoval
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".shield-removal");
        private static readonly AccessTools.FieldRef<ShieldDomeImageEffect> effect = AccessTools.StaticFieldRefAccess<ShieldDomeImageEffect>(AccessTools.Field(typeof(ShieldGenerator), "m_shieldDomeEffect"));
        private static readonly AccessTools.FieldRef<List<ShieldGenerator>> instances = AccessTools.StaticFieldRefAccess<List<ShieldGenerator>>(AccessTools.Field(typeof(ShieldGenerator), "m_instances"));
        private static readonly AccessTools.FieldRef<int> changes = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(ShieldGenerator), "m_instanceChangeID"));
        private static readonly AccessTools.FieldRef<ShieldGenerator, bool> ghost = AccessTools.FieldRefAccess<ShieldGenerator, bool>("m_isPlacementGhost");
        private static readonly AccessTools.FieldRef<ShieldGenerator, GameObject[]> lowLoops = AccessTools.FieldRefAccess<ShieldGenerator, GameObject[]>("m_lowLoopInstances");
        internal static bool Running { get; private set; }
        internal static int Removed { get; private set; }
        // Off runs the game's own OnDestroy, as natively; the isolated client switches it to compare.
        internal static bool Guarding { get; set; } = true;

        internal static void Start()
        {
            if (Running) return;
            patches.Patch(AccessTools.Method(typeof(ShieldGenerator), "OnDestroy"), prefix: new HarmonyMethod(typeof(ShieldRemoval), nameof(Leave)));
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchOwner();
        }

        // Valheim 1.0.21: if (!m_isPlacementGhost) { m_shieldDomeEffect.RemoveShield(this); m_instances.Remove(this); m_instanceChangeID++;
        // Character.SetupContinuousEffect(transform, transform.position, false, m_shieldLowLoop, ref m_lowLoopInstances); }
        private static bool Leave(ShieldGenerator __instance)
        {
            if (!Guarding || ghost(__instance) || (object)effect() != null) return true;
            instances().Remove(__instance);
            changes()++;
            Character.SetupContinuousEffect(__instance.transform, __instance.transform.position, false, __instance.m_shieldLowLoop, ref lowLoops(__instance));
            Removed++;
            return false;
        }
    }
}
