using HarmonyLib;

namespace WorldPerformance
{
    // The game strips rich text tags with a regular expression from every shown tame's name on its health bar each frame,
    // and from hovered signs and portals. Every tag the expression removes starts with '<', so a text without one comes back
    // as it was given; it is now returned at once, and a text with one goes through the expression as before.
    internal static class PlainTextTags
    {
        internal static long Plain, Tagged;

        internal static void Initialize(PerformancePatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(StringExtensionMethods), nameof(StringExtensionMethods.RemoveRichTextTags)),
                prefix: new HarmonyMethod(typeof(PlainTextTags), nameof(Prefix)));

        private static bool Prefix(string text, ref string __result)
        {
            if (text == null || text.IndexOf('<') >= 0) { Tagged++; return true; }
            Plain++;
            __result = text;
            return false;
        }
    }
}
