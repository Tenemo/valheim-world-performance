using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // The health, stamina, adrenaline, eitr and mount bars write their numbers each frame from a table of the numbers 0 to
    // 256, and make a new text for every larger number, so a late-game character's health and stamina made two new texts a
    // frame. The last texts made for numbers above 256 are now kept and given again for the same number. A positive number's
    // text has only digits, whatever the language.
    internal static class HudNumberTexts
    {
        private static readonly int[] numbers = new int[8];
        private static readonly string[] texts = new string[8];

        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var transpiler = new HarmonyMethod(typeof(HudNumberTexts), nameof(Transpiler));
            foreach (string bar in new[] { "UpdateMount", "UpdateHealth", "UpdateStamina", "UpdateAdrenaline", "UpdateEitr" })
                harmony.Patch(AccessTools.Method(typeof(Hud), bar), transpiler: transpiler);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var fast = AccessTools.Method(typeof(StringExtensionMethods), nameof(StringExtensionMethods.ToFastString));
            var code = new List<CodeInstruction>(instructions);
            int count = code.FindAll(instruction => instruction.Calls(fast)).Count;
            if (count == 0) throw new System.InvalidOperationException("A hud bar's number changed.");
            return Transpilers.CallInstead(code, instruction => instruction.Calls(fast), AccessTools.Method(typeof(HudNumberTexts), nameof(Text)), count, "A hud bar's number changed.");
        }

        internal static string Text(int number)
        {
            if (number <= 256) return number.ToFastString();
            int slot = number & 7;
            if (numbers[slot] == number && texts[slot] != null) return texts[slot];
            numbers[slot] = number;
            return texts[slot] = number.ToFastString();
        }
    }
}
