using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game finishes loaded assets, such as a location's models or a dungeon's rooms, for about 2 ms a frame at Unity's
    // Low background loading priority, also while the loading screen covers a trip, a respawn or the first spawn. While the
    // screen is fully up, the priority is now High, about 50 ms a frame, so those loads finish behind it rather than in view.
    internal static class LoadingScreenPriority
    {
        // The isolated test client sets false to compare the native priority.
        internal static bool Raising = true;
        private static bool raised;

        [HarmonyPatch(typeof(Hud), "UpdateBlackScreen")]
        private static class Screen
        {
            private static void Postfix(CanvasGroup ___m_loadingScreen) =>
                Raise(Raising && ___m_loadingScreen.gameObject.activeSelf && ___m_loadingScreen.alpha >= 1f);
        }

        [HarmonyPatch(typeof(Hud), "OnDestroy")]
        private static class Gone
        {
            private static void Prefix() => Raise(false);
        }

        private static void Raise(bool covered)
        {
            if (covered == raised) return;
            raised = covered;
            if (covered) BackgroundLoadingBudgetController.RequestLoadingBudget(ThreadPriority.High);
            else BackgroundLoadingBudgetController.ReleaseLoadingBudgetRequest(ThreadPriority.High);
        }
    }
}
