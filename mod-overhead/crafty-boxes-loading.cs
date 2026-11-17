using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // AzuCraftyBoxes 1.8.28 offers a chest to its registry after every Container.Load, which runs whenever the chest's
    // contents change: it looks for a player above the chest and checks the chest's and every ward's access before a
    // registration that does nothing for a chest it already holds. Its own checks still run first, then a chest it
    // already holds is passed over. Without a game profile the access check would fail, so that case runs the mod's code.
    internal static class CraftyBoxesLoading
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Held, Offered;
        private static readonly AccessTools.FieldRef<Player, bool> loading = AccessTools.FieldRefAccess<Player, bool>("m_isLoading");
        private static readonly AccessTools.FieldRef<Player, bool> teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
        private static Func<Container, bool> skip, access;
        private static Action<Container> add;
        private static HashSet<Container> containers;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var functions = assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions");
            var boxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
            var patch = assembly.GetType("AzuCraftyBoxes.Patches.ContainerLoadPatch");
            var shouldSkip = functions == null ? null : AccessTools.Method(functions, "ShouldSkipContainer", new[] { typeof(Container) });
            var hasAccess = functions == null ? null : AccessTools.Method(functions, "HasAccessToContainer", new[] { typeof(Container) });
            var register = boxes == null ? null : AccessTools.Method(boxes, "AddContainer", new[] { typeof(Container) });
            containers = boxes == null ? null : AccessTools.Field(boxes, "Containers")?.GetValue(null) as HashSet<Container>;
            var postfix = patch == null ? null : AccessTools.Method(patch, "Postfix", new[] { typeof(Container) });
            if (shouldSkip == null || hasAccess == null || register == null || containers == null || postfix == null)
                throw new InvalidOperationException("AzuCraftyBoxes' chest loading changed.");
            skip = AccessTools.MethodDelegate<Func<Container, bool>>(shouldSkip);
            access = AccessTools.MethodDelegate<Func<Container, bool>>(hasAccess);
            add = AccessTools.MethodDelegate<Action<Container>>(register);
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(CraftyBoxesLoading), nameof(Loaded)));
            log.LogInfo("AzuCraftyBoxes passes over chests it already holds when they load.");
        }

        private static bool Loaded(Container __0)
        {
            var chest = __0;
            if (!Shortcut) return true;
            if (skip(chest)) return false;
            var local = Player.m_localPlayer;
            if (local == null) return false;
            // A chest the registry holds is never added again, and the checks before it change nothing.
            if (containers.Contains(chest) && Game.instance != null && Game.instance.GetPlayerProfile() != null) { Held++; return false; }
            Offered++;
            var above = chest.GetComponentInParent<Player>();
            if ((above == null || above == local) && !loading(local) && !teleporting(local) && access(chest)) add(chest);
            return false;
        }
    }
}
