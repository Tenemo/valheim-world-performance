using System.Collections.Generic;
using HarmonyLib;

namespace WorldPerformance
{
    // Every time the build menu opens, changes list or searches, the game pushes the shown piece buttons onto a stack and
    // pops them back for the new list, so each piece gets the button mirrored from the one it had: every button changes
    // icon, place in the grid and gamepad neighbours, and all of it is set and laid out again. Reversing the list before the
    // pushes gives each piece the button it had last time, so what has not changed is not set again.
    internal static class BuildButtonOrder
    {
        // The isolated test client sets false to compare the native order.
        internal static bool Keeping = true;
        private static readonly AccessTools.FieldRef<BuildUi, List<BuildUiPieceButton>> shown = AccessTools.FieldRefAccess<BuildUi, List<BuildUiPieceButton>>("m_pieceButtons");

        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons")]
        private static class Keep
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(BuildUi __instance)
            {
                if (Keeping) shown(__instance).Reverse();
            }
        }
    }
}
