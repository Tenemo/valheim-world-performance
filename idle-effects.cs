using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A cooking station or oven keeps a particle system playing for each slot, and a grill a looping sizzle, empty or not: an
    // empty or unlit slot only switches its systems' emission off and mutes its sound. The weather's effects likewise keep playing with
    // their emission off outside their weather. Each such system costs the engine's particle update every frame, drawing nothing.
    // A looping system whose emission the game switched off pauses once its last particle, and the last of the particles it
    // started in other systems such as a raindrop's splash, has died; a muted slot's sound pauses. A system that ends by itself
    // or that another system's particles start stays native.
    // Each plays on from where it stood as soon as the game switches its emission on or unmutes the sound, in that frame and
    // before the engine's particle update. The game does both only in the two methods patched here; should other code switch a
    // paused system or sound on, a look at an eighth of them a frame plays it again within eight frames.
    // While a system is paused here its renderer is off too, since it has nothing to draw, so the camera's culling and particle
    // preparation pass over it; it comes back on as the system plays again. A renderer that was already off stays off.
    internal static class IdleEffects
    {
        // A set with a list beside it, so each frame can look at a slice of it.
        private sealed class Kept<T> where T : UnityEngine.Object
        {
            private readonly HashSet<T> members = new HashSet<T>();
            private readonly List<T> order = new List<T>();
            private int next;
            internal int Count => members.Count;
            internal bool Contains(T item) => members.Contains(item);
            internal void Add(T item) { if (members.Add(item)) order.Add(item); }
            internal bool Remove(T item) { if (!members.Remove(item)) return false; order.Remove(item); return true; }
            internal IEnumerable<T> All => order;
            internal void Clear() { members.Clear(); order.Clear(); next = 0; }

            // An eighth of the members, in turn; those drop returns true for leave.
            internal void Slice(Func<T, bool> drop)
            {
                for (int count = order.Count / 8 + 1; count > 0 && order.Count > 0; count--)
                {
                    if (next >= order.Count) next = 0;
                    var item = order[next];
                    if (!drop(item)) { next++; continue; }
                    members.Remove(item);
                    order[next] = order[order.Count - 1];
                    order.RemoveAt(order.Count - 1);
                }
            }
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".idle-effects");
        private static readonly AccessTools.FieldRef<CookingStation, ParticleSystem[]> slotSystems = AccessTools.FieldRefAccess<CookingStation, ParticleSystem[]>("m_ps");
        private static readonly AccessTools.FieldRef<CookingStation, AudioSource[]> slotSounds = AccessTools.FieldRefAccess<CookingStation, AudioSource[]>("m_as");
        // Systems switched off whose particles are still dying, systems paused here and muted sounds paused here.
        private static readonly HashSet<ParticleSystem> fading = new HashSet<ParticleSystem>();
        private static readonly Kept<ParticleSystem> paused = new Kept<ParticleSystem>();
        private static readonly Kept<AudioSource> silent = new Kept<AudioSource>();
        private static readonly List<ParticleSystem> checking = new List<ParticleSystem>();
        private static readonly List<ParticleSystem> found = new List<ParticleSystem>();
        // Systems the weather's other systems start, which never play on their own.
        private static readonly HashSet<ParticleSystem> started = new HashSet<ParticleSystem>();
        // Renderers switched off here, by the paused system that holds them.
        private static readonly Dictionary<ParticleSystem, ParticleSystemRenderer> hidden = new Dictionary<ParticleSystem, ParticleSystemRenderer>();
        private static readonly Func<ParticleSystem, bool> switchedOn = system =>
        {
            if (!system) { hidden.Remove(system); return true; }
            // Other code that played it again gets its renderer back at once.
            if (!system.isPaused) Show(system);
            if (!system.emission.enabled) return false;
            Show(system);
            system.Play(false);
            return true;
        };
        private static readonly Func<AudioSource, bool> unmuted = sound =>
        {
            if (!sound) return true;
            if (sound.mute) return false;
            sound.UnPause();
            return true;
        };
        private static bool pausing = true, hiding = true;
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        internal static int PausedSystems => paused.Count;
        internal static int PausedSounds => silent.Count;
        internal static int HiddenRenderers => hidden.Count;

        // Off plays every paused system and sound and pauses none, as natively; the isolated client switches it for paired measurements.
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

        // Paused systems' renderers off; the isolated client switches it off for paired measurements.
        internal static bool HidingRenderers
        {
            get => hiding;
            set
            {
                if (value == hiding) return;
                hiding = value;
                if (!value) ShowAll();
                else foreach (var system in paused.All) Hide(system);
            }
        }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(CookingStation), "SetSlotVisual"), postfix: new HarmonyMethod(typeof(IdleEffects), nameof(SlotShown)));
            patches.Patch(AccessTools.Method(typeof(EnvMan), "SetParticleArrayEnabled"), postfix: new HarmonyMethod(typeof(IdleEffects), nameof(WeatherSwitched)));
            Gather();
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            ResumeAll();
        }

        // Before the frame's particle update: a dying system whose last particles have gone pauses, and a slice of the paused
        // systems and sounds is looked at in case other code switched them back on.
        internal static void Update()
        {
            if (!Running || !pausing) return;
            try
            {
                if (fading.Count > 0)
                {
                    checking.AddRange(fading);
                    foreach (var system in checking)
                    {
                        if (!system || system.emission.enabled || !system.gameObject.activeInHierarchy) fading.Remove(system);
                        else if (Quiet(system)) Pause(system);
                    }
                    checking.Clear();
                }
                paused.Slice(switchedOn);
                silent.Slice(unmuted);
            }
            catch (Exception error) { Fail(error); }
        }

        // Once a second for each slot of each station, and whenever a slot changes.
        private static void SlotShown(CookingStation __instance, int i)
        {
            if (!pausing) return;
            try
            {
                var systems = slotSystems(__instance);
                if (systems != null && i < systems.Length) Follow(systems[i]);
                if (i < __instance.m_burntPS.Length) Follow(__instance.m_burntPS[i]);
                if (i < __instance.m_donePS.Length) Follow(__instance.m_donePS[i]);
                var sounds = slotSounds(__instance);
                var sound = sounds != null && i < sounds.Length ? sounds[i] : null;
                if (!sound) return;
                if (!sound.mute)
                {
                    if (silent.Remove(sound)) sound.UnPause();
                }
                else if (!silent.Contains(sound) && sound.isPlaying && sound.loop)
                {
                    sound.Pause();
                    silent.Add(sound);
                }
            }
            catch (Exception error) { Fail(error); }
        }

        // When the weather changes, and once for every weather as the world starts.
        private static void WeatherSwitched(GameObject[] psystems)
        {
            if (!pausing || psystems == null) return;
            try
            {
                foreach (var item in psystems)
                {
                    if (!item) continue;
                    item.GetComponentsInChildren(found);
                    foreach (var system in found)
                    {
                        var children = system.subEmitters;
                        for (int index = 0; index < children.subEmittersCount; index++) if (children.GetSubEmitterSystem(index) is ParticleSystem child && child) started.Add(child);
                    }
                    foreach (var system in found) if (!started.Contains(system)) Follow(system);
                    found.Clear();
                }
            }
            catch (Exception error) { Fail(error); }
        }

        // The weather effects already switched off when pausing is switched on; cooking slots follow at their next update.
        private static void Gather()
        {
            if (!EnvMan.instance) return;
            foreach (var setup in EnvMan.instance.m_environments) if (setup != null) WeatherSwitched(setup.m_psystems);
        }

        private static void Follow(ParticleSystem system)
        {
            if (!system) return;
            if (system.emission.enabled)
            {
                fading.Remove(system);
                if (paused.Remove(system)) { Show(system); system.Play(false); }
                return;
            }
            // A system other code played again while it was paused here is followed anew.
            if (paused.Contains(system) && system.isPaused) return;
            if (paused.Remove(system)) Show(system);
            if (!system.isPlaying || !system.main.loop || !system.gameObject.activeInHierarchy) return;
            if (Quiet(system)) Pause(system);
            else fading.Add(system);
        }

        // Pausing a system pauses the systems its particles start too, so it waits until their particles are gone as well.
        private static bool Quiet(ParticleSystem system)
        {
            if (system.particleCount > 0) return false;
            var children = system.subEmitters;
            for (int index = 0; index < children.subEmittersCount; index++)
            {
                var child = children.GetSubEmitterSystem(index);
                if (child && child.particleCount > 0) return false;
            }
            return true;
        }

        private static void Pause(ParticleSystem system)
        {
            fading.Remove(system);
            system.Pause(false);
            paused.Add(system);
            Hide(system);
        }

        private static void Hide(ParticleSystem system)
        {
            if (!hiding || !system || hidden.ContainsKey(system)) return;
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (!renderer || !renderer.enabled) return;
            renderer.enabled = false;
            hidden[system] = renderer;
        }

        private static void Show(ParticleSystem system)
        {
            if (!hidden.TryGetValue(system, out var renderer)) return;
            hidden.Remove(system);
            if (renderer) renderer.enabled = true;
        }

        private static void ShowAll()
        {
            foreach (var renderer in hidden.Values) if (renderer) renderer.enabled = true;
            hidden.Clear();
        }

        private static void ResumeAll()
        {
            ShowAll();
            foreach (var system in paused.All) if (system) system.Play(false);
            foreach (var sound in silent.All) if (sound) sound.UnPause();
            paused.Clear();
            fading.Clear();
            silent.Clear();
        }

        private static void Fail(Exception error)
        {
            if (Failure != null) return;
            Failure = error.ToString();
            Plugin.Log?.LogError("Pausing idle effects failed and stopped; every effect plays natively. " + error);
            try { Stop(); }
            catch (Exception restoring) { Plugin.Log?.LogError(restoring); }
        }
    }
}
