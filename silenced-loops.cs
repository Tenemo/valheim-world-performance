using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game lets only the nearest few sounds of each kind of looping sound play aloud, one more than the kind's own limit
    // or just the nearest without one: AudioMan's looping concurrency fades the others to zero volume, and they keep playing
    // silently, each still updated by the audio engine and by its ZSFX every frame. A looping sound the limit has faded to zero volume, with no fade of its own under way, is paused until the limit
    // lets it play again, a fade starts or it is played or stopped; it then rises from zero volume as natively, with its
    // reverb brought up to date, and only its place in the loop differs. Other sounds and audio outside ZSFX stay native.
    internal static class SilencedLoops
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".silenced-loops");
        private static readonly AccessTools.FieldRef<ZSFX, AudioSource> source = AccessTools.FieldRefAccess<ZSFX, AudioSource>("m_audioSource");
        private static readonly AccessTools.FieldRef<ZSFX, bool> silenced = AccessTools.FieldRefAccess<ZSFX, bool>("m_disabledFromConcurrency");
        private static readonly AccessTools.FieldRef<ZSFX, float> concurrencyVolume = AccessTools.FieldRefAccess<ZSFX, float>("m_concurrencyVolumeModifier");
        private static readonly AccessTools.FieldRef<ZSFX, float> fadeOut = AccessTools.FieldRefAccess<ZSFX, float>("m_fadeOutTimer");
        private static readonly AccessTools.FieldRef<ZSFX, float> fadeIn = AccessTools.FieldRefAccess<ZSFX, float>("m_fadeInTimer");
        private static readonly AccessTools.FieldRef<AudioMan, List<ZSFX>> looping = AccessTools.FieldRefAccess<AudioMan, List<ZSFX>>("m_loopingSfx");
        private static readonly Action<ZSFX> updateReverb = AccessTools.MethodDelegate<Action<ZSFX>>(AccessTools.Method(typeof(ZSFX), "UpdateReverb"));
        // Sounds the limit has silenced that may still be fading out, checked after each frame's sound updates.
        private static readonly HashSet<ZSFX> fading = new HashSet<ZSFX>();
        private static readonly HashSet<ZSFX> paused = new HashSet<ZSFX>();
        private static readonly List<ZSFX> checking = new List<ZSFX>();
        private static bool pausing = true;
        internal static bool Running { get; private set; }

        // Off resumes every paused sound and pauses none, as natively; the isolated client switches it for paired measurements.
        internal static bool Pausing
        {
            get => pausing;
            set
            {
                pausing = value;
                if (!value) ResumeAll();
                else if (Running) Gather();
            }
        }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(ZSFX), nameof(ZSFX.ConcurrencyDisable)), postfix: new HarmonyMethod(typeof(SilencedLoops), nameof(Disabled)));
            patches.Patch(AccessTools.Method(typeof(ZSFX), nameof(ZSFX.ConcurrencyEnable)), postfix: new HarmonyMethod(typeof(SilencedLoops), nameof(Enabled)));
            var resume = new HarmonyMethod(typeof(SilencedLoops), nameof(Changing));
            patches.Patch(AccessTools.Method(typeof(ZSFX), nameof(ZSFX.FadeOut)), prefix: resume);
            var forget = new HarmonyMethod(typeof(SilencedLoops), nameof(Forget));
            patches.Patch(AccessTools.Method(typeof(ZSFX), nameof(ZSFX.Play)), prefix: forget);
            patches.Patch(AccessTools.Method(typeof(ZSFX), nameof(ZSFX.Stop)), prefix: forget);
            patches.Patch(AccessTools.Method(typeof(ZSFX), "OnDisable"), prefix: forget);
            Gather();
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            ResumeAll();
        }

        // After the frame's sound updates: a silenced sound whose volume has reached zero pauses.
        internal static void LateUpdate()
        {
            if (!Running || fading.Count == 0) return;
            checking.Clear();
            checking.AddRange(fading);
            foreach (var sound in checking)
            {
                if (!pausing || !sound || !silenced(sound)) { fading.Remove(sound); continue; }
                var audio = source(sound);
                if (!audio || !sound.isActiveAndEnabled || !audio.isPlaying || !audio.loop || fadeOut(sound) >= 0f || fadeIn(sound) >= 0f || sound.m_fadeOutOnAwake)
                {
                    // Not playing a steady loop: the limit's next pass, every 16 frames, offers it again.
                    fading.Remove(sound);
                    continue;
                }
                if (concurrencyVolume(sound) > 0f || audio.volume > 0f) continue;
                audio.Pause();
                fading.Remove(sound);
                paused.Add(sound);
            }
            checking.Clear();
        }

        // The game's limit silences every loop beyond the nearest of its kind every 16 frames, and when a loop starts.
        private static void Disabled(ZSFX __instance)
        {
            if (pausing && !paused.Contains(__instance)) fading.Add(__instance);
        }

        private static void Enabled(ZSFX __instance)
        {
            fading.Remove(__instance);
            if (paused.Remove(__instance)) Resume(__instance);
        }

        // A fade on a paused sound continues natively from zero volume.
        private static void Changing(ZSFX __instance)
        {
            if (paused.Remove(__instance)) Resume(__instance);
        }

        // Playing restarts the sound, and stopping or disabling it ends the pause along with the playback.
        private static void Forget(ZSFX __instance)
        {
            fading.Remove(__instance);
            paused.Remove(__instance);
        }

        private static void Resume(ZSFX sound)
        {
            var audio = source(sound);
            if (!audio) return;
            audio.UnPause();
            // Natively the reverb follows the camera once a second while the loop plays silently.
            if (audio.isPlaying) updateReverb(sound);
        }

        private static void ResumeAll()
        {
            foreach (var sound in paused) if (sound) Resume(sound);
            paused.Clear();
            fading.Clear();
        }

        // Loops already silenced when the feature starts or is switched on.
        private static void Gather()
        {
            if (!pausing || !AudioMan.instance) return;
            foreach (var sound in looping(AudioMan.instance))
                if (sound && silenced(sound) && !paused.Contains(sound)) fading.Add(sound);
        }
    }
}
