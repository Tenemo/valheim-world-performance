using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WorldPerformance
{
    // Living labels need their original tokens for language and input-layout changes. A strong dictionary also owns
    // every destroyed label, including inactive rows that never receive OnDestroy. Use weak membership for both types.
    [HarmonyPatch]
    internal static class LocalizedTextLifetime
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Localization), nameof(Localization.Localize), new[] { typeof(Transform) });
            yield return AccessTools.Method(typeof(Localization), nameof(Localization.RemoveTextFromCache), new[] { typeof(Text) });
            yield return AccessTools.Method(typeof(Localization), nameof(Localization.RemoveTextFromCache), new[] { typeof(TMP_Text) });
            yield return AccessTools.Method(typeof(Localization), nameof(Localization.ReLocalizeAll));
            yield return AccessTools.Method(typeof(Localization), nameof(Localization.ReLocalizeVisible));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int changed = 0;
            foreach (var instruction in instructions)
            {
                foreach (var label in new[] { typeof(Text), typeof(TMP_Text) })
                {
                    var dictionary = typeof(Dictionary<,>).MakeGenericType(label, typeof(string));
                    foreach (string name in new[] { "set_Item", "TryGetValue", "ContainsKey", "Remove" })
                    {
                        var parameters = name == "set_Item" ? new[] { label, typeof(string) }
                            : name == "TryGetValue" ? new[] { label, typeof(string).MakeByRefType() } : new[] { label };
                        if (instruction.Calls(AccessTools.Method(dictionary, name, parameters)))
                        {
                            instruction.opcode = OpCodes.Call;
                            instruction.operand = AccessTools.Method(typeof(WeakTextCache<>).MakeGenericType(label), name == "set_Item" ? "Remember" : name);
                            changed++;
                        }
                    }
                }
                yield return instruction;
            }
            if (changed != 2) throw new InvalidOperationException("The game's localized label cache changed.");
        }

        private static class WeakTextCache<T> where T : Component
        {
            private static readonly ConditionalWeakTable<Dictionary<T, string>, ConditionalWeakTable<T, StrongBox<string>>> caches =
                new ConditionalWeakTable<Dictionary<T, string>, ConditionalWeakTable<T, StrongBox<string>>>();

            private static ConditionalWeakTable<T, StrongBox<string>> Cache(Dictionary<T, string> previous)
            {
                var weak = caches.GetOrCreateValue(previous);
                if (previous.Count > 0)
                {
                    foreach (var entry in previous) weak.GetOrCreateValue(entry.Key).Value = entry.Value;
                    previous.Clear();
                }
                return weak;
            }

            private static void Remember(Dictionary<T, string> previous, T label, string original)
            {
                Cache(previous).GetOrCreateValue(label).Value = original;
            }
            private static bool TryGetValue(Dictionary<T, string> previous, T label, out string original)
            {
                bool found = Cache(previous).TryGetValue(label, out var value);
                original = found ? value.Value : null;
                return found;
            }
            private static bool ContainsKey(Dictionary<T, string> previous, T label) => TryGetValue(previous, label, out _);
            private static bool Remove(Dictionary<T, string> previous, T label)
            {
                return Cache(previous).Remove(label);
            }
        }
    }
}
