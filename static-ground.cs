using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Once an object with static physics, such as a pickable, a bush, a rock or a planted crop, has rested for its wait time,
    // the slow updater checks it about once a second for falling and being pushed up, and each check casts a ray from 6 km
    // down onto the terrain layer: 2.5 µs each, about 0.16 ms a frame at the base, where 2,488 objects check. The ray's answer
    // depends only on the object's horizontal position and on the terrain layer's colliders, which only zone heightmaps carry
    // and which change only when a heightmap rebuilds or clears its collision mesh, or is enabled, disabled or destroyed. The
    // mod keeps each answer by exact horizontal position and forgets them all whenever any of that happens, so these checks
    // get the native answers without casting the ray. The checks act only on an object above its ground or below it; when its
    // kept answer shows neither, they would read the same position, get the same answer and do nothing, so the mod skips them.
    // Checks against solids stay native.
    internal static class StaticGround
    {
        // Answers kept at most; more are forgotten together, which only means casting their rays again.
        private const int Capacity = 1 << 16;
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".static-ground");
        private static readonly Dictionary<Vector2, (bool hit, float height)> answers = new Dictionary<Vector2, (bool hit, float height)>();
        private static ZoneSystem answering;
        private static readonly AccessTools.FieldRef<StaticPhysics, bool> falling = AccessTools.FieldRefAccess<StaticPhysics, bool>("m_falling");
        private static readonly AccessTools.FieldRef<StaticPhysics, float> waitedUntil = AccessTools.FieldRefAccess<StaticPhysics, float>("m_updateTime");
        internal static bool Running { get; private set; }
        // Off casts every ray, as natively, while answers stay kept; the isolated client switches it for paired measurements.
        internal static bool Caching { get; set; } = true;
        internal static long Kept { get; private set; }
        internal static long Cast { get; private set; }
        internal static long Forgotten { get; private set; }
        internal static int Answers => answers.Count;

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            answers.Clear();
            var forget = new HarmonyMethod(typeof(StaticGround), nameof(Forget));
            foreach (string method in new[] { "RebuildCollisionMesh", nameof(Heightmap.Clear), "OnEnable", "OnDisable", "OnDestroy" })
                patches.Patch(AccessTools.Method(typeof(Heightmap), method), postfix: forget);
            var transpiler = new HarmonyMethod(typeof(StaticGround), nameof(Transpile));
            foreach (string method in new[] { "GetFallHeight", "PushUp" })
                patches.Patch(AccessTools.Method(typeof(StaticPhysics), method), transpiler: transpiler);
            patches.Patch(AccessTools.Method(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate)), prefix: new HarmonyMethod(typeof(StaticGround), nameof(ChecksAct)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            answers.Clear();
        }

        // The check's ground query becomes GroundHeight, which takes the same stack: the zone system, the position and the
        // height to set.
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            var native = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), new[] { typeof(Vector3), typeof(float).MakeByRefType() });
            var queries = code.Where(instruction => instruction.Calls(native)).ToList();
            if (queries.Count != 1)
            {
                Plugin.Fallback($"StaticPhysics.{original.Name} no longer has the expected shape, so its ground checks cast their rays natively.");
                return code;
            }
            queries[0].opcode = OpCodes.Call;
            queries[0].operand = AccessTools.Method(typeof(StaticGround), nameof(GroundHeight));
            return code;
        }

        // Before the slow update: whether the native checks would act, as CheckFall and PushUp decide, so that they run only then.
        // Outside the active area they do nothing. Objects that are falling, still waiting or checking against solids are left
        // to the native update.
        private static bool ChecksAct(StaticPhysics __instance, float time, Vector2s referenceZone)
        {
            if (!Caching || __instance.m_checkSolids || falling(__instance) || !(time > waitedUntil(__instance))) return true;
            var position = __instance.transform.position;
            if (ZNetScene.OutsideActiveArea(position, referenceZone)) return false;
            bool hit = GroundHeight(ZoneSystem.instance, position, out float ground);
            return __instance.m_fall && position.y > (hit ? ground : position.y) + 0.05f || __instance.m_pushUp && hit && position.y < ground - 0.05f;
        }

        private static bool GroundHeight(ZoneSystem zones, Vector3 position, out float height)
        {
            if (!Caching) return zones.GetGroundHeight(position, out height);
            if (!ReferenceEquals(zones, answering))
            {
                answers.Clear();
                answering = zones;
            }
            var spot = new Vector2(position.x, position.z);
            if (answers.TryGetValue(spot, out var answer))
            {
                Kept++;
                height = answer.height;
                return answer.hit;
            }
            bool hit = zones.GetGroundHeight(position, out height);
            Cast++;
            if (answers.Count >= Capacity) answers.Clear();
            answers[spot] = (hit, height);
            return hit;
        }

        private static void Forget()
        {
            answers.Clear();
            Forgotten++;
        }
    }
}
