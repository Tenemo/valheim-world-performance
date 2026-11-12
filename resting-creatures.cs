using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Each physics step a walking creature reads its rigidbody's velocity and writes it straight back, and that write alone
    // restarts the physics engine's sleep timer, so an owned creature never sleeps however long it stands, while a remote one
    // sleeps at once. A body left awake costs ground contacts, a contact callback and a published transform every step. The
    // unchanged velocity is no longer written, and a body the physics step left still on the ground sleeps at once. Anything
    // that moves it wakes it: a new velocity or force, a body touching it, the loss of what it stands on. Only the repeated
    // touch of a trigger such as a fire stops reaching a sleeping body, so every half second it wakes for a moment. Asleep it
    // counts as touching the ground, as the game's sleeping bodies do, and keeps the ground tilt rays and ground data of its
    // last awake step, since it has not moved. Players, swimmers and fliers stay native.
    // An owned creature that has done nothing for half a second, whose body sleeps and whose tilt has settled, runs its fixed
    // update only every fourth step with the skipped steps' time added; at Tenemo's base that is most of his tames, and each
    // update cost about 20 µs. Anything it does or undergoes runs it again at once: a decision to move, a push, a hit or a
    // heal, a status effect, an attack, water, a rider, a woken body. Its settled tilt is not rewritten each update either,
    // since that write moves a lox's body capsules, which sit under its visual, and so woke its body for a physics step.
    internal static class RestingCreatures
    {
        private delegate bool GroundQuery(Character character, Vector3 point, out float height, out Vector3 normal);

        private sealed class Ground
        {
            internal readonly (bool hit, float height, Vector3 normal)[] rays = new (bool, float, Vector3)[2];
            internal bool raysKept, dataKept;
            internal int nextRay;
            internal float height;
            internal Vector3 normal;
            internal Heightmap.Biome biome;
            internal Heightmap.BiomeArea area;
            internal Heightmap map;
            // Steps since the creature last did anything, and the step time its skipped updates still owe it.
            internal int restSteps;
            internal float owed;
            // Whether the tilt's last run left the visual all but still, and the rotation before the run under way.
            internal bool tiltStill, tiltRunning;
            internal Quaternion tiltBefore;
        }

        internal const string PatchOwner = Plugin.Id + ".resting-creatures";
        private const uint WakeSteps = 25;
        private const float StillSquared = 0.0001f;
        private static readonly Harmony patches = new Harmony(PatchOwner);
        private static readonly ConditionalWeakTable<Character, Ground> grounds = new ConditionalWeakTable<Character, Ground>();
        private static readonly GroundQuery groundHeight = AccessTools.MethodDelegate<GroundQuery>(AccessTools.Method(typeof(Character), "GetGroundHeight"));
        private static readonly AccessTools.FieldRef<Character, Rigidbody> bodies = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<Character, float> lastGroundTouch = AccessTools.FieldRefAccess<Character, float>("m_lastGroundTouch");
        internal static bool Running { get; private set; }
        internal static string Failure { get; private set; }
        // Off writes every velocity and casts every ray, as natively; the isolated client switches each for paired measurements.
        internal static bool Sleeping = true;
        internal static bool Settling = true;
        internal static bool Waking = true;
        internal static bool ReusingGround = true;
        internal static bool Spacing = true;
        internal static bool HoldingTilt = true;
        // Off looks each creature's ground up in the table at every hook and tests a remote creature's network view last, as
        // before; the isolated client switches it for paired measurements.
        internal static bool Lookups = true;
        // The creature whose ground the last lookup found. A creature's fixed update runs its hooks one after another, and the
        // table never drops an entry while its creature lives, so the same creature finds the same ground again.
        private static Character lastCreature;
        private static Ground lastGround;
        // A creature that has done nothing for SettleSteps runs its fixed update only every SpacedSteps-th step while its
        // body sleeps and its tilt is settled, with the skipped steps' time added; anything it does runs it at once.
        internal static int SpacedSteps = 4, SettleSteps = 25;
        // The squared change of the visual's rotation a step below which its tilt counts as settled: about a hundredth of a degree.
        private const float StillTurn = 1e-8f;
        internal static long Skipped, Wakes, Settled, RaysKept, RaysCast, DataKept, DataRead, SpacedSkips, SpacedRuns, Stirred, TiltsHeld;
        private static readonly AccessTools.FieldRef<Character, Vector3> moveDirs = AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, Vector3> pushForces = AccessTools.FieldRefAccess<Character, Vector3>("m_pushForce");
        private static readonly AccessTools.FieldRef<Character, Vector3> currentVelocities = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        private static readonly AccessTools.FieldRef<Character, float> standUps = AccessTools.FieldRefAccess<Character, float>("m_standUp");
        private static readonly AccessTools.FieldRef<Character, float> liquidDepths = AccessTools.FieldRefAccess<Character, float>("m_cashedInLiquidDepth");
        private static readonly AccessTools.FieldRef<Character, SEMan> effectManagers = AccessTools.FieldRefAccess<Character, SEMan>("m_seman");
        private static readonly AccessTools.FieldRef<SEMan, List<StatusEffect>> effects = AccessTools.FieldRefAccess<SEMan, List<StatusEffect>>("m_statusEffects");
        private static readonly AccessTools.FieldRef<Humanoid, Attack> attacks = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
        private static readonly AccessTools.FieldRef<Character, ZNetView> views = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Character, BaseAI> minds = AccessTools.FieldRefAccess<Character, BaseAI>("m_baseAI");
        private static readonly AccessTools.FieldRef<Character, GameObject> visuals = AccessTools.FieldRefAccess<Character, GameObject>("m_visual");

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            try
            {
                var type = typeof(Character);
                patches.Patch(AccessTools.Method(type, "UpdateWalking"), transpiler: new HarmonyMethod(typeof(RestingCreatures), nameof(TranspileWalking)));
                patches.Patch(AccessTools.Method(type, "OnCollisionStay"), postfix: new HarmonyMethod(typeof(RestingCreatures), nameof(Touched)));
                patches.Patch(AccessTools.Method(type, "UpdateGroundTilt"), prefix: new HarmonyMethod(typeof(RestingCreatures), nameof(HoldTilt)), postfix: new HarmonyMethod(typeof(RestingCreatures), nameof(Tilted)), transpiler: new HarmonyMethod(typeof(RestingCreatures), nameof(TranspileTilt)));
                patches.Patch(AccessTools.Method(type, "UpdateLava"), transpiler: new HarmonyMethod(typeof(RestingCreatures), nameof(TranspileLava)));
                patches.Patch(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.CustomFixedUpdate)), prefix: new HarmonyMethod(typeof(RestingCreatures), nameof(SpaceSteps)));
                patches.Patch(AccessTools.Method(type, nameof(Character.SetHealth)), postfix: new HarmonyMethod(typeof(RestingCreatures), nameof(Stir)));
            }
            catch (Exception error)
            {
                // A changed game method leaves creatures awake.
                Failure = (error.InnerException ?? error).Message;
                Running = false;
                patches.UnpatchSelf();
                Plugin.Fallback("Resting creatures stay awake: " + Failure);
            }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        internal static string Report() => $"skipped writes {Skipped}, wakes {Wakes}, settled {Settled}, rays kept {RaysKept}, cast {RaysCast}, ground data kept {DataKept}, read {DataRead}, spaced skips {SpacedSkips}, spaced runs {SpacedRuns}, stirred {Stirred}, tilts held {TiltsHeld}";

        private static Ground Find(Character creature)
        {
            if (Lookups && (object)creature == lastCreature) return lastGround;
            var ground = grounds.GetOrCreateValue(creature);
            if (Lookups)
            {
                lastCreature = creature;
                lastGround = ground;
            }
            return ground;
        }

        private static bool TryFind(Character creature, out Ground ground)
        {
            if (Lookups && (object)creature == lastCreature)
            {
                ground = lastGround;
                return true;
            }
            if (!grounds.TryGetValue(creature, out ground)) return false;
            if (Lookups)
            {
                lastCreature = creature;
                lastGround = ground;
            }
            return true;
        }

        // Whether the creature is doing or undergoing anything its fixed update must answer at once. Every test only reads, so
        // a creature another player's game owns is answered by its network view first.
        internal static bool Busy(Humanoid creature)
        {
            if (Lookups)
            {
                var owner = views(creature);
                if (!owner.IsValid() || !owner.IsOwner()) return true;
            }
            if (creature.IsPlayer() || moveDirs(creature) != Vector3.zero || pushForces(creature) != Vector3.zero || standUps(creature) > 0f || liquidDepths(creature) > 0f) return true;
            if (currentVelocities(creature).sqrMagnitude >= StillSquared || bodies(creature).linearVelocity.sqrMagnitude >= StillSquared) return true;
            if (attacks(creature) != null || effects(effectManagers(creature)).Count > 0 || creature.IsAttached()) return true;
            var mind = minds(creature);
            if (!mind || mind.HaveRider()) return true;
            var view = views(creature);
            return !view.IsValid() || !view.IsOwner();
        }

        private static bool SpaceSteps(Humanoid __instance, ref float fixedDeltaTime)
        {
            if (!Sleeping || !Spacing) return true;
            var ground = Find(__instance);
            if (Busy(__instance)) ground.restSteps = 0;
            else ground.restSteps++;
            if (ground.restSteps <= SettleSteps || (HoldingTilt && !ground.tiltStill) || ground.restSteps % SpacedSteps == 0 || !bodies(__instance).IsSleeping() || WakeStep(__instance))
            {
                fixedDeltaTime += ground.owed;
                ground.owed = 0f;
                if (ground.restSteps > SettleSteps) SpacedRuns++;
                return true;
            }
            ground.owed += fixedDeltaTime;
            SpacedSkips++;
            return false;
        }

        // Each sleeping body wakes in its own one of every WakeSteps steps, so that triggers reach it.
        private static bool WakeStep(Character character) =>
            Waking && (uint)((long)(Time.fixedTimeAsDouble / Time.fixedDeltaTime + .5) + character.GetInstanceID()) % WakeSteps == 0;

        // A change of health, from a hit, a burn or healing, runs the creature's next steps in full.
        private static void Stir(Character __instance)
        {
            if (__instance.IsPlayer() || !TryFind(__instance, out var ground) || ground.restSteps == 0) return;
            ground.restSteps = 0;
            Stirred++;
        }

        // The velocity written back after status effects had their say becomes WriteVelocity, with the creature added.
        private static IEnumerable<CodeInstruction> TranspileWalking(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int effects = code.FindIndex(instruction => instruction.Calls(AccessTools.Method(typeof(SEMan), nameof(SEMan.ModifyWalkVelocity))));
            int write = effects < 0 ? -1 : code.FindIndex(effects, instruction => instruction.Calls(AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.linearVelocity))));
            if (write < 0) throw new InvalidOperationException("Character.UpdateWalking no longer writes the velocity after status effects.");
            code[write].opcode = OpCodes.Call;
            code[write].operand = AccessTools.Method(typeof(RestingCreatures), nameof(WriteVelocity));
            code.Insert(write, new CodeInstruction(OpCodes.Ldarg_0));
            return code;
        }

        private static void WriteVelocity(Rigidbody body, Vector3 velocity, Character character)
        {
            if (!Sleeping || character.IsPlayer() || !velocity.Equals(body.linearVelocity))
            {
                body.linearVelocity = velocity;
                return;
            }
            Skipped++;
            if (!body.IsSleeping()) return;
            lastGroundTouch(character) = 0f;
            if (WakeStep(character))
            {
                Wakes++;
                // A woken body finds its ground contact a step late, and falling for that step would walk it down a slope.
                body.AddForce(-Physics.gravity, ForceMode.Acceleration);
            }
        }

        // After the physics step no force can be waiting, so a body it left still can sleep without losing one. A new velocity or
        // force in the next step wakes it again.
        private static void Touched(Character __instance)
        {
            if (!Sleeping || !Settling || __instance.IsPlayer()) return;
            var body = bodies(__instance);
            if (body.IsSleeping() || body.linearVelocity.sqrMagnitude >= StillSquared || !__instance.IsOwner()) return;
            Settled++;
            body.Sleep();
        }

        private static IEnumerable<CodeInstruction> TranspileTilt(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var rays = code.Where(instruction => instruction.Calls(AccessTools.Method(typeof(Character), "GetGroundHeight"))).ToList();
            if (rays.Count != 2) throw new InvalidOperationException("Character.UpdateGroundTilt no longer casts two rays.");
            foreach (var ray in rays)
            {
                ray.opcode = OpCodes.Call;
                ray.operand = AccessTools.Method(typeof(RestingCreatures), nameof(GroundHeight));
            }
            return code;
        }

        // The tilt eases the visual toward the ground's slope by a twentieth of the way each step and writes the visual's
        // rotation even when nothing is left to turn, and that write moves the colliders under the visual, a lox's two body
        // capsules, which wakes its sleeping body. Once a step turns it by less than about a hundredth of a degree, so that less
        // than a quarter of a degree is left, a resting creature's tilt holds until its body wakes or it does something.
        private static bool HoldTilt(Character __instance)
        {
            if (!Sleeping || !HoldingTilt || !TryFind(__instance, out var ground)) return true;
            var visual = visuals(__instance);
            if (!visual)
            {
                ground.tiltStill = true;
                return true;
            }
            if (ground.tiltStill && ground.restSteps > SettleSteps && bodies(__instance).IsSleeping())
            {
                TiltsHeld++;
                return false;
            }
            ground.tiltBefore = visual.transform.localRotation;
            ground.tiltRunning = true;
            return true;
        }

        private static void Tilted(Character __instance)
        {
            if (!TryFind(__instance, out var ground) || !ground.tiltRunning) return;
            ground.tiltRunning = false;
            Quaternion before = ground.tiltBefore, after = visuals(__instance).transform.localRotation;
            float turned = (after.x - before.x) * (after.x - before.x) + (after.y - before.y) * (after.y - before.y) + (after.z - before.z) * (after.z - before.z) + (after.w - before.w) * (after.w - before.w);
            ground.tiltStill = turned < StillTurn;
        }

        // The tilt casts two rays in a row each step; the first one's answer to "asleep" holds for both.
        private static bool GroundHeight(Character character, Vector3 point, out float height, out Vector3 normal)
        {
            if (!ReusingGround) return groundHeight(character, point, out height, out normal);
            var ground = Find(character);
            int ray = ground.nextRay;
            ground.nextRay ^= 1;
            if (ray == 0 && !bodies(character).IsSleeping()) ground.raysKept = false;
            if (ground.raysKept)
            {
                RaysKept++;
                var kept = ground.rays[ray];
                height = kept.height;
                normal = kept.normal;
                return kept.hit;
            }
            RaysCast++;
            bool hit = groundHeight(character, point, out height, out normal);
            ground.rays[ray] = (hit, height, normal);
            if (ray == 1) ground.raysKept = bodies(character).IsSleeping();
            return hit;
        }

        private static IEnumerable<CodeInstruction> TranspileLava(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int query = code.FindIndex(instruction => instruction.Calls(AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundData))));
            if (query < 0) throw new InvalidOperationException("Character.UpdateLava no longer reads ground data.");
            code[query].opcode = OpCodes.Call;
            code[query].operand = AccessTools.Method(typeof(RestingCreatures), nameof(GroundData));
            code.Insert(query, new CodeInstruction(OpCodes.Ldarg_0));
            return code;
        }

        private static void GroundData(ZoneSystem zones, ref Vector3 point, out Vector3 normal, out Heightmap.Biome biome, out Heightmap.BiomeArea area, out Heightmap map, Character character)
        {
            if (!ReusingGround || character.IsPlayer())
            {
                zones.GetGroundData(ref point, out normal, out biome, out area, out map);
                return;
            }
            var ground = Find(character);
            bool asleep = bodies(character).IsSleeping();
            if (ground.dataKept && asleep && ground.map)
            {
                DataKept++;
                point.y = ground.height;
                normal = ground.normal;
                biome = ground.biome;
                area = ground.area;
                map = ground.map;
                return;
            }
            DataRead++;
            zones.GetGroundData(ref point, out normal, out biome, out area, out map);
            ground.dataKept = asleep;
            ground.height = point.y;
            ground.normal = normal;
            ground.biome = biome;
            ground.area = area;
            ground.map = map;
        }
    }
}
