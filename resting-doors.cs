using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A door's or gate's animator evaluates in every physics step, also while the door stands open or closed, where its
    // controller plays a clip without length: a single key, a pose that never changes. An animator found at rest at two of
    // its door's own state checks in a row, 0.2 s apart, is switched off with its state kept: its pose, its parameter and
    // its state stay as they are, and the door answers hover and use from that state as natively. It is switched on again
    // just before the door sets a new state, whether the player, another player or the door's check of its network record
    // changed it, so the opening or closing plays natively from the next physics step. At rest means a single layer without
    // root motion, out of any transition, in a state tagged open or closed that matches the door's state parameter and
    // plays only clips without length; other doors stay native.
    internal static class RestingDoors
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".resting-doors");
        private static readonly AccessTools.FieldRef<Door, Animator> animatorOf = AccessTools.FieldRefAccess<Door, Animator>("m_animator");
        private static readonly int stateParameter = Animator.StringToHash("state");
        private static readonly List<AnimatorClipInfo> clips = new List<AnimatorClipInfo>();
        // Animators found at rest at their door's last check, and those switched off, with whether they kept their state
        // when switched off natively.
        private static readonly HashSet<Animator> settling = new HashSet<Animator>();
        private static readonly Dictionary<Animator, bool> resting = new Dictionary<Animator, bool>();
        private static readonly List<Animator> destroyed = new List<Animator>();
        private static bool pausing = true;
        private static int checks;
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        internal static int Paused => resting.Count;

        // Off plays every resting animator and pauses none, as natively; the isolated client switches it for paired measurements.
        internal static bool Pausing
        {
            get => pausing;
            set
            {
                pausing = value;
                if (!value) WakeAll();
            }
        }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(Door), "UpdateState"), postfix: new HarmonyMethod(typeof(RestingDoors), nameof(Checked)));
            patches.Patch(AccessTools.Method(typeof(Door), "SetState"), prefix: new HarmonyMethod(typeof(RestingDoors), nameof(Changing)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            WakeAll();
        }

        // After each of the door's checks of its network record, five times a second.
        private static void Checked(Door __instance)
        {
            try
            {
                if (++checks % 1024 == 0) Forget();
                var animator = animatorOf(__instance);
                if (!pausing || !animator || !animator.isActiveAndEnabled) return;
                if (!AtRest(animator))
                {
                    settling.Remove(animator);
                    return;
                }
                if (settling.Add(animator)) return;
                settling.Remove(animator);
                resting[animator] = animator.keepAnimatorStateOnDisable;
                animator.keepAnimatorStateOnDisable = true;
                animator.enabled = false;
            }
            catch (Exception error) { Fail(error); }
        }

        private static bool AtRest(Animator animator)
        {
            if (animator.layerCount != 1 || animator.applyRootMotion || animator.writeDefaultValuesOnDisable || animator.IsInTransition(0)) return false;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            bool closed = state.IsTag("closed");
            if (!closed && !state.IsTag("open") || closed != (animator.GetInteger(stateParameter) == 0)) return false;
            animator.GetCurrentAnimatorClipInfo(0, clips);
            bool still = clips.Count > 0;
            foreach (var clip in clips) still &= clip.clip && clip.clip.length == 0f;
            clips.Clear();
            return still;
        }

        // Before the door sets a state its animator does not hold, the animator plays again, so the change runs natively.
        private static void Changing(Door __instance, int state)
        {
            try
            {
                var animator = animatorOf(__instance);
                if (!animator) return;
                settling.Remove(animator);
                if (resting.ContainsKey(animator) && animator.GetInteger(stateParameter) != state) Wake(animator);
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Wake(Animator animator)
        {
            bool kept = resting[animator];
            resting.Remove(animator);
            animator.enabled = true;
            animator.keepAnimatorStateOnDisable = kept;
        }

        private static void WakeAll()
        {
            foreach (var entry in resting)
                if (entry.Key)
                {
                    entry.Key.enabled = true;
                    entry.Key.keepAnimatorStateOnDisable = entry.Value;
                }
            resting.Clear();
            settling.Clear();
        }

        // Doors destroyed with their zones leave entries behind.
        private static void Forget()
        {
            foreach (var animator in resting.Keys) if (!animator) destroyed.Add(animator);
            foreach (var animator in destroyed) resting.Remove(animator);
            destroyed.Clear();
            settling.RemoveWhere(animator => !animator);
        }

        private static void Fail(Exception error)
        {
            if (Failure != null) return;
            Failure = error.ToString();
            Plugin.Log?.LogError("Pausing resting door animators failed and stopped; every door animator plays natively. " + error);
            try { Stop(); }
            catch (Exception restoring) { Plugin.Log?.LogError(restoring); }
        }
    }
}
