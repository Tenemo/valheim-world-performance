using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Every frame each water volume writes the same water time into its own copy of the water material, a copy it makes only
    // to hold its depth at its four corners and whether it follows the global wind: about 1.2 µs a volume, 0.22 ms for the 178
    // volumes loaded around the base. The volumes instead share one copy of each material they start with, written once a
    // frame where the first volume would write it, and keep their depth and wind in their renderer's property block beside what
    // the game keeps there, such as the visible distance the game's Water component sets on a surface, so every surface draws
    // with the values it has natively. A volume whose setup has not run yet shares at once with the depth it has.
    internal static class SharedWaterTime
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".shared-water-time");
        private static readonly MethodInfo setup = AccessTools.Method(typeof(WaterVolume), "SetupMaterial");
        private static readonly int waterTime = Shader.PropertyToID("_WaterTime");
        private static readonly int depth = Shader.PropertyToID("_depth");
        private static readonly int globalWind = Shader.PropertyToID("_UseGlobalWind");
        private static readonly AccessTools.FieldRef<float> time = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_waterTime"));
        private static readonly AccessTools.FieldRef<WaterVolume, float[]> depths = AccessTools.FieldRefAccess<WaterVolume, float[]>("m_normalizedDepth");
        // Each starting material's shared copy and the other way round, and each known volume's surface and starting material.
        private static readonly Dictionary<Material, Material> copies = new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, Material> sources = new Dictionary<Material, Material>();
        private static readonly Dictionary<WaterVolume, (MeshRenderer renderer, Material source)> surfaces = new Dictionary<WaterVolume, (MeshRenderer, Material)>(Identity<WaterVolume>.Instance);
        // Volumes enabled since they last shared, which share at their next update unless their setup has; every other volume
        // updating is known without a lookup.
        private static readonly HashSet<WaterVolume> enabled = new HashSet<WaterVolume>(Identity<WaterVolume>.Instance);
        // Reused for every surface: the renderer's block is read into it, so the values the game set there stay.
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static readonly float[] forced = new float[4];
        // Set as the game's volume updates start each frame, the only place it updates their materials, so the frame's first
        // update writes the copies' time, as natively the first writes its own.
        private static bool due;
        private static bool sharing = true;
        internal static bool Running { get; private set; }
        internal static int Surfaces => surfaces.Count;
        internal static int Copies => copies.Count;

        // Off gives every volume its own material written every frame, as natively; the isolated client switches it for paired
        // measurements.
        internal static bool Sharing
        {
            get => sharing;
            set
            {
                if (value == sharing) return;
                sharing = value;
                if (Running) Switch(value);
            }
        }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            due = true;
            patches.Patch(setup, prefix: new HarmonyMethod(typeof(SharedWaterTime), nameof(SetUp)));
            patches.Patch(AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.UpdateMaterials)), prefix: new HarmonyMethod(typeof(SharedWaterTime), nameof(Write)));
            patches.Patch(AccessTools.Method(typeof(WaterVolume), "OnEnable"), postfix: new HarmonyMethod(typeof(SharedWaterTime), nameof(Enabled)));
            patches.Patch(AccessTools.Method(typeof(WaterVolume), "OnDisable"), postfix: new HarmonyMethod(typeof(SharedWaterTime), nameof(Forget)));
            patches.Patch(AccessTools.Method(typeof(MonoUpdaters), "Update"), prefix: new HarmonyMethod(typeof(SharedWaterTime), nameof(Updating)));
            // Volumes enabled before a restart share at their next update too.
            foreach (var volume in WaterVolume.Instances) if (volume && !surfaces.ContainsKey(volume)) enabled.Add(volume);
            if (sharing) Switch(true);
        }

        // Each volume gets its own material again, set up and written by the game's own code. The volumes' starting materials
        // and the copies stay known for a restart.
        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchOwner();
            if (sharing) Switch(false);
        }

        private static void Switch(bool share)
        {
            foreach (var volume in new List<WaterVolume>(surfaces.Keys))
                if (!volume) surfaces.Remove(volume);
                else if (share) Share(volume);
                else Restore(volume);
        }

        // With sharing off the game sets the volume up, after its starting material is known.
        private static bool SetUp(WaterVolume __instance)
        {
            if (sharing)
            {
                Share(__instance);
                return false;
            }
            var renderer = __instance.m_waterSurface;
            if (renderer && renderer.sharedMaterial && !surfaces.ContainsKey(__instance)) surfaces[__instance] = (renderer, SourceOf(renderer.sharedMaterial));
            return true;
        }

        private static bool Write(WaterVolume __instance)
        {
            if (!sharing) return true;
            // Only a volume enabled since it last shared can be unknown; one whose surface cannot share yet tries again.
            if (enabled.Count > 0 && enabled.Contains(__instance))
            {
                if (!surfaces.ContainsKey(__instance)) Share(__instance);
                if (surfaces.ContainsKey(__instance)) enabled.Remove(__instance);
            }
            if (!due) return false;
            due = false;
            float now = time();
            foreach (var copy in copies.Values) copy.SetFloat(waterTime, now);
            return false;
        }

        private static void Updating() => due = true;

        private static void Enabled(WaterVolume __instance) => enabled.Add(__instance);

        // A disabled or destroyed volume is set up again when it next updates after being enabled.
        private static void Forget(WaterVolume __instance)
        {
            surfaces.Remove(__instance);
            enabled.Remove(__instance);
        }

        // The world's volumes and the materials they started with are gone at its end, and so do their shared copies.
        internal static void ForgetWorld()
        {
            foreach (var pair in new List<KeyValuePair<Material, Material>>(copies))
                if (!pair.Key || !pair.Value)
                {
                    if (pair.Value) Object.Destroy(pair.Value);
                    copies.Remove(pair.Key);
                    sources.Remove(pair.Value);
                }
            foreach (var volume in new List<WaterVolume>(surfaces.Keys)) if (!volume) surfaces.Remove(volume);
            enabled.RemoveWhere(volume => !volume);
        }

        private static Material SourceOf(Material material) => sources.TryGetValue(material, out var source) ? source : material;

        // The volume draws with its starting material's shared copy and keeps the depth and wind the game's setup gives it.
        private static void Share(WaterVolume volume)
        {
            var renderer = volume.m_waterSurface;
            var current = renderer ? renderer.sharedMaterial : null;
            if (!current) return;
            var source = surfaces.TryGetValue(volume, out var surface) ? surface.source : SourceOf(current);
            // The volume's own material, which the game made while sharing was off, goes with it.
            if (current != source && !sources.ContainsKey(current)) Object.Destroy(current);
            if (!copies.TryGetValue(source, out var copy))
            {
                copy = new Material(source);
                copy.SetFloat(waterTime, time());
                copies[source] = copy;
                sources[copy] = source;
            }
            float force = volume.m_forceDepth;
            float[] values = depths(volume);
            if (force >= 0f)
            {
                for (int corner = 0; corner < forced.Length; corner++) forced[corner] = force;
                values = forced;
            }
            // The block keeps its own copy of the values.
            renderer.GetPropertyBlock(block);
            block.SetFloatArray(depth, values);
            block.SetFloat(globalWind, volume.m_useGlobalWind ? 1f : 0f);
            renderer.sharedMaterial = copy;
            renderer.SetPropertyBlock(block);
            surfaces[volume] = (renderer, source);
        }

        // The game's own setup makes the volume its material again, with sharing off or its patch gone, and writes its time. A
        // property block cannot drop single values, so the surface's block starts empty and its Water component sets its visible
        // distance again, as the game's Water.ApplySettings leaves the block natively.
        private static void Restore(WaterVolume volume)
        {
            var (renderer, source) = surfaces[volume];
            if (!renderer) return;
            renderer.SetPropertyBlock(null);
            if (renderer.TryGetComponent<Water>(out var water) && water.isActiveAndEnabled && ZNet.instance && ZoneSystem.instance) water.ApplySettings();
            renderer.sharedMaterial = source;
            setup.Invoke(volume, null);
            volume.UpdateMaterials();
        }
    }
}
