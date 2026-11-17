using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace WorldPerformance
{
    // Hud.UpdateStatusEffects finds each shown status effect's icon, cooldown overlay, name and timer by searching the entry's
    // children every frame: three searches by name, two component lookups and a search for the first active text. Every entry
    // is a copy of one template whose parts nobody renames, moves or replaces, and the game destroys and rebuilds all entries
    // whenever the number of effects changes, so each entry's icon, overlay and timer are now looked up once, when first used.
    // The name is the first of the entry's texts on an active object, as the search finds it; the timer's object switches on
    // and off, so that is still checked each frame.
    internal static class StatusEffectParts
    {
        private sealed class Parts
        {
            internal Transform Entry;
            internal Image Icon;
            internal GameObject Cooldown;
            internal TMP_Text[] Texts;
            internal GameObject[] Objects;
            internal TMP_Text Timer;
            internal bool IconKnown, CooldownKnown, TimerKnown;
        }

        private static readonly List<Parts> kept = new List<Parts>();
        private static Parts last;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            harmony.Patch(AccessTools.Method(typeof(Hud), "UpdateStatusEffects"), transpiler: new HarmonyMethod(typeof(StatusEffectParts), nameof(Transpiler)));
        }

        // entry.Find("Icon").GetComponent<Image>(), entry.Find("Cooldown").gameObject, entry.GetComponentInChildren<TMP_Text>() and
        // entry.Find("TimeText").GetComponent<TMP_Text>() become calls of the methods below with the entry.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var find = AccessTools.Method(typeof(Transform), nameof(Transform.Find), new[] { typeof(string) });
            var image = AccessTools.Method(typeof(Component), nameof(Component.GetComponent), Type.EmptyTypes).MakeGenericMethod(typeof(Image));
            var text = AccessTools.Method(typeof(Component), nameof(Component.GetComponent), Type.EmptyTypes).MakeGenericMethod(typeof(TMP_Text));
            var inChildren = AccessTools.Method(typeof(Component), nameof(Component.GetComponentInChildren), Type.EmptyTypes).MakeGenericMethod(typeof(TMP_Text));
            var gameObject = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
            var code = instructions.ToList();
            int replaced = 0;
            void Replace(int index, int count, string method)
            {
                for (int part = 1; part < count; part++)
                {
                    if (code[index + part].labels.Count > 0 || code[index + part].blocks.Count > 0) throw new InvalidOperationException("The status effect list changed.");
                    code[index + part].opcode = OpCodes.Nop;
                    code[index + part].operand = null;
                }
                code[index].opcode = OpCodes.Call;
                code[index].operand = AccessTools.Method(typeof(StatusEffectParts), method);
                replaced++;
            }
            for (int index = 0; index + 2 < code.Count; index++)
            {
                string name = code[index].opcode == OpCodes.Ldstr ? (string)code[index].operand : null;
                if (code[index].Calls(inChildren)) Replace(index, 1, nameof(Name));
                else if (name == null || !code[index + 1].Calls(find)) continue;
                else if (name == "Icon" && code[index + 2].Calls(image)) Replace(index, 3, nameof(Icon));
                else if (name == "Cooldown" && code[index + 2].Calls(gameObject)) Replace(index, 3, nameof(Cooldown));
                else if (name == "TimeText" && code[index + 2].Calls(text)) Replace(index, 3, nameof(Timer));
            }
            if (replaced != 4) throw new InvalidOperationException("The status effect list changed.");
            return code;
        }

        private static Parts Of(Transform entry)
        {
            if (last != null && ReferenceEquals(last.Entry, entry)) return last;
            for (int index = 0; index < kept.Count; index++)
                if (ReferenceEquals(kept[index].Entry, entry)) return last = kept[index];
            // Entries of rebuilt lists are destroyed; forget them before the list grows.
            if (kept.Count >= 32) kept.RemoveAll(parts => !parts.Entry);
            last = new Parts { Entry = entry };
            kept.Add(last);
            return last;
        }

        private static Image Icon(Transform entry)
        {
            var parts = Of(entry);
            if (!parts.IconKnown) { parts.Icon = entry.Find("Icon").GetComponent<Image>(); parts.IconKnown = true; }
            return parts.Icon;
        }

        private static GameObject Cooldown(Transform entry)
        {
            var parts = Of(entry);
            if (!parts.CooldownKnown) { parts.Cooldown = entry.Find("Cooldown").gameObject; parts.CooldownKnown = true; }
            return parts.Cooldown;
        }

        private static TMP_Text Timer(Transform entry)
        {
            var parts = Of(entry);
            if (!parts.TimerKnown) { parts.Timer = entry.Find("TimeText").GetComponent<TMP_Text>(); parts.TimerKnown = true; }
            return parts.Timer;
        }

        // GetComponentInChildren<TMP_Text>() searches the entry and its children depth first and answers the first text on an
        // object active in the hierarchy; GetComponentsInChildren lists them in the same order.
        internal static TMP_Text Name(Component entry)
        {
            var parts = Of((Transform)entry);
            if (parts.Texts == null)
            {
                parts.Texts = entry.GetComponentsInChildren<TMP_Text>(true);
                parts.Objects = parts.Texts.Select(candidate => candidate.gameObject).ToArray();
            }
            for (int index = 0; index < parts.Texts.Length; index++)
                if (parts.Texts[index] && parts.Objects[index].activeInHierarchy) return parts.Texts[index];
            return entry.GetComponentInChildren<TMP_Text>();
        }
    }
}
