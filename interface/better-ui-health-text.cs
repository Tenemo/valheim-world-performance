using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;

namespace WorldPerformance
{
    // BetterUI 2.5.14 writes the health number on every enemy, boss and player health bar each frame, formatting the bar's
    // text size, health and maximum health anew each time. Each number now keeps the text it was given with the values it
    // was made from, and while they and the number format stay the same it is given that text again, which it shows
    // unchanged as before.
    internal static class BetterUiHealthText
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Formatted, Reused;

        private sealed class Shown
        {
            internal string Format, Text;
            internal int Size, Health, Maximum;
            internal NumberFormatInfo Numbers;
        }

        private static readonly ConditionalWeakTable<TMP_Text, Shown> shown = new ConditionalWeakTable<TMP_Text, Shown>();

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            string version = plugin.Metadata.Version.ToString();
            if (version != "2.5.14") throw new InvalidOperationException("BetterUI " + version + " is not the version whose health text this follows (2.5.14).");
            var hud = plugin.Instance.GetType().Assembly.GetType("BetterUI.GameClasses.BetterEnemyHud");
            harmony.Patch(hud == null ? null : AccessTools.Method(hud, "UpdateHudsPostfix"), transpiler: new HarmonyMethod(typeof(BetterUiHealthText), nameof(Transpiler)));
            log.LogInfo("BetterUI's health numbers are formatted again only when they change.");
        }

        // Each of the three numbers is string.Format(format, size, health, maximum) handed to the text: the values are passed
        // unboxed to Show instead, in the same order.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var format = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object), typeof(object), typeof(object) });
            var setText = AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text));
            var health = AccessTools.Method(typeof(Character), nameof(Character.GetHealth));
            var maximum = AccessTools.Method(typeof(Character), nameof(Character.GetMaxHealth));
            var code = instructions.ToList();
            bool Boxes(int index, Type type) => code[index].opcode == OpCodes.Box && (Type)code[index].operand == type;
            int replaced = 0;
            for (int index = 12; index + 1 < code.Count; index++)
            {
                if (!code[index].Calls(format) || !code[index + 1].Calls(setText)) continue;
                if (!Boxes(index - 1, typeof(float)) || !code[index - 2].Calls(maximum) || !Boxes(index - 5, typeof(float)) || !code[index - 6].Calls(health) ||
                    !Boxes(index - 9, typeof(int)) || code[index - 12].opcode != OpCodes.Ldstr)
                    throw new InvalidOperationException("BetterUI's health text changed.");
                foreach (int box in new[] { index - 1, index - 5, index - 9 }) { code[box].opcode = OpCodes.Nop; code[box].operand = null; }
                code[index].opcode = OpCodes.Call;
                code[index].operand = AccessTools.Method(typeof(BetterUiHealthText), nameof(Show));
                code[index + 1].opcode = OpCodes.Nop;
                code[index + 1].operand = null;
                replaced++;
            }
            if (replaced != 3) throw new InvalidOperationException("BetterUI's health text changed; expected three numbers.");
            return code;
        }

        private static unsafe int Bits(float value) => *(int*)&value;

        // string.Format formats with the current number format; a format that can change keeps the native path.
        private static void Show(TMP_Text text, string format, int size, float health, float maximum)
        {
            var numbers = NumberFormatInfo.CurrentInfo;
            if (!Shortcut || (object)text == null || !numbers.IsReadOnly)
            {
                text.text = string.Format(format, size, health, maximum);
                return;
            }
            var last = shown.GetOrCreateValue(text);
            int healthBits = Bits(health), maximumBits = Bits(maximum);
            if (last.Text == null || !ReferenceEquals(last.Format, format) || last.Size != size || last.Health != healthBits || last.Maximum != maximumBits ||
                !ReferenceEquals(last.Numbers, numbers))
            {
                last.Text = string.Format(format, size, health, maximum);
                last.Format = format; last.Size = size; last.Health = healthBits; last.Maximum = maximumBits; last.Numbers = numbers;
                Formatted++;
            }
            else Reused++;
            text.text = last.Text;
        }

        // For the comparison driver: the text the shortcut gives a number with these values, and the text BetterUI formats.
        internal static string[] Compare(TMP_Text text, string format, int size, float health, float maximum)
        {
            Show(text, format, size, health, maximum);
            return new[] { text.text, string.Format(format, size, health, maximum) };
        }
    }
}
