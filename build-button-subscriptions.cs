using System;
using HarmonyLib;

namespace WorldPerformance
{
    // Every time the build menu opens, changes tab or searches, the game sets its piece buttons up again, and each setup adds
    // the button's language-change subscription once more without removing the previous one. The list grows by the listed
    // pieces on every open, each addition copies all of it, and it keeps destroyed buttons alive. A button now drops its
    // earlier subscription before the game adds it again, and destroyed buttons drop theirs.
    internal static class BuildButtonSubscriptions
    {
        // The isolated test client sets false to compare the native growth.
        internal static bool Removing = true;
        private static readonly AccessTools.FieldRef<BuildUi, BuildUiPieceButton> specialButton = AccessTools.FieldRefAccess<BuildUi, BuildUiPieceButton>("m_specialPieceButton");

        private static void Drop(BuildUiPieceButton button)
        {
            if (Removing && button != null) Localization.OnLanguageChange = (Action)Delegate.Remove(Localization.OnLanguageChange, new Action(button.RefreshSearchTerm));
        }

        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup))]
        private static class Setup
        {
            private static void Prefix(BuildUiPieceButton __instance) => Drop(__instance);
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
