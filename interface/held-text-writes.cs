using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;

namespace WorldPerformance
{
    // The game writes the armour value, each crafting and building requirement's amount and the selected build piece's name
    // every frame, and other mods write over them in the same frame: Epic Loot and BetterUI the armour value, AzuCraftyBoxes
    // each amount as available over needed and the piece's name with how many can be built. Each of those texts changes and
    // changes back, so TextMesh Pro lays it out and generates it again every frame, several times for AzuCraftyBoxes'
    // auto-sized amounts. While those updates and the mods' additions to them run, text writes are now held, a read returns
    // the held text, and each text is written once with the value it ends with, which is what players see.
    internal static class HeldTextWrites
    {
        private static int depth;
        private static readonly Dictionary<TMP_Text, string> held = new Dictionary<TMP_Text, string>();
        private static readonly List<TMP_Text> order = new List<TMP_Text>();

        internal static void Initialize(PerformancePatches harmony)
        {
            var begin = new HarmonyMethod(typeof(HeldTextWrites), nameof(Begin)) { priority = Priority.First };
            var end = new HarmonyMethod(typeof(HeldTextWrites), nameof(End));
            foreach (var update in new[] { AccessTools.Method(typeof(InventoryGui), "UpdateCharacterStats"), AccessTools.Method(typeof(InventoryGui), "SetupRequirement"),
                AccessTools.Method(typeof(Hud), "SetupPieceInfo") })
                harmony.Patch(update ?? throw new InvalidOperationException("The game's armour, requirement or build piece texts changed."), prefix: begin, finalizer: end);
            harmony.Patch(AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)), prefix: new HarmonyMethod(typeof(HeldTextWrites), nameof(Write)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(TMP_Text), nameof(TMP_Text.text)), prefix: new HarmonyMethod(typeof(HeldTextWrites), nameof(Read)));
        }

        // The inventory screen's switch: off writes natively for paired measurements.
        private static void Begin(out bool __state)
        {
            __state = InventoryScreen.Lean;
            if (__state) depth++;
        }

        // After every mod's postfix, and also when one throws.
        private static void End(bool __state)
        {
            if (!__state || --depth > 0) return;
            for (int index = 0; index < order.Count; index++)
                if (order[index]) order[index].text = held[order[index]];
            held.Clear();
            order.Clear();
        }

        private static bool Write(TMP_Text __instance, string value)
        {
            if (depth == 0) return true;
            if (!held.ContainsKey(__instance)) order.Add(__instance);
            held[__instance] = value;
            return false;
        }

        private static bool Read(TMP_Text __instance, ref string __result)
        {
            if (depth == 0 || !held.TryGetValue(__instance, out string value)) return true;
            __result = value;
            return false;
        }
    }
}
