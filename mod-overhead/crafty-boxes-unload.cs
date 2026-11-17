using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // AzuCraftyBoxes 1.8.28 forgets a chest only through its own removal, which skips a chest whose queued registration
    // has not gone through yet and runs only while pulling is on, so chests that unload with their area stay in its
    // registry with their inventories for the rest of the session. To find them it also searched every building piece
    // for chests in and around it as the piece unloaded or broke, making two lists for every piece. Each chest now leaves
    // the registry, the registration queue and the pending removals when Unity destroys it, in any pulling mode, so the
    // piece search is no longer needed.
    internal static class CraftyBoxesUnload
    {
        private static HashSet<Container> containers, pending, removing;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
            HashSet<Container> Set(string name) => boxes == null ? null : AccessTools.Field(boxes, name)?.GetValue(null) as HashSet<Container>;
            containers = Set("Containers"); pending = Set("ContainersToAdd"); removing = Set("ContainersToRemove");
            var patch = assembly.GetType("AzuCraftyBoxes.Patches.WearNTearOnDestroyPatch");
            var search = patch == null ? null : AccessTools.Method(patch, "Prefix", new[] { typeof(WearNTear) });
            if (containers == null || pending == null || removing == null || search == null) throw new InvalidOperationException("AzuCraftyBoxes' chest registry changed.");
            harmony.Patch(search, prefix: new HarmonyMethod(typeof(CraftyBoxesUnload), nameof(SkipSearch)));
            ContainerLifetime.Track(harmony, Forget);
            log.LogInfo("AzuCraftyBoxes forgets each chest as it unloads or breaks.");
        }

        private static bool SkipSearch() => false;

        private static void Forget(Container chest)
        {
            containers.Remove(chest); pending.Remove(chest); removing.Remove(chest);
        }
    }
}
