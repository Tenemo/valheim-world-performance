using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WorldPerformance
{
    // A key hint sets its three hint objects whenever its interface group gains or loses the input, when it is enabled and
    // when the input device changes. After each object the game asks the hint's Localize to refresh, and that Localize sits
    // above the whole in-game interface, so every hint object walks every label of every panel, shown or not. A hint only
    // needs its own labels refreshed: language and input device changes reach every label through Localize itself.
    internal static class InputHintRefresh
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".input-hint-refresh");
        internal static bool Running { get; private set; }
        // Off refreshes the whole interface natively; the isolated client switches it for its checks and paired measurements.
        internal static bool Lean { get; set; } = true;

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(UIInputHint), "UpdateVisiblityAndLayout"), prefix: new HarmonyMethod(typeof(InputHintRefresh), nameof(UpdateHintObject)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        private static bool UpdateHintObject(GameObject go, bool condition, Localize ___m_localize)
        {
            if (!Lean) return true;
            if (go == null) return false;
            go.SetActive(condition);
            if (condition && ___m_localize) Localization.instance.ReLocalizeVisible(go.transform);
            LayoutRebuilder.ForceRebuildLayoutImmediate(go.transform as RectTransform);
            return false;
        }
    }
}
