using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Every instanced grass and scenery patch reads the main camera's position for its level of detail each frame, one
    // after another in the game's single pass over them, which moves no camera. The position is now read once as the pass
    // begins and given to each patch that asks for that camera's position during it.
    internal static class InstanceCameraPosition
    {
        private static bool passing;
        private static Camera camera;
        private static Vector3 position;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            harmony.Patch(AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomUpdate)),
                prefix: new HarmonyMethod(typeof(InstanceCameraPosition), nameof(Begin)), finalizer: new HarmonyMethod(typeof(InstanceCameraPosition), nameof(End)));
            harmony.Patch(AccessTools.Method(typeof(InstanceRenderer), nameof(InstanceRenderer.CustomUpdate)), transpiler: new HarmonyMethod(typeof(InstanceCameraPosition), nameof(Transpiler)));
        }

        private static void Begin(List<IMonoUpdater> source)
        {
            if (!ReferenceEquals(source, InstanceRenderer.Instances)) return;
            camera = Utils.GetMainCamera();
            if (camera == null) return;
            position = camera.transform.position;
            passing = true;
        }

        private static void End() { passing = false; camera = null; }

        // mainCamera.transform.position becomes Position(mainCamera); mainCamera is the local the method stores first.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var main = AccessTools.Method(typeof(Utils), nameof(Utils.GetMainCamera));
            var transform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
            var getPosition = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
            var code = instructions.ToList();
            if (!code[0].Calls(main) || !code[1].IsStloc()) throw new InvalidOperationException("The instance renderer's camera changed.");
            int local = Transpilers.Local(code[1]);
            int replaced = 0;
            for (int index = 1; index + 2 < code.Count; index++)
            {
                if (!code[index].IsLdloc() || Transpilers.Local(code[index]) != local || !code[index + 1].Calls(transform) || !code[index + 2].Calls(getPosition)) continue;
                if (code[index + 1].labels.Count > 0 || code[index + 2].labels.Count > 0) throw new InvalidOperationException("The instance renderer's camera changed.");
                code[index + 1].opcode = OpCodes.Call;
                code[index + 1].operand = AccessTools.Method(typeof(InstanceCameraPosition), nameof(Position));
                code[index + 2].opcode = OpCodes.Nop;
                code[index + 2].operand = null;
                replaced++;
            }
            if (replaced != 2) throw new InvalidOperationException("The instance renderer's camera changed.");
            return code;
        }

        internal static Vector3 Position(Camera main) => passing && ReferenceEquals(main, camera) ? position : main.transform.position;
    }
}
