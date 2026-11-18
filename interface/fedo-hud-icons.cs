using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // FedoHud 1.1.2 gives every beehive and every grown crop a floating "!" that it shows when the hive is full or the crop
    // is ready, and checks it every frame even with the icon turned off, as the pack ships it. Creating each icon also
    // logged a missing-font warning. The icons are now made only while their setting is on; turning one on in game gives the
    // loaded hives and crops their icons at once, as FedoHud's own wake would have. Progression Fix replaces FedoHud's
    // entries with personal ones, so the entries are read again as each world loads, before any hive or crop wakes.
    internal static class FedoHudIcons
    {
        private static Func<bool> beehiveIcon;
        private static Func<bool> cropIcon;
        private static MethodInfo beehive, crop;
        private static readonly object[] argument = new object[1];
        private static FieldInfo beehiveField, cropField;
        private static object owner;
        private static ConfigEntry<bool> beehiveEntry, cropEntry;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.FedoHud, out var plugin)) return;
            var type = plugin.Instance.GetType();
            var beehiveSetting = AccessTools.PropertyGetter(type, "ShowBeehiveFullIcon");
            var cropSetting = AccessTools.PropertyGetter(type, "ShowGrowthReadyIcon");
            owner = plugin.Instance;
            beehiveField = AccessTools.Field(type, "_showBeehiveFullIcon");
            cropField = AccessTools.Field(type, "_showGrowthReadyIcon");
            beehive = AccessTools.Method(type.Assembly.GetType("FedoHud.BeehiveFullIndicator+BeehiveAwakePatch"), "Postfix");
            crop = AccessTools.Method(type.Assembly.GetType("FedoHud.PlantGrownIndicator+PickableAwakePatch"), "Postfix");
            if (beehiveSetting == null || cropSetting == null || beehiveField?.FieldType != typeof(ConfigEntry<bool>) || cropField?.FieldType != typeof(ConfigEntry<bool>) || beehive == null || crop == null)
                throw new InvalidOperationException("FedoHud's floating icons changed.");
            beehiveIcon = AccessTools.MethodDelegate<Func<bool>>(beehiveSetting, plugin.Instance);
            cropIcon = AccessTools.MethodDelegate<Func<bool>>(cropSetting, plugin.Instance);
            harmony.Patch(beehive, prefix: new HarmonyMethod(typeof(FedoHudIcons), nameof(BeehiveIconOn)));
            harmony.Patch(crop, prefix: new HarmonyMethod(typeof(FedoHudIcons), nameof(CropIconOn)));
            harmony.Patch(AccessTools.Method(typeof(ZNetScene), "Awake"), postfix: new HarmonyMethod(typeof(FedoHudIcons), nameof(Follow)));
            harmony.Defer(Follow, Unfollow);
            log.LogInfo("FedoHud's beehive and crop icons are made only while turned on.");
        }

        // Listens to the entries FedoHud reads now.
        private static void Follow()
        {
            var hive = (ConfigEntry<bool>)beehiveField.GetValue(owner);
            var grown = (ConfigEntry<bool>)cropField.GetValue(owner);
            if (ReferenceEquals(hive, beehiveEntry) && ReferenceEquals(grown, cropEntry)) return;
            Unfollow();
            beehiveEntry = hive;
            cropEntry = grown;
            if (beehiveEntry != null) beehiveEntry.SettingChanged += BeehiveTurned;
            if (cropEntry != null) cropEntry.SettingChanged += CropTurned;
        }

        private static void Unfollow()
        {
            if (beehiveEntry != null) beehiveEntry.SettingChanged -= BeehiveTurned;
            if (cropEntry != null) cropEntry.SettingChanged -= CropTurned;
            beehiveEntry = cropEntry = null;
        }

        private static bool BeehiveIconOn() => beehiveIcon();

        private static bool CropIconOn() => cropIcon();

        // FedoHud's wake adds the icon to an object that has none, so running it now for each loaded one gives exactly the
        // icons the game would have had with the setting on from the start; turned off, the icons hide themselves.
        private static void BeehiveTurned(object sender, EventArgs arguments) { if (beehiveIcon()) Wake<Beehive>(beehive); }

        private static void CropTurned(object sender, EventArgs arguments) { if (cropIcon()) Wake<Pickable>(crop); }

        private static void Wake<T>(MethodInfo postfix) where T : Component
        {
            foreach (var target in UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                argument[0] = target;
                postfix.Invoke(null, argument);
            }
            argument[0] = null;
        }
    }
}
