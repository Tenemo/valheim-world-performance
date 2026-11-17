using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WorldPerformance
{
    // Some objects make their own copy of a material or mesh and never destroy it, so every copy outlives its object until
    // the hourly asset unload: the combined mesh of each mineable rock, the body material VisEquipment reads on each
    // humanoid, the materials of each piece or creature with a colour variation, the mesh of each weapon's swing trail, and
    // with Star Level System the coloured copy of each creature's body material. Each copy now goes with the networked object
    // that made it, and a trail's with its weapon. A placement ghost's copies stay with the asset unload: the player keeps
    // them after their ghost is gone, and PlantEasily's extra ghosts draw them too. A copy
    // several objects hold goes with the last of them, and nothing loaded from the game's files is ever destroyed: those
    // have positive instance ids, copies made while playing negative ones.
    internal static class OwnedCopies
    {
        private static readonly Dictionary<int, List<Object>> byOwner = new Dictionary<int, List<Object>>();
        private static readonly Dictionary<int, int> holders = new Dictionary<int, int>();
        // Views that remove themselves as they wake, as on a placement ghost, a menu character or an icon render: their object
        // stays, so nothing is released with them.
        private static readonly HashSet<int> leaving = new HashSet<int>();
        private static readonly AccessTools.FieldRef<MineRock5, MeshFilter> rockFilter = AccessTools.FieldRefAccess<MineRock5, MeshFilter>("m_meshFilter");
        private static readonly AccessTools.FieldRef<MaterialVariation, Renderer> variationRenderer = AccessTools.FieldRefAccess<MaterialVariation, Renderer>("m_renderer");
        private static readonly AccessTools.FieldRef<MeleeWeaponTrail, Mesh> trailMesh = AccessTools.FieldRefAccess<MeleeWeaponTrail, Mesh>("m_trailMesh");
        // The colour variation running now, whose renderer's material getter copies are its own.
        private static MaterialVariation variation;
        // Off leaves new copies to the asset unload, as natively; the isolated client switches it for paired measurements.
        internal static bool Releasing { get; set; } = true;
        internal static long Owned { get; private set; }
        internal static long Released { get; private set; }
        internal static int Owners => byOwner.Count;
        // Set once every patch is in place; the Star Level System integration needs it.
        internal static bool Ready { get; private set; }

        internal static void Initialize(PerformancePatches harmony)
        {
            HarmonyMethod Handler(string name) => name == null ? null : new HarmonyMethod(typeof(OwnedCopies), name);
            void Patch(Type type, string method, string prefix, string postfix) =>
                harmony.Patch(AccessTools.Method(type, method), prefix: Handler(prefix), postfix: Handler(postfix));
            Patch(typeof(ZNetView), "Awake", nameof(BeforeView), null);
            Patch(typeof(ZNetView), "OnDestroy", null, nameof(Release));
            Patch(typeof(MineRock5), "Awake", null, nameof(OwnRockMesh));
            Patch(typeof(VisEquipment), "Awake", nameof(BeforeBody), nameof(OwnBodyMaterial));
            // A colour variation reads its renderer's materials, which copies each one the renderer has not copied yet, while
            // it checks and while it sets its material; it keeps only some of those copies, and every one is its object's own.
            foreach (var method in new[] { "CheckMaterial", "UpdateMaterial" })
                harmony.Patch(AccessTools.Method(typeof(MaterialVariation), method), prefix: Handler(nameof(EnterVariation)), finalizer: Handler(nameof(LeaveVariation)));
            harmony.Patch(AccessTools.Method(typeof(Renderer), "GetMaterialArray"), prefix: Handler(nameof(BeforeMaterials)), postfix: Handler(nameof(OwnVariationCopies)));
            // A swing trail makes its mesh as it starts and destroys only the trail object drawing it.
            Patch(typeof(MeleeWeaponTrail), "Start", null, nameof(OwnTrailMesh));
            harmony.Defer(() => Ready = true, () => Ready = false);
        }

        // Records a runtime copy as one the networked object around the component holds.
        internal static void Own(Component on, Object copy)
        {
            if (!Releasing || !copy || copy.GetInstanceID() >= 0 || !on) return;
            var view = on.GetComponentInParent<ZNetView>(true);
            if (!view || ViewLeaves() || leaving.Contains(view.GetInstanceID())) return;
            int owner = view.GetInstanceID();
            if (!byOwner.TryGetValue(owner, out var copies)) byOwner[owner] = copies = new List<Object>(2);
            foreach (var held in copies) if (ReferenceEquals(held, copy)) return;
            copies.Add(copy);
            int id = copy.GetInstanceID();
            holders[id] = holders.TryGetValue(id, out int count) ? count + 1 : 1;
            Owned++;
        }

        // ZNetView.Awake removes the view when objects are made without the network: ghosts, previews and the menu.
        private static bool ViewLeaves() => ZNetView.m_forceDisableInit || ZDOMan.instance == null;

        private static void BeforeView(ZNetView __instance)
        {
            if (ViewLeaves()) leaving.Add(__instance.GetInstanceID());
        }

        private static void Release(ZNetView __instance)
        {
            if (leaving.Count > 0) leaving.Remove(__instance.GetInstanceID());
            if (byOwner.Count == 0 || !byOwner.TryGetValue(__instance.GetInstanceID(), out var copies)) return;
            byOwner.Remove(__instance.GetInstanceID());
            foreach (var copy in copies)
            {
                int id = copy.GetInstanceID();
                if (holders.TryGetValue(id, out int count) && count > 1) { holders[id] = count - 1; continue; }
                holders.Remove(id);
                if (copy) { Object.Destroy(copy); Released++; }
            }
        }

        private static void OwnRockMesh(MineRock5 __instance)
        {
            var filter = rockFilter(__instance);
            if (filter) Own(__instance, filter.sharedMesh);
        }

        private static void BeforeBody(VisEquipment __instance, out Material __state) =>
            __state = __instance.m_bodyModel ? __instance.m_bodyModel.sharedMaterial : null;

        private static void OwnBodyMaterial(VisEquipment __instance, Material __state)
        {
            var body = __instance.m_bodyModel;
            if (!body) return;
            var material = body.sharedMaterial;
            if (!ReferenceEquals(material, __state)) Own(__instance, material);
        }

        private static void EnterVariation(MaterialVariation __instance, out MaterialVariation __state)
        {
            __state = variation;
            variation = __instance;
        }

        private static Exception LeaveVariation(MaterialVariation __state, Exception __exception)
        {
            variation = __state;
            return __exception;
        }

        private static void BeforeMaterials(Renderer __instance, out Material[] __state) =>
            __state = variation is object && ReferenceEquals(__instance, variationRenderer(variation)) ? __instance.sharedMaterials : null;

        // Owns what the getter just copied: the materials it returns that the renderer did not hold before.
        private static void OwnVariationCopies(Renderer __instance, Material[] __result, Material[] __state)
        {
            if (__state == null || __result == null) return;
            foreach (var material in __result)
                if (material && Array.IndexOf(__state, material) < 0) Own(__instance, material);
        }

        // The trail keeps drawing into its mesh while its weapon exists, so the mesh goes with the weapon's object.
        private static void OwnTrailMesh(MeleeWeaponTrail __instance)
        {
            var mesh = trailMesh(__instance);
            if (!Releasing || !mesh || mesh.GetInstanceID() >= 0) return;
            __instance.gameObject.AddComponent<HeldCopies>().copies = new List<Object> { mesh };
            Owned++;
        }

        internal static IReadOnlyList<Object> HeldCopiesOf(GameObject holder) =>
            holder && holder.TryGetComponent<HeldCopies>(out var held) ? held.copies : (IReadOnlyList<Object>)Array.Empty<Object>();

        // Copies an object without a networked view holds, destroyed with it.
        private sealed class HeldCopies : MonoBehaviour
        {
            internal List<Object> copies;

            private void OnDestroy()
            {
                // A clone of the holder starts without the list: the copies stay the original's.
                if (copies == null) return;
                foreach (var copy in copies)
                    if (copy) { Object.Destroy(copy); Released++; }
            }
        }
    }

    // Star Level System 1.33.0 colours each creature by giving its body a copy of the body material, made once a creature
    // and never destroyed. The copy goes with the creature; a copy two creatures share goes with the last of them.
    internal static class ColoredCreatureCopies
    {
        private const string Suffix = " (SLSColor)";

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.StarLevelSystem, out var plugin)) return;
            if (!OwnedCopies.Ready) throw new InvalidOperationException("Owned copies are unavailable.");
            var colorization = plugin.Instance.GetType().Assembly.GetType("StarLevelSystem.modules.Colorization");
            var apply = colorization == null ? null : AccessTools.Method(colorization, "ApplyColorization");
            if (apply == null || apply.GetParameters().Length != 2 || apply.GetParameters()[0].ParameterType != typeof(GameObject))
                throw new InvalidOperationException("Star Level System's colorization changed.");
            harmony.Patch(apply, postfix: new HarmonyMethod(typeof(ColoredCreatureCopies), nameof(OwnColoredBodies)));
            log.LogInfo("Star Level System's coloured creature materials go with their creatures.");
        }

        private static void OwnColoredBodies(GameObject __0)
        {
            if (!__0) return;
            foreach (var renderer in __0.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var material = renderer.sharedMaterial;
                if (material && material.name.EndsWith(Suffix, StringComparison.Ordinal)) OwnedCopies.Own(renderer, material);
            }
        }
    }
}
