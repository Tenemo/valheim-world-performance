using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The Mistlands mist emitter adds one physics step (0.02 s) to its 0.1 s timer every frame instead of the frame's time,
    // so it runs every fifth frame, 29 times a second at 144 FPS, 10 at 50 and 6 at 30, and each run emits a tenth of a
    // second's mist. A run took 2.6 ms in the Mistlands in testing, where the mist stays at its particle limit at any of these
    // rates. Above 50 FPS the timer counts the frame's own time, so it runs 10 times a second, the rate its emission is set
    // for; at 50 FPS and below it runs as natively. Creatures' sight, hovering and the other mist checks read the mist zones
    // and Wisp light bubbles, not the particles, so only loose mist below the particle limit thins, to what 50 FPS shows.
    internal static class MistEmitterRate
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".mist-emitter-rate");
        internal static bool Running { get; private set; }
        // Off runs the emitter every fifth frame, as natively; the isolated client switches it for checks and paired measurements.
        internal static bool Pacing { get; set; } = true;

        internal static void Start()
        {
            if (Running) return;
            patches.Patch(AccessTools.Method(typeof(ParticleMist), "Update"), prefix: new HarmonyMethod(typeof(MistEmitterRate), nameof(Pace)));
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        // The game adds the physics step after this; taking back what the step exceeds the frame's time by leaves the frame's
        // time. A paused game's frames take no time, so the emitter waits with it.
        private static void Pace(ParticleSystem ___m_ps, ref float ___m_accumulator)
        {
            if (!Pacing) return;
            float surplus = Time.fixedDeltaTime - Time.deltaTime;
            if (surplus > 0f && ___m_ps.emission.enabled) ___m_accumulator -= surplus;
        }
    }
}
