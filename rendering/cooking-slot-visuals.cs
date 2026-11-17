using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    [HarmonyPatch(typeof(CookingStation), "SetSlotVisual")]
    internal static class CookingSlotVisuals
    {
        // Destroy runs at the end of the frame. Detach obsolete models first so another refresh, including
        // NoSmokeStayLit's second pass, sees the current model instead of replacing the same old child again.
        private static void Prefix(CookingStation __instance, int i, string item)
        {
            var slot = __instance.m_slots[i];
            Transform retainedModel = null;
            for (int child = slot.childCount - 1; child >= 0; child--)
            {
                var model = slot.GetChild(child);
                if (item != "" && retainedModel == null && model.name == item)
                {
                    retainedModel = model;
                    continue;
                }
                model.SetParent(null, true);
                Object.Destroy(model.gameObject);
            }
        }
    }
}
