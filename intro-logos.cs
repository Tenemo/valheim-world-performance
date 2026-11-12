using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game's start shows the Coffee Stain and Iron Gate logos for 2 s each while the main menu loads at a Normal loading
    // budget, and only then the Valheim logo with its loading bar at a High budget. Approved by the owner on 2026-11-10 as a
    // visible change, the two logos are skipped: the Valheim logo and loading bar show at once and the menu loads at the High
    // budget from the start. The save data wait, the startup messages and every prompt keep their order, and the menu, its
    // music and the mods' menu additions start when the menu scene activates, as after the logos.
    internal static class IntroLogos
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".intro-logos");
        private static AccessTools.FieldRef<SceneLoader, bool> showLogos;
        private static bool checkedMenu;
        internal static bool Skipped { get; private set; }

        internal static void Start()
        {
            string problem = Problem();
            if (problem != null)
            {
                Plugin.Fallback("The start keeps showing the intro logos: " + problem);
                return;
            }
            showLogos = AccessTools.FieldRefAccess<SceneLoader, bool>("_showLogos");
            patches.Patch(AccessTools.Method(typeof(SceneLoader), "Start"), prefix: new HarmonyMethod(typeof(IntroLogos), nameof(Skip)));
            patches.Patch(AccessTools.Method(typeof(FejdStartup), "Awake"), postfix: new HarmonyMethod(typeof(IntroLogos), nameof(MenuLoaded)));
        }

        // The skip clears the one flag the loading coroutine reads before both logos. A game update that shows them another
        // way, adds a logo or sets the flag elsewhere fails this check instead of quietly showing the logos again.
        private static string Problem()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var flag = AccessTools.Field(typeof(SceneLoader), "_showLogos");
            var fadeLogo = AccessTools.Method(typeof(SceneLoader), "FadeLogo");
            var load = AccessTools.Method(typeof(SceneLoader), "LoadSceneAsync");
            if (flag == null || flag.FieldType != typeof(bool) || fadeLogo == null || load == null || AccessTools.Method(typeof(SceneLoader), "Start") == null)
                return "SceneLoader no longer has its logo flag, Start, FadeLogo and LoadSceneAsync.";
            var logos = typeof(SceneLoader).GetFields(all).Where(field => field.FieldType == typeof(GameObject)).Select(field => field.Name).OrderBy(name => name);
            if (!logos.SequenceEqual(new[] { "coffeeStainLogo", "gameLogo", "ironGateLogo" })) return "SceneLoader's logos changed: " + string.Join(", ", logos) + ".";
            foreach (var method in typeof(SceneLoader).GetMethods(all).Where(method => method.Name != "Awake" && method.GetMethodBody() != null))
                if (PatchProcessor.GetOriginalInstructions(method).Any(code => code.StoresField(flag))) return "SceneLoader." + method.Name + " sets the logo flag.";
            var code = PatchProcessor.GetOriginalInstructions(AccessTools.EnumeratorMoveNext(load));
            var reads = Enumerable.Range(0, code.Count).Where(index => code[index].LoadsField(flag)).ToList();
            if (reads.Count != 1 || reads[0] + 1 >= code.Count || code[reads[0] + 1].opcode != OpCodes.Brfalse && code[reads[0] + 1].opcode != OpCodes.Brfalse_S)
                return "the loading coroutine no longer branches on the logo flag once.";
            var end = code.FindIndex(instruction => instruction.labels.Contains((Label)code[reads[0] + 1].operand));
            var fades = Enumerable.Range(0, code.Count).Where(index => code[index].Calls(fadeLogo)).ToList();
            if (end < 0 || fades.Count != 2 || fades.Any(index => index < reads[0] || index > end)) return "the loading coroutine shows logos outside the logo flag's branch.";
            return null;
        }

        private static void Skip(SceneLoader __instance)
        {
            showLogos(__instance) = false;
            Skipped = true;
        }

        // The menu's first load comes after the loading coroutine, so a start that showed the logos is known here.
        private static void MenuLoaded()
        {
            if (!Skipped && !checkedMenu) Plugin.Fallback("The start showed the intro logos: SceneLoader started before World Performance.");
            checkedMenu = true;
        }
    }
}
