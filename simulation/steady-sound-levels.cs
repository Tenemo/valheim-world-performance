using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Every playing game sound sets its audio source's volume and pitch each frame, though a steady loop such as a fire, a
    // mill or the wind keeps both unchanged for minutes. Each is now written only when the source holds another value; an
    // audio source keeps the value it was last given, and giving it the same value again changes nothing.
    internal static class SteadySoundLevels
    {
        internal static long Written, Kept;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            harmony.Patch(AccessTools.Method(typeof(ZSFX), nameof(ZSFX.CustomUpdate)), transpiler: new HarmonyMethod(typeof(SteadySoundLevels), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var volume = AccessTools.PropertySetter(typeof(AudioSource), nameof(AudioSource.volume));
            var pitch = AccessTools.PropertySetter(typeof(AudioSource), nameof(AudioSource.pitch));
            var code = Transpilers.CallInstead(instructions, instruction => instruction.Calls(volume), AccessTools.Method(typeof(SteadySoundLevels), nameof(Volume)), 2, "ZSFX's volume changed.");
            return Transpilers.CallInstead(code, instruction => instruction.Calls(pitch), AccessTools.Method(typeof(SteadySoundLevels), nameof(Pitch)), 1, "ZSFX's pitch changed.");
        }

        internal static void Volume(AudioSource source, float value)
        {
            if (source.volume == value) { Kept++; return; }
            Written++;
            source.volume = value;
        }

        internal static void Pitch(AudioSource source, float value)
        {
            if (source.pitch == value) { Kept++; return; }
            Written++;
            source.pitch = value;
        }
    }
}
