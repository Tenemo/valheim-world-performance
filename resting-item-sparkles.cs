using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A dropped item plays a looping sparkle that asks for AlwaysSimulate culling, so the engine simulates it every frame
    // wherever the item lies, behind walls and behind the camera too; the sparkles of the hundreds of items lying around a
    // base cost the particle update every frame. While nothing can move an item, its sparkle looks the same whether the
    // engine simulates it unseen or pauses it until a camera sees it: such a system switches to the engine's Pause culling
    // once formed, and back to its native culling as soon as that ends, judged on an eighth of the items a frame before the
    // particle update. An owned item rests while its body sleeps and is not kinematic. An item another game owns, or nobody
    // owns, only follows its record, so it rests while its record is unchanged since the previous look and its transform is
    // at the record's position, whatever its body does. Unseen effects leave items native because they move; this holds
    // them only while they cannot.
    internal static class RestingItemSparkles
    {
        private sealed class Entry
        {
            internal ParticleSystem System;
            internal Rigidbody Body;
            internal ZNetView View;
            internal ParticleSystemCullingMode Native;
            internal float Formed;
            internal uint Revision;
            internal bool Paused;
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".resting-item-sparkles");
        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly Dictionary<int, (int systems, int[] eligible)> prefabs = new Dictionary<int, (int, int[])>();
        private static readonly List<ParticleSystem> systems = new List<ParticleSystem>();
        private static int next;
        private static bool running;
        private static bool pausing = true;
        // Off returns every sparkle to its native culling and rests none; the isolated client switches it for paired measurements.
        internal static bool Pausing
        {
            get => pausing;
            set
            {
                pausing = value;
                if (!value) foreach (var entry in entries) Restore(entry);
            }
        }
        internal static string Failure { get; private set; }
        internal static int Count => entries.Count;
        internal static int PausedCount => entries.Count(entry => entry.Paused);

        internal static void Start()
        {
            if (running) return;
            running = true;
            Failure = null;
            patches.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), postfix: new HarmonyMethod(typeof(RestingItemSparkles), nameof(Created)));
            foreach (var item in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
                if (item.GetComponent<ZNetView>() is ZNetView view && view.GetZDO() != null) Register(view);
        }

        internal static void Stop()
        {
            if (!running) return;
            running = false;
            patches.UnpatchSelf();
            foreach (var entry in entries) Restore(entry);
            entries.Clear();
            prefabs.Clear();
        }

        // Why an item's system keeps native simulation, or null when it may rest with its item: the same conditions
        // as unseen effects apart from being an item's.
        internal static string Exclusion(ParticleSystem system)
        {
            var main = system.main;
            if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate) return "native culling";
            if (!main.loop) return "one-shot";
            if (main.stopAction != ParticleSystemStopAction.None) return "stop action";
            if (system.lights.enabled) return "lights module";
            if (system.collision.enabled || system.trigger.enabled) return "collision or trigger module";
            if (system.subEmitters.enabled && system.subEmitters.subEmittersCount > 0) return "sub-emitters";
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (!renderer || !renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.None) return "no renderer";
            if (system.GetComponent<SmokeRenderer>() || system.GetComponent<VortexParticles>() || system.GetComponent<WrapParticles>() ||
                system.GetComponent<MistEmitter>() || system.GetComponent<ParticleMist>()) return "script-driven";
            return null;
        }

        private static void Created(ZNetView __instance)
        {
            if (!running) return;
            try { Register(__instance); }
            catch (Exception error) { Fail(error); }
        }

        private static void Register(ZNetView view)
        {
            var zdo = view.GetZDO();
            if (zdo == null || !view.GetComponent<ItemDrop>()) return;
            var body = view.GetComponent<Rigidbody>();
            if (!body) return;
            int hash = zdo.GetPrefab();
            view.GetComponentsInChildren(true, systems);
            if (!prefabs.TryGetValue(hash, out var prefab))
            {
                var source = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
                var prefabSystems = new List<ParticleSystem>();
                if (source) source.GetComponentsInChildren(true, prefabSystems);
                // Sub-emitters' targets play only when another system starts them.
                var targets = new HashSet<ParticleSystem>();
                foreach (var system in prefabSystems)
                    if (system.subEmitters.enabled)
                        for (int index = 0; index < system.subEmitters.subEmittersCount; index++)
                            if (system.subEmitters.GetSubEmitterSystem(index) is ParticleSystem target) targets.Add(target);
                prefabs[hash] = prefab = (prefabSystems.Count, source ? Enumerable.Range(0, prefabSystems.Count).Where(index => !targets.Contains(prefabSystems[index]) && Exclusion(prefabSystems[index]) == null).ToArray() : new int[0]);
            }
            // A hierarchy that differs from its prefab keeps native effects.
            if (prefab.eligible.Length == 0 || systems.Count != prefab.systems) return;
            foreach (int index in prefab.eligible)
            {
                var system = systems[index];
                entries.Add(new Entry { System = system, Body = body, View = view, Native = system.main.cullingMode, Formed = Time.time + FormingSeconds(system.main) });
            }
        }

        // Seconds a system takes to reach its steady look from its start, as unseen effects count them.
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

        // An eighth of the items a frame, before the engine's particle update.
        internal static void Update()
        {
            if (!running || entries.Count == 0) return;
            try
            {
                float now = Time.time;
                for (int count = entries.Count / 8 + 1; count > 0 && entries.Count > 0; count--)
                {
                    if (next >= entries.Count) next = 0;
                    var entry = entries[next];
                    if (!entry.System || !entry.Body || !entry.View)
                    {
                        entries[next] = entries[entries.Count - 1];
                        entries.RemoveAt(entries.Count - 1);
                        continue;
                    }
                    var zdo = entry.View.GetZDO();
                    if (zdo == null)
                    {
                        Restore(entry);
                        entries[next] = entries[entries.Count - 1];
                        entries.RemoveAt(entries.Count - 1);
                        continue;
                    }
                    bool rest = Pausing && now >= entry.Formed && (zdo.IsOwner()
                        ? entry.Body.IsSleeping() && !entry.Body.isKinematic
                        : zdo.DataRevision == entry.Revision && (entry.View.transform.position - zdo.GetPosition()).sqrMagnitude < 1e-4f);
                    entry.Revision = zdo.DataRevision;
                    if (rest != entry.Paused)
                    {
                        var main = entry.System.main;
                        main.cullingMode = rest ? ParticleSystemCullingMode.Pause : entry.Native;
                        entry.Paused = rest;
                    }
                    next++;
                }
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Restore(Entry entry)
        {
            if (!entry.Paused || !entry.System) return;
            var main = entry.System.main;
            main.cullingMode = entry.Native;
            entry.Paused = false;
        }

        private static void Fail(Exception error)
        {
            if (Failure != null) return;
            Failure = error.ToString();
            Plugin.Log?.LogError("Resting items' sparkles failed and stopped; native effects are restored. " + error);
            try { Stop(); }
            catch (Exception restoring) { Plugin.Log?.LogError(restoring); }
        }
    }
}
