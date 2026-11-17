using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WorldPerformance
{
    // Each floating damage number fades out by having its text colour set every frame, which makes the text component
    // rebuild its whole mesh: about 7 microseconds a number a frame, and 105 ms a second with a few hundred numbers on
    // screen in the hidden client. The fade now sets the same transparency on the number's canvas renderer, which leaves
    // the mesh as it is. The text itself turns opaque once, as the game's first fade step makes it; a number drawn in
    // more than one piece, such as one with a fallback font's glyphs, fades as before.
    [HarmonyPatch(typeof(DamageText), "UpdateWorldTexts")]
    internal static class DamageNumberFading
    {
        // The isolated test client sets false to compare the native fade.
        internal static bool Fading = true;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var colour = code.Where(instruction => instruction.operand is MethodInfo method && method.Name == "set_color" && typeof(Graphic).IsAssignableFrom(method.DeclaringType)).ToList();
            if (colour.Count != 1) throw new System.InvalidOperationException("The game's damage number fade changed.");
            colour[0].opcode = OpCodes.Call;
            colour[0].operand = AccessTools.Method(typeof(DamageNumberFading), nameof(Fade));
            return code;
        }

        // The colour the game sets differs from the text's own only in its transparency.
        private static void Fade(Graphic text, Color colour)
        {
            var renderer = text.canvasRenderer;
            if (Fading && text.transform.childCount == 0)
            {
                if (text.color.a != 1f) { var opaque = colour; opaque.a = 1f; text.color = opaque; }
                renderer.SetAlpha(colour.a);
                return;
            }
            if (renderer.GetAlpha() != 1f) renderer.SetAlpha(1f);
            text.color = colour;
        }
    }
}
