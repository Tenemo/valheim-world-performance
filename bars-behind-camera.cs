using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Each frame the enemy hud first shows every bar whose creature should have one, then hides again each bar whose point
    // lies behind the camera with its mirrored screen position inside the screen. A tame near the player keeps its bar, so
    // while it stands behind the camera its bar is switched on and off again in the same update, enabling and disabling every
    // image and text in it each frame. A hidden bar that is about to be shown is now left hidden when the same test, on the
    // same camera and point, says the update would hide it again; the update then skips its own test, as it does for any
    // hidden bar, and the bar ends the frame hidden exactly as before. Players, bosses and mounts keep the native path.
    internal static class BarsBehindCamera
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".bars-behind-camera");
        private static AccessTools.FieldRef<object, Character> character;
        private static AccessTools.FieldRef<object, bool> mount;
        private static System.Reflection.FieldInfo gui;
        internal static bool Running { get; private set; }
        // Off shows and hides the bars natively; the isolated client switches it for checks and paired measurements.
        internal static bool Skipping = true;
        internal static long Skipped;
        internal static string Failure { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            var data = AccessTools.Inner(typeof(EnemyHud), "HudData") ?? throw new MissingMemberException("EnemyHud.HudData changed.");
            character = AccessTools.FieldRefAccess<Character>(data, "m_character");
            mount = AccessTools.FieldRefAccess<bool>(data, "m_isMount");
            gui = AccessTools.Field(data, "m_gui") ?? throw new MissingFieldException("EnemyHud.HudData.m_gui changed.");
            // After Star Level System's transpiler, which finds the update's first bar shown by its native call.
            patches.Patch(AccessTools.Method(typeof(EnemyHud), "UpdateHuds"), transpiler: new HarmonyMethod(typeof(BarsBehindCamera), nameof(Transpiler)) { priority = Priority.Last });
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        // The update shows a bar with gui.SetActive(true) twice, first for every bar that should have one and then for each
        // bar its screen test keeps, and hides one twice. The first call passes the bar's hud data to Show as well.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
            var code = instructions.ToList();
            bool Sets(int index, OpCode value) => index + 2 < code.Count && code[index].LoadsField(gui) && code[index + 1].opcode == value && code[index + 2].Calls(setActive);
            var shows = Enumerable.Range(0, code.Count).Where(index => Sets(index, OpCodes.Ldc_I4_1)).ToList();
            int hides = Enumerable.Range(0, code.Count).Count(index => Sets(index, OpCodes.Ldc_I4_0));
            if (shows.Count != 2 || hides != 2 || shows[0] == 0 || !code[shows[0] - 1].IsLdloc() || code[shows[0] + 2].labels.Count > 0)
                throw new InvalidOperationException("EnemyHud.UpdateHuds changed: expected each bar to be shown, then hidden again behind the camera.");
            int call = shows[0] + 2;
            code[call].opcode = OpCodes.Call;
            code[call].operand = AccessTools.Method(typeof(BarsBehindCamera), nameof(Show));
            code.Insert(call, new CodeInstruction(code[shows[0] - 1].opcode, code[shows[0] - 1].operand));
            return code;
        }

        private static void Show(GameObject bar, bool active, object data)
        {
            if (active && Skipping && !bar.activeSelf && Hidden(data))
            {
                Skipped++;
                return;
            }
            bar.SetActive(active);
        }

        // The update's own screen test for a bar it has shown: it hides a bar unless the point is in front of the camera or
        // its screen position falls outside the screen.
        internal static bool Hidden(object data)
        {
            if (Failure != null) return false;
            try
            {
                var creature = character(data);
                if (!creature || mount(data) || creature.IsBoss()) return false;
                var camera = Utils.GetMainCamera();
                if (!camera) return false;
                Vector3 point = creature.IsPlayer() ? creature.GetHeadPoint() + Vector3.up * 0.3f : creature.GetTopPoint();
                Vector3 position = camera.WorldToScreenPointScaled(point);
                return !(position.x < 0f || position.x > (float)Screen.width || position.y < 0f || position.y > (float)Screen.height || position.z > 0f);
            }
            catch (Exception error)
            {
                Failure = error.ToString();
                Plugin.Log?.LogError("Leaving bars behind the camera hidden failed and stopped; the enemy hud shows and hides them natively. " + error);
                return false;
            }
        }
    }
}
