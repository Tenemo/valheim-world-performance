using System;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // AzuClock 1.1.1 draws the arrows of {wind} and {windforecast} with a game font that lacks them. The first time Unity
    // needs a character a font lacks, it opens every font installed in Windows on the main thread to find one that has
    // it: up to 2.5 s with 674 fonts, in the frame the clock first appears after a join. The clock's font now asks for the
    // arrows while the loading screen covers the view, so that search happens behind it, once per game start.
    internal static class ClockFontFallback
    {
        private static string arrows;
        private static ConfigEntryBase clockString, fontName, osFont;
        private static bool done;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuClock, out var plugin)) return;
            var clock = plugin.Instance.GetType();
            ConfigEntryBase Entry(string name) => AccessTools.Field(clock, name)?.GetValue(null) as ConfigEntryBase;
            var forecast = clock.Assembly.GetType("AzuClock.Forecast");
            var table = forecast == null ? null : AccessTools.Field(forecast, "Arrows")?.GetValue(null) as string[];
            arrows = table == null ? null : string.Concat(table);
            clockString = Entry("ClockString"); fontName = Entry("ClockFontName"); osFont = Entry("ClockUseOSFont");
            if (string.IsNullOrEmpty(arrows) || clockString == null || fontName == null || osFont == null)
                throw new InvalidOperationException("AzuClock's wind arrows or font settings changed.");
            harmony.Patch(AccessTools.Method(typeof(Hud), "UpdateBlackScreen"), postfix: new HarmonyMethod(typeof(ClockFontFallback), nameof(Covered)));
        }

        private static void Covered(CanvasGroup ___m_loadingScreen)
        {
            if (done || !___m_loadingScreen.gameObject.activeSelf || ___m_loadingScreen.alpha < 1f) return;
            // An OS font is AzuClock's own choice of a font with more characters; a clock without wind arrows needs no search.
            string text = (string)clockString.BoxedValue;
            if (osFont.BoxedValue.ToString() == "On" || (text.IndexOf("{wind}", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("{windforecast}", StringComparison.OrdinalIgnoreCase) < 0)) { done = true; return; }
            // The same lookup AzuClock makes; the font may load later in this loading screen.
            string name = (string)fontName.BoxedValue;
            var font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(candidate => candidate.name == name);
            if (!font) return;
            done = true;
            var timer = Stopwatch.StartNew();
            font.RequestCharactersInTexture(arrows);
            Plugin.Log.LogInfo("AzuClock's wind arrows found their font behind the loading screen in " + timer.ElapsedMilliseconds + " ms.");
        }
    }
}
