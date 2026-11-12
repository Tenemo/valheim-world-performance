using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Quick Stack and AzuCraftyBoxes register each chest as it loads but forget it only when it breaks, or for
    // AzuCraftyBoxes when a piece around it unloads after its queued registration went through. A chest that unloads
    // with its area therefore stays in their lists, with its inventory, for the rest of the session. Each chest now
    // tells the integrations when Unity destroys it, whether it unloads or breaks.
    internal sealed class ContainerLifetime : MonoBehaviour
    {
        private static readonly List<Action<Container>> Cleanups = new List<Action<Container>>();
        private Container container;

        // Each integration registers its cleanup under its own patches, so one failing leaves the others working.
        internal static void Track(PerformancePatches harmony, Action<Container> cleanup)
        {
            harmony.Patch(AccessTools.Method(typeof(Container), "Awake"), postfix: new HarmonyMethod(typeof(ContainerLifetime), nameof(Watch)));
            harmony.Defer(() => Cleanups.Add(cleanup), () => Cleanups.Remove(cleanup));
        }

        private static void Watch(Container __instance)
        {
            var lifetime = __instance.GetComponent<ContainerLifetime>();
            if (!lifetime) lifetime = __instance.gameObject.AddComponent<ContainerLifetime>();
            lifetime.container = __instance;
        }

        private void OnDestroy()
        {
            if ((object)container == null) return;
            foreach (var cleanup in Cleanups)
                try { cleanup(container); }
                catch (Exception error) { Debug.LogWarning("Chest cleanup failed: " + error.GetBaseException().Message); }
        }
    }

    // Quick Stack's chest list for nearby storing and restocking.
    internal static class QuickStackUnload
    {
        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.QuickStack, out var plugin)) return;
            var finder = plugin.Instance.GetType().Assembly.GetType("QuickStackStore.ContainerFinder");
            var list = finder == null ? null : AccessTools.Field(finder, "AllContainers");
            if (list?.FieldType != typeof(List<Container>)) throw new InvalidOperationException("Quick Stack's chest list changed.");
            // Unity treats destroyed chests as equal to each other, so the exact entry is found by reference.
            ContainerLifetime.Track(harmony, chest =>
            {
                var chests = (List<Container>)list.GetValue(null);
                int index = chests.FindIndex(entry => ReferenceEquals(entry, chest));
                if (index >= 0) chests.RemoveAt(index);
            });
        }
    }
}
