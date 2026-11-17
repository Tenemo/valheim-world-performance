using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace WorldPerformance
{
    // Every time the build menu opens, changes tab or searches, the game sets its piece buttons up again, and each setup adds
    // the button's language-change subscription once more without removing the previous one. The list grows by the listed
    // pieces on every open, each addition copies all of it, and it keeps destroyed buttons alive. A button that is already
    // subscribed is not added again, and destroyed buttons drop their subscription. Removing and adding each button again
    // would search and copy the whole list for every button, about 380 times an opening at a large base.
    internal static class BuildButtonSubscriptions
    {
        // The isolated test client sets false to compare the native growth.
        internal static bool Removing = true;
        // The shipped feature; the isolated test client sets false to remove and add each button again on every setup.
        internal static bool Once = true;
        // The instance ids of the buttons whose subscription this keeps on the list.
        private static readonly HashSet<int> subscribed = new HashSet<int>();
        private static readonly AccessTools.FieldRef<BuildUi, BuildUiPieceButton> specialButton = AccessTools.FieldRefAccess<BuildUi, BuildUiPieceButton>("m_specialPieceButton");

        private static void Drop(BuildUiPieceButton button)
        {
            if (!Removing || button == null) return;
            subscribed.Remove(button.GetInstanceID());
            Localization.OnLanguageChange = (Action)Delegate.Remove(Localization.OnLanguageChange, new Action(button.RefreshSearchTerm));
        }

        // Replaces the setup's Delegate.Combine, so an already subscribed button leaves the list as it is.
        private static Delegate Subscribe(Delegate list, Delegate added)
        {
            if (Removing && added.Target is BuildUiPieceButton button && !subscribed.Add(button.GetInstanceID()) && Once) return list;
            return Delegate.Combine(list, added);
        }

        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup))]
        private static class Setup
        {
            private static void Prefix(BuildUiPieceButton __instance)
            {
                if (!Once) Drop(__instance);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var combine = AccessTools.Method(typeof(Delegate), nameof(Delegate.Combine), new[] { typeof(Delegate), typeof(Delegate) });
                var code = instructions.ToList();
                var calls = code.Where(instruction => instruction.Calls(combine)).ToList();
                if (calls.Count != 1) throw new InvalidOperationException("The build menu button's language subscription changed.");
                calls[0].operand = AccessTools.Method(typeof(BuildButtonSubscriptions), nameof(Subscribe));
                return code;
            }
        }

        // The repair and remove button is destroyed and made again on every update of the list.
        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons")]
        private static class Special
        {
            private static void Prefix(BuildUi __instance) => Drop(specialButton(__instance));
        }

        [HarmonyPatch(typeof(BuildUi), "OnDestroy")]
        private static class Closed
        {
            private static void Prefix(BuildUi __instance)
            {
                foreach (var button in __instance.GetComponentsInChildren<BuildUiPieceButton>(true)) Drop(button);
            }
        }
    }
}
