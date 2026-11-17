using System;
using System.Collections.Generic;
using HarmonyLib;

namespace WorldPerformance
{
    // Every vine segment runs an update each frame, and it does something only while growth is queued, which locations and
    // catch-up growth do, or once after its berries appear or go, when it compares their state with the one it last saw and
    // the owner's game rechecks which neighbours may hold berries. Ashlands ruins carry hundreds of segments, 0.3 ms of the
    // owner's frame there. A segment with no queued growth whose berries are as its last update saw them is switched off after
    // that update. Everything that can change either switches it on again: its berries being picked, regrowing, enabled or
    // disabled, its shape changing and queued growth. Timed growth and support checks are invoked, so they keep running while
    // a segment is off, and a segment switched on updates in the same or the next frame, as the game's own update order varies.
    internal static class IdleVines
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".idle-vines");
        // The segments this feature switched off, by instance, so it never switches on one that something else switched off.
        private static readonly HashSet<int> paused = new HashSet<int>();
        private static List<Vine> vines;
        internal static bool Running { get; private set; }
        // Off keeps every segment updating, as natively; the isolated client switches it for checks and paired measurements.
        internal static bool Pausing { get; private set; } = true;
        internal static long Paused, Woken;

        internal static void Start()
        {
            if (Running) return;
            var vine = typeof(Vine);
            var pickable = typeof(Pickable);
            vines = AccessTools.Field(vine, "s_allVines")?.GetValue(null) as List<Vine> ?? throw new MissingFieldException("Vine.s_allVines changed.");
            try
            {
                // Everything that wakes a segment goes on before anything switches one off.
                var wakeVine = new HarmonyMethod(typeof(IdleVines), nameof(WakeVine));
                var wakePickable = new HarmonyMethod(typeof(IdleVines), nameof(WakePickable));
                patches.Patch(AccessTools.Method(pickable, nameof(Pickable.SetPicked)), postfix: wakePickable);
                patches.Patch(AccessTools.Method(pickable, nameof(Pickable.SetEnabled), new[] { typeof(int) }), postfix: wakePickable);
                patches.Patch(AccessTools.Method(pickable, "UpdateRespawn"), postfix: wakePickable);
                patches.Patch(AccessTools.Method(vine, "SetType"), postfix: wakeVine);
                patches.Patch(AccessTools.Method(vine, nameof(Vine.UpdateGrow)), postfix: new HarmonyMethod(typeof(IdleVines), nameof(Grown)));
                patches.Patch(AccessTools.Method(vine, "Grow") ?? throw new MissingMethodException("Vine.Grow changed."),
                    prefix: new HarmonyMethod(typeof(IdleVines), nameof(Growing)), postfix: new HarmonyMethod(typeof(IdleVines), nameof(GrewChild)));
                patches.Patch(AccessTools.Method(vine, "OnDestroy"), postfix: new HarmonyMethod(typeof(IdleVines), nameof(Destroyed)));
                patches.Patch(AccessTools.Method(vine, nameof(Vine.Update)), postfix: new HarmonyMethod(typeof(IdleVines), nameof(Updated)));
            }
            catch
            {
                patches.UnpatchSelf();
                throw;
            }
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            WakeAll();
        }

        internal static void SetPausing(bool value)
        {
            Pausing = value;
            if (!value) WakeAll();
        }

        internal static string Report() => $"segments off {paused.Count}, switched off {Paused}, switched on {Woken}";

        private static void WakeAll()
        {
            foreach (var vine in vines)
                if (vine && paused.Remove(vine.GetInstanceID())) vine.enabled = true;
            paused.Clear();
        }

        private static void Wake(Vine vine)
        {
            if (!paused.Remove(vine.GetInstanceID())) return;
            vine.enabled = true;
            Woken++;
        }

        // The game compares the berries with their last state and runs queued growth in this update; with neither left, the
        // next update could do nothing until one of the patched calls below changes them.
        private static void Updated(Vine __instance, Pickable ___m_pickable, bool ___m_lastPickable)
        {
            if (!Pausing || __instance.m_initialGrowItterations > 0 || ___m_pickable.CanBePicked() != ___m_lastPickable) return;
            __instance.enabled = false;
            paused.Add(__instance.GetInstanceID());
            Paused++;
        }

        private static void WakePickable(Pickable __instance)
        {
            if (paused.Count > 0 && __instance.TryGetComponent(out Vine vine)) Wake(vine);
        }

        private static void WakeVine(Vine __instance) => Wake(__instance);

        // Timed growth that fell behind queues the missed steps for the update.
        private static void Grown(Vine __instance)
        {
            if (__instance.m_initialGrowItterations > 0) Wake(__instance);
        }

        // A segment grows by copying itself, so one switched off copies that too, and timed growth and catch-up queued by an
        // invoked check grow while it is off. Natively the parent is on, so its new segment is switched on here; the segment
        // joins the list of all vines in its Awake during the copy.
        private static void Growing(Vine __instance, out int __state) => __state = paused.Contains(__instance.GetInstanceID()) ? vines.Count : -1;

        private static void GrewChild(int __state)
        {
            if (__state < 0) return;
            for (int index = __state; index < vines.Count; index++)
                if (vines[index] && !vines[index].enabled) vines[index].enabled = true;
        }

        private static void Destroyed(Vine __instance) => paused.Remove(__instance.GetInstanceID());
    }
}
