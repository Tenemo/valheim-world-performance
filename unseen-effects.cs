using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Torch flames, flares and other looping world effects ask for AlwaysSimulate culling, so the engine simulates each of them
    // every frame wherever it is, behind walls and behind the camera too. Eligible systems switch to the engine's Pause culling:
    // the engine skips their simulation while no camera sees them and resumes from the same state when one does, so a steady
    // looping effect looks the same whenever it comes into view. PauseAndCatchup would instead simulate the missed time inside
    // the first frame back, an 18 to 65 ms stall. An effect that starts, as when a fire is lit or an object loads, keeps native
    // simulation until it is fully formed, so it grows as it would natively whether or not anyone sees it, and only a formed
    // effect pauses. Systems are found as their objects appear, never by scanning frames, and their native settings return
    // when the feature stops or fails.
    internal static class UnseenEffects
    {
        private sealed class Prefab { internal int Systems; internal int[] Eligible; }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".unseen-effects");
        private static readonly Dictionary<int, Prefab> prefabs = new Dictionary<int, Prefab>();
        private static readonly HashSet<UnseenEffect> managed = new HashSet<UnseenEffect>();
        private static readonly List<ParticleSystem> systems = new List<ParticleSystem>();
        private static readonly AccessTools.FieldRef<ItemStand, GameObject> standVisual = AccessTools.FieldRefAccess<ItemStand, GameObject>("m_visualItem");
        private static readonly AccessTools.FieldRef<LocationProxy, GameObject> locationInstance = AccessTools.FieldRefAccess<LocationProxy, GameObject>("m_instance");
        private static bool running;
        private static long registrationTicks, startTicks, starts, prefabsEvaluated;
        internal static bool Paused { get; private set; }
        internal static string Failure { get; private set; }

        internal static void Start()
        {
            if (running) return;
            running = true;
            Paused = true;
            Failure = null;
            patches.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), postfix: new HarmonyMethod(typeof(UnseenEffects), nameof(Created)));
            patches.Patch(AccessTools.Method(typeof(ItemStand), "SetVisualItem"), prefix: new HarmonyMethod(typeof(UnseenEffects), nameof(StandVisualBefore)),
                postfix: new HarmonyMethod(typeof(UnseenEffects), nameof(StandVisual)));
            patches.Patch(AccessTools.Method(typeof(LocationProxy), "SpawnLocation"), postfix: new HarmonyMethod(typeof(UnseenEffects), nameof(LocationSpawned)));
            patches.Patch(AccessTools.Method(typeof(DungeonGenerator), "Spawn"), postfix: new HarmonyMethod(typeof(UnseenEffects), nameof(DungeonSpawned)));
            patches.Patch(AccessTools.Method(typeof(DungeonGenerator), "Generate", new[] { typeof(int), typeof(ZoneSystem.SpawnMode) }),
                postfix: new HarmonyMethod(typeof(UnseenEffects), nameof(DungeonGenerated)));
            patches.Patch(AccessTools.Method(typeof(SoftReferencePrefabSpawner), "SpawnPrefab"), postfix: new HarmonyMethod(typeof(UnseenEffects), nameof(EffectLoaded)));
            // Objects that already exist when the feature starts; at game start there are none.
            foreach (var view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (view.GetZDO() != null) Register(view.gameObject);
        }

        // Restores every managed system's native settings and removes the markers; the feature stays off afterwards.
        internal static void Stop()
        {
            if (!running) return;
            running = false;
            Paused = false;
            patches.UnpatchSelf();
            foreach (var effect in managed.ToArray())
            {
                try { Restore(effect); }
                catch (Exception error) { Plugin.Log?.LogError(error); }
                if (effect) UnityEngine.Object.Destroy(effect);
            }
            managed.Clear();
            prefabs.Clear();
        }

        // Switches managed systems between Pause culling and their native settings without forgetting them, for paired measurements.
        internal static void SetPaused(bool paused)
        {
            if (!running || paused == Paused) return;
            Paused = paused;
            foreach (var effect in managed)
                if (!effect || !effect.System) continue;
                else if (!paused) Restore(effect);
                else
                {
                    var main = effect.System.main;
                    main.cullingMode = ParticleSystemCullingMode.Pause;
                }
        }

        internal static ParticleSystem[] Managed() => managed.Where(effect => effect && effect.System).Select(effect => effect.System).ToArray();

        internal static string Report()
        {
            double Milliseconds(long ticks) => Math.Round(ticks * 1000d / Stopwatch.Frequency, 3);
            var live = managed.Where(effect => effect && effect.System).ToList();
            var active = live.Where(effect => effect.System.gameObject.activeInHierarchy).ToList();
            return "Unseen world effects: " + (running ? Paused ? "paused while unseen" : "native (switched for measurement)" : "stopped") +
                "; managed systems " + live.Count + " (" + active.Count + " active, " +
                active.Count(effect => effect.System.main.cullingMode == ParticleSystemCullingMode.Pause) + " in Pause culling); forming up to " +
                Math.Round(live.Select(effect => effect.FormingSeconds).DefaultIfEmpty(0f).Max(), 2) + " s; prefabs evaluated " + prefabsEvaluated +
                "; starts " + starts + "; registration " + Milliseconds(registrationTicks) + " ms, start decisions " + Milliseconds(startTicks) + " ms" +
                (Failure != null ? "; failed: " + Failure : "");
        }

        // Why a system keeps native simulation, or null when it may pause while unseen. Particle lights light what the camera
        // sees; collision, trigger and sub-emitter modules reach scripts or other systems; characters, projectiles, items and
        // vehicles carry their effects around; scripts that write particles every frame or own environment effects need them
        // simulated; a stop action would wait for particles that never age while unseen.
        internal static string Exclusion(ParticleSystem system, HashSet<ParticleSystem> subEmitters)
        {
            var main = system.main;
            if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate) return "native culling";
            if (!main.loop) return "one-shot";
            if (main.stopAction != ParticleSystemStopAction.None) return "stop action";
            if (!system.GetComponentInParent<ZNetView>(true)) return "not a world object";
            if (system.GetComponentInParent<Character>(true)) return "character";
            if (system.GetComponentInParent<Projectile>(true)) return "projectile";
            if (system.GetComponentInParent<ItemDrop>(true)) return "item";
            if (system.GetComponentInParent<Ship>(true) || system.GetComponentInParent<Vagon>(true)) return "vehicle";
            if (system.lights.enabled) return "lights module";
            if (system.collision.enabled || system.trigger.enabled) return "collision or trigger module";
            if (system.subEmitters.enabled && system.subEmitters.subEmittersCount > 0 || subEmitters.Contains(system)) return "sub-emitters";
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (!renderer || !renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.None) return "no renderer";
            if (system.GetComponent<SmokeRenderer>() || system.GetComponent<VortexParticles>() || system.GetComponent<WrapParticles>() ||
                system.GetComponent<OceanBubbleParticles>() || system.GetComponent<MistEmitter>() || system.GetComponent<DistantFogEmitter>() ||
                system.GetComponent<ParticleMist>()) return "script-driven";
            return null;
        }

        private static HashSet<ParticleSystem> SubEmitters(List<ParticleSystem> candidates)
        {
            var result = new HashSet<ParticleSystem>();
            foreach (var system in candidates)
            {
                var modules = system.subEmitters;
                if (!modules.enabled) continue;
                for (int index = 0; index < modules.subEmittersCount; index++)
                    if (modules.GetSubEmitterSystem(index) is ParticleSystem child) result.Add(child);
            }
            return result;
        }

        // Network objects as they are created: streamed in, placed, spawned or dropped. The decision is cached per prefab, and
        // the many prefabs without eligible effects cost one dictionary lookup.
        private static void Created(ZNetView __instance)
        {
            if (!running) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                var zdo = __instance.GetZDO();
                if (zdo == null) return;
                int hash = zdo.GetPrefab();
                if (!prefabs.TryGetValue(hash, out var prefab))
                {
                    var source = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
                    if (!source) { Register(__instance.gameObject); return; }
                    source.GetComponentsInChildren(true, systems);
                    var subEmitters = SubEmitters(systems);
                    prefabs[hash] = prefab = new Prefab { Systems = systems.Count,
                        Eligible = Enumerable.Range(0, systems.Count).Where(index => Exclusion(systems[index], subEmitters) == null).ToArray() };
                    prefabsEvaluated++;
                }
                if (prefab.Eligible.Length == 0) return;
                __instance.GetComponentsInChildren(true, systems);
                // A hierarchy that already differs from its prefab is judged on its own.
                if (systems.Count != prefab.Systems) { Register(__instance.gameObject); return; }
                var eligible = prefab.Eligible.Select(index => systems[index]).ToArray();
                foreach (var system in eligible) Manage(system);
            }
            catch (Exception error) { Fail(error); }
            finally { registrationTicks += Stopwatch.GetTimestamp() - start; }
        }

        // Effects spawned into world objects later: item stand visuals, location contents, dungeon rooms and effects some
        // objects load beside a placeholder after they appear. Stands refresh their visual every 4 seconds, so only a new
        // visual is examined. A building ghost's effects stay native.
        private static void StandVisualBefore(ItemStand __instance, out GameObject __state) => __state = standVisual(__instance);
        private static void StandVisual(ItemStand __instance, GameObject __state) { if (running && standVisual(__instance) != __state) Registered(standVisual(__instance)); }
        private static void LocationSpawned(LocationProxy __instance, bool __result) { if (running && __result) Registered(locationInstance(__instance)); }
        internal static void DungeonSpawned(DungeonGenerator __instance) { if (running) Registered(__instance.gameObject); }
        private static void DungeonGenerated(DungeonGenerator __instance, ZoneSystem.SpawnMode mode) { if (running && mode != ZoneSystem.SpawnMode.Ghost) Registered(__instance.gameObject); }
        private static void EffectLoaded(SoftReferencePrefabSpawner __instance)
        {
            if (running && !ZNetView.m_forceDisableInit && __instance.transform.parent && !Player.IsPlacementGhost(__instance.transform.root.gameObject))
                Registered(__instance.transform.parent.gameObject);
        }

        private static void Registered(GameObject root)
        {
            if (!root) return;
            long start = Stopwatch.GetTimestamp();
            try { Register(root); }
            catch (Exception error) { Fail(error); }
            finally { registrationTicks += Stopwatch.GetTimestamp() - start; }
        }

        private static void Register(GameObject root)
        {
            root.GetComponentsInChildren(true, systems);
            var subEmitters = SubEmitters(systems);
            var eligible = systems.Where(system => Exclusion(system, subEmitters) == null).ToArray();
            foreach (var system in eligible) Manage(system);
        }

        private static void Manage(ParticleSystem system)
        {
            if (system.GetComponent<UnseenEffect>()) return;
            var main = system.main;
            // Adding the marker runs its activation before it knows its system, so that one returns at once.
            var effect = system.gameObject.AddComponent<UnseenEffect>();
            effect.NativeCulling = main.cullingMode;
            effect.FormingSeconds = FormingSeconds(main);
            effect.System = system;
            managed.Add(effect);
            if (Paused && system.gameObject.activeInHierarchy) Started(effect);
        }

        // Seconds an effect takes from its start to its steady look: one loop, or longer when its particles outlive the loop.
        // Particles that never die, such as a torch's glow, are steady after one loop, as prewarm assumes. A prewarmed effect
        // starts formed.
        private static float FormingSeconds(ParticleSystem.MainModule main)
        {
            if (main.prewarm) return 0f;
            float Longest(ParticleSystem.MinMaxCurve curve) =>
                curve.mode == ParticleSystemCurveMode.Constant ? curve.constant :
                curve.mode == ParticleSystemCurveMode.TwoConstants ? Math.Max(curve.constantMin, curve.constantMax) :
                curve.curveMultiplier * new[] { curve.curveMin, curve.curveMax }.Where(item => item != null).SelectMany(item => item.keys)
                    .Select(key => key.value).DefaultIfEmpty(0f).Max();
            float life = Longest(main.startDelay) + Longest(main.startLifetime);
            float seconds = float.IsInfinity(life) || float.IsNaN(life) ? main.duration : Math.Max(main.duration, life);
            return seconds / Math.Max(main.simulationSpeed, .01f);
        }

        // Activation is where an object that is created, lit or relit starts its effect. It keeps native simulation until it
        // is formed, then pauses while unseen.
        internal static void Started(UnseenEffect effect)
        {
            if (!effect.System) return;
            // A copy of a managed object made after the feature stopped keeps its native settings.
            if (!running) { Restore(effect); UnityEngine.Object.Destroy(effect); return; }
            managed.Add(effect);
            if (!Paused) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                var main = effect.System.main;
                main.cullingMode = effect.NativeCulling;
                effect.Form();
                starts++;
            }
            catch (Exception error) { Fail(error); }
            finally { startTicks += Stopwatch.GetTimestamp() - start; }
        }

        internal static void Formed(UnseenEffect effect)
        {
            if (!running || !Paused || !effect.System) return;
            try
            {
                var main = effect.System.main;
                main.cullingMode = ParticleSystemCullingMode.Pause;
            }
            catch (Exception error) { Fail(error); }
        }

        internal static void Forget(UnseenEffect effect) => managed.Remove(effect);

        private static void Restore(UnseenEffect effect)
        {
            if (!effect || !effect.System) return;
            var main = effect.System.main;
            main.cullingMode = effect.NativeCulling;
        }

        private static void Fail(Exception error)
        {
            if (Failure != null) return;
            Failure = error.ToString();
            Plugin.Log?.LogError("Pausing unseen world effects failed and stopped; native effects are restored. " + error);
            try { Stop(); }
            catch (Exception restoring) { Plugin.Log?.LogError(restoring); }
        }
    }

    // Marks a world effect the feature manages and keeps its native settings. Fields are serialized so a copy of a managed
    // object knows them too. Activation is where a created, lit or relit object starts the effect.
    internal sealed class UnseenEffect : MonoBehaviour
    {
        public ParticleSystem System;
        public ParticleSystemCullingMode NativeCulling;
        public float FormingSeconds;
        private bool forming;

        private void OnEnable() => UnseenEffects.Started(this);
        private void OnDestroy() => UnseenEffects.Forget(this);

        // A restart replaces any earlier wait, so an effect relit while forming pauses only once formed again. Cancelling
        // searches every pending call in the game, so only a wait that exists is cancelled.
        internal void Form()
        {
            if (forming) CancelInvoke(nameof(Formed));
            forming = true;
            Invoke(nameof(Formed), FormingSeconds);
        }

        private void Formed()
        {
            forming = false;
            UnseenEffects.Formed(this);
        }
    }
}
