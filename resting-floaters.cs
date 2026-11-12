using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Every frame each water volume computes the wave height under every object inside its trigger, about 2 µs each, and
    // the ocean's triggers reach over the floors of coastal bases, so items lying on them pay too. A floating item uses its
    // water level only to decide, in its owner's physics step, whether it floats (its centre below the level), splashes
    // (its lowest point below it while moving) and has ever floated. An owned item whose rigidbody sleeps with both its
    // centre and its lowest point more than half a metre above the highest wave possible there, and above the level it
    // last stored, gets the same answers from any level up to that bound, so its computation is skipped while that holds.
    // The bound is calm water plus the largest sum of the game's ten waves at the stronger wind and the volume's deepest
    // point. Items in the water, moving, kinematic, owned by others, never measured yet, and creatures and fish compute
    // natively, and a skipped item measures again in the next frame it no longer qualifies.
    internal static class RestingFloaters
    {
        private const float Margin = .5f;
        // The ten waves' heights summed (23.3 m) times the largest value of (product of two half-sines minus 0.2), 0.8.
        private const float WaveReach = 18.64f;
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".resting-floaters");
        private static readonly AccessTools.FieldRef<Floating, Rigidbody> body = AccessTools.FieldRefAccess<Floating, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<Floating, Collider> shape = AccessTools.FieldRefAccess<Floating, Collider>("m_collider");
        private static readonly AccessTools.FieldRef<Floating, ZNetView> view = AccessTools.FieldRefAccess<Floating, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Floating, float> level = AccessTools.FieldRefAccess<Floating, float>("m_waterLevel");
        private static readonly AccessTools.FieldRef<WaterVolume, float[]> depths = AccessTools.FieldRefAccess<WaterVolume, float[]>("m_normalizedDepth");
        private static readonly AccessTools.FieldRef<WaterVolume, float> flatDepth = AccessTools.FieldRefAccess<WaterVolume, float>("m_oneDepth");
        private static readonly AccessTools.FieldRef<Vector4> windOne = AccessTools.StaticFieldRefAccess<Vector4>(AccessTools.Field(typeof(WaterVolume), "s_globalWind1"));
        private static readonly AccessTools.FieldRef<Vector4> windTwo = AccessTools.StaticFieldRefAccess<Vector4>(AccessTools.Field(typeof(WaterVolume), "s_globalWind2"));
        private static readonly AccessTools.FieldRef<float> windBlend = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(WaterVolume), "s_globalWindAlpha"));
        private static WaterVolume boundVolume;
        private static int boundFrame = -1;
        private static float highestWave;
        internal static bool Running { get; private set; }
        // Off computes every level, as natively; the isolated client switches it for paired measurements.
        internal static bool Skipping { get; set; } = true;
        internal static long Skipped { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            boundVolume = null;
            patches.Patch(AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.UpdateFloaters)), transpiler: new HarmonyMethod(typeof(RestingFloaters), nameof(Transpile)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        // Before an object's wave height is computed, a resting item continues with the next object instead.
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            int transform = code.FindIndex(instruction => instruction.Calls(AccessTools.Method(typeof(IWaterInteractable), nameof(IWaterInteractable.GetTransform))));
            int present = transform < 1 ? -1 : code.FindIndex(transform, instruction => instruction.opcode == OpCodes.Brfalse || instruction.opcode == OpCodes.Brfalse_S);
            int surface = present < 0 ? -1 : code.FindIndex(present, instruction => instruction.Calls(AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface))));
            int stored = surface < 0 ? -1 : code.FindIndex(surface, instruction => instruction.Calls(AccessTools.Method(typeof(IWaterInteractable), nameof(IWaterInteractable.SetLiquidLevel))));
            if (stored < 0 || stored + 1 >= code.Count || !code[transform - 1].IsLdloc())
            {
                Plugin.Fallback("WaterVolume.UpdateFloaters no longer has the expected shape, so every level is computed natively.");
                return code;
            }
            var next = generator.DefineLabel();
            code[stored + 1].labels.Add(next);
            code.InsertRange(present + 1, new[]
            {
                new CodeInstruction(code[transform - 1].opcode, code[transform - 1].operand),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RestingFloaters), nameof(Resting))),
                new CodeInstruction(OpCodes.Brtrue, next)
            });
            return code;
        }

        internal static bool Resting(IWaterInteractable interactable, WaterVolume volume)
        {
            if (!Skipping || !(interactable is Floating item)) return false;
            var rigidbody = body(item);
            var collider = shape(item);
            var network = view(item);
            float stored = level(item);
            if (!(stored > -10000f) || !rigidbody || !collider || rigidbody.isKinematic || !rigidbody.IsSleeping() || !network || !network.IsValid() || !network.IsOwner())
                return false;
            float highest = Mathf.Max(stored, HighestWave(volume)) + Margin;
            if (!(collider.bounds.min.y > highest) || !(rigidbody.worldCenterOfMass.y - item.m_waterLevelOffset > highest)) return false;
            Skipped++;
            return true;
        }

        // The highest level WaterVolume.GetWaterSurface can return anywhere in the volume this frame; positive infinity when
        // the wind data is outside the ranges the game produces.
        private static float HighestWave(WaterVolume volume)
        {
            if (volume == boundVolume && Time.frameCount == boundFrame) return highestWave;
            boundVolume = volume;
            boundFrame = Time.frameCount;
            float calm = volume.transform.position.y + volume.m_surfaceOffset;
            if (!volume.m_useGlobalWind) return highestWave = calm;
            float blend = windBlend();
            float deepest = flatDepth(volume);
            if (!(deepest > -1f))
            {
                // Depth interpolates between the four corners with clamped steps, so it never exceeds the deepest corner.
                var corners = depths(volume);
                deepest = Mathf.Max(Mathf.Max(corners[0], corners[1]), Mathf.Max(corners[2], corners[3]));
            }
            float wind = Mathf.Max(Mathf.Abs(windOne().w), Mathf.Abs(windTwo().w));
            highestWave = blend >= 0f && blend <= 1f && !float.IsNaN(deepest) && !float.IsNaN(wind) ? calm + WaveReach * wind * Mathf.Clamp01(deepest) : float.PositiveInfinity;
            return highestWave;
        }
    }
}
