using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WorldPerformance
{
    // Every floating damage number is a new copy of the game's text object, destroyed when it fades: about 45 microseconds
    // to show one, and dozens a second in a big fight. Faded numbers now wait hidden for the next one, which takes the
    // template's place and order back as a new copy would. A number another mod changed beyond what the game sets for each
    // number, such as FedoHud's bold, scaled hits, is destroyed as before.
    internal static class DamageNumberReuse
    {
        private const int MostKept = 100;
        // The isolated test client sets false to compare native copies.
        internal static bool Pooling = true;
        private static readonly Stack<GameObject> kept = new Stack<GameObject>();
        private static GameObject template;
        private static int templateComponents;
        private static FontStyles templateStyle;
        private static Vector3 templateScale;

        [HarmonyPatch(typeof(DamageText), "AddInworldText")]
        private static class Shown
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var copies = code.Where(instruction => instruction.operand is MethodInfo method && method.Name == nameof(Object.Instantiate) && method.IsGenericMethod &&
                    method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Transform) })).ToList();
                if (copies.Count != 1) throw new InvalidOperationException("The game's damage number copy changed.");
                copies[0].operand = AccessTools.Method(typeof(DamageNumberReuse), nameof(Take));
                return code;
            }
        }

        [HarmonyPatch(typeof(DamageText), "UpdateWorldTexts")]
        private static class Faded
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var destroy = AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object) });
                var removals = code.Where(instruction => instruction.Calls(destroy)).ToList();
                if (removals.Count != 1) throw new InvalidOperationException("The game's damage number removal changed.");
                removals[0].operand = AccessTools.Method(typeof(DamageNumberReuse), nameof(Release));
                return code;
            }
        }

        private static GameObject Take(GameObject original, Transform parent)
        {
            if (!Pooling) return Object.Instantiate(original, parent);
            if (!ReferenceEquals(original, template))
            {
                Clear();
                template = original;
                templateComponents = original.GetComponentCount();
                templateStyle = original.TryGetComponent<TMP_Text>(out var text) ? text.fontStyle : FontStyles.Normal;
                templateScale = original.transform.localScale;
            }
            while (kept.Count > 0)
            {
                var number = kept.Pop();
                if (!number) continue;
                if (number.transform.parent != parent) { Object.Destroy(number); continue; }
                var transform = number.transform;
                transform.localPosition = original.transform.localPosition;
                transform.localRotation = original.transform.localRotation;
                transform.SetAsLastSibling();
                number.SetActive(original.activeSelf);
                return number;
            }
            return Object.Instantiate(original, parent);
        }

        // A number that still matches the template once the game's own settings are put back waits for reuse.
        private static void Release(Object faded)
        {
            if (Pooling && faded is GameObject number && number && template && kept.Count < MostKept &&
                number.GetComponentCount() == templateComponents && number.transform.localScale == templateScale &&
                number.TryGetComponent<TMP_Text>(out var text) && text.fontStyle == templateStyle)
            {
                number.SetActive(false);
                kept.Push(number);
                return;
            }
            Object.Destroy(faded);
        }

        private static void Clear()
        {
            foreach (var number in kept)
                if (number) Object.Destroy(number);
            kept.Clear();
        }
    }
}
