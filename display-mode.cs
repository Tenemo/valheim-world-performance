using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game's present settings read Screen.currentResolution every frame, and again with vsync on. Each read asks Windows
    // for the display's current mode, a display-configuration query on the main thread that slows down while other programs
    // query displays too. Only the refresh rate comes from that answer, since the game reads the window size separately, and
    // the mode changes only when the display does. The last answer is therefore reused, and the mode asked again twice a
    // second and in any frame in which the window's size, full-screen mode or focus changes. Resolution, full-screen and
    // focus changes apply in the same frame as natively; a change of the refresh rate alone applies within half a second.
    internal static class DisplayMode
    {
        private const float Interval = .5f;
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".display-mode");
        private static Resolution answer;
        private static float asked = float.NegativeInfinity;
        private static int width, height;
        private static FullScreenMode screenMode;
        private static bool focused;
        internal static bool Running { get; private set; }
        // Off asks Windows on every read, as natively; the isolated client switches it for paired measurements.
        internal static bool Cached { get; set; } = true;
        internal static long Queries { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            asked = float.NegativeInfinity;
            var transpiler = new HarmonyMethod(typeof(DisplayMode), nameof(Redirect));
            patches.Patch(AccessTools.Method(typeof(PresentManager), "InvokeEventIfResolutionChanged"), transpiler: transpiler);
            patches.Patch(AccessTools.Method(typeof(PresentManager), "UpdatePresentSettingsWithVsyncCount"), transpiler: transpiler);
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        // Only the two per-frame readers change; other readers, such as the graphics settings menu, ask Windows as before.
        private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var mode = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.currentResolution));
            var present = AccessTools.Method(typeof(PresentManager), nameof(PresentManager.GetCurrentPresentResolution));
            var result = new List<CodeInstruction>(instructions);
            int redirected = 0;
            foreach (var instruction in result)
            {
                if (instruction.Calls(mode)) instruction.operand = AccessTools.Method(typeof(DisplayMode), nameof(Current));
                else if (instruction.Calls(present)) instruction.operand = AccessTools.Method(typeof(DisplayMode), nameof(Present));
                else continue;
                redirected++;
            }
            if (redirected == 0) Plugin.Fallback($"PresentManager.{original.Name} no longer reads the display mode where expected, so it stays native.");
            return result;
        }

        internal static Resolution Current()
        {
            int currentWidth = Screen.width, currentHeight = Screen.height;
            FullScreenMode currentMode = Screen.fullScreenMode;
            bool currentFocus = Application.isFocused;
            float now = Time.unscaledTime;
            if (!Cached || now - asked >= Interval || currentWidth != width || currentHeight != height || currentMode != screenMode || currentFocus != focused)
            {
                answer = Screen.currentResolution;
                asked = now;
                width = currentWidth;
                height = currentHeight;
                screenMode = currentMode;
                focused = currentFocus;
                Queries++;
            }
            return answer;
        }

        // PresentManager.GetCurrentPresentResolution with the cached mode: the window's own size replaces the display's.
        private static Resolution Present()
        {
            Resolution present = Current();
            present.width = Screen.width;
            present.height = Screen.height;
            return present;
        }
    }
}
