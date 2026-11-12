using System;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // FedoHud 1.1.2 gives every beehive and every grown crop a floating "!" that it shows when the hive is full or the crop
    // is ready, and checks it every frame even with the icon turned off, as the pack ships it. Creating each icon also
    // logged a missing-font warning. The icons are now made only while their setting is on; turning one on in game shows
    // it on hives and crops that load afterwards.
    internal static class FedoHudIcons
    {
        private static Func<bool> beehiveIcon;
        private static Func<bool> cropIcon;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var type = plugin.Instance.GetType();
            var beehiveSetting = AccessTools.PropertyGetter(type, "ShowBeehiveFullIcon");
            var cropSetting = AccessTools.PropertyGetter(type, "ShowGrowthReadyIcon");
            var beehive = AccessTools.Method(type.Assembly.GetType("FedoHud.BeehiveFullIndicator+BeehiveAwakePatch"), "Postfix");
            var crop = AccessTools.Method(type.Assembly.GetType("FedoHud.PlantGrownIndicator+PickableAwakePatch"), "Postfix");
            if (beehiveSetting == null || cropSetting == null || beehive == null || crop == null)
                throw new InvalidOperationException("FedoHud's floating icons changed.");
            beehiveIcon = AccessTools.MethodDelegate<Func<bool>>(beehiveSetting, plugin.Instance);
            cropIcon = AccessTools.MethodDelegate<Func<bool>>(cropSetting, plugin.Instance);
            harmony.Patch(beehive, prefix: new HarmonyMethod(typeof(FedoHudIcons), nameof(BeehiveIconOn)));
            harmony.Patch(crop, prefix: new HarmonyMethod(typeof(FedoHudIcons), nameof(CropIconOn)));
            log.LogInfo("FedoHud's beehive and crop icons are made only while turned on.");
        }

        private static bool BeehiveIconOn() => beehiveIcon();

        private static bool CropIconOn() => cropIcon();
    }
}
