using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Three parts of every creature's and player's physics step that repeat work with nothing to show for it:
    // - The sliding, water and flying effects are set up with the character's position, liquid level and network id, which
    //   are read before the setup finds that the character has no such effect. Those reads are now made only for effects the
    //   character has.
    // - The body's friction reads the collider's material up to four times and writes its friction values every step,
    //   though they rarely change. It is now read once, and a value is written only when it differs from the material's: the
    //   material ends each step with the same values as before.
    // - Standing on something, the step looks up whether the ground's object is terrain. Whether a collider's object has a
    //   heightmap never changes while it exists, so the answer is kept for each collider.
    // The first two replace the game's methods, so any other mod's patch on them, or on the reads the first leaves out, turns
    // them off and fails mod-compatibility.
    internal static class CharacterStepTrims
    {
        private static readonly AccessTools.FieldRef<Character, bool> sliding = AccessTools.FieldRefAccess<Character, bool>("m_sliding");
        private static readonly AccessTools.FieldRef<Character, GameObject[]> slideInstances = AccessTools.FieldRefAccess<Character, GameObject[]>("m_slideEffects_instances");
        private static readonly AccessTools.FieldRef<Character, GameObject[]> waterInstances = AccessTools.FieldRefAccess<Character, GameObject[]>("m_waterEffects_instances");
        private static readonly AccessTools.FieldRef<Character, GameObject[]> flyingInstances = AccessTools.FieldRefAccess<Character, GameObject[]>("m_flyingEffects_instances");
        private static readonly AccessTools.FieldRef<Character, CapsuleCollider> collider = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");
        private static readonly AccessTools.FieldRef<Character, float> slippage = AccessTools.FieldRefAccess<Character, float>("m_slippage");
        private static readonly AccessTools.FieldRef<Character, Vector3> moveDirection = AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly Func<Character, bool> inTar = AccessTools.MethodDelegate<Func<Character, bool>>(AccessTools.Method(typeof(Character), "InTar"));
        private static MethodInfo friction;
        // UpdateContinousEffects and the reads it makes that the shortcut leaves out.
        private static MethodBase[] effectReads;
        // 0 until the first call reads the methods' patches, then 1 when the shortcut runs, -1 when the game's own runs.
        private static int effectsReady, frictionReady;

        private sealed class Identity : IEqualityComparer<Collider>
        {
            public bool Equals(Collider first, Collider second) => ReferenceEquals(first, second);
            public int GetHashCode(Collider item) => RuntimeHelpers.GetHashCode(item);
        }

        private static readonly Dictionary<Collider, Heightmap> grounds = new Dictionary<Collider, Heightmap>(new Identity());

        internal static void Initialize(PerformancePatches harmony)
        {
            friction = AccessTools.Method(typeof(Character), "UpdateBodyFriction");
            effectReads = new[] { "UpdateContinousEffects", "GetLiquidLevel", "InLiquid", "InTar", "IsFlying", "GetZDOID" }.Select(name => (MethodBase)AccessTools.Method(typeof(Character), name, Type.EmptyTypes)).ToArray();
            if (effectReads.Any(read => read == null)) throw new MissingMethodException("A character's effect reads changed.");
            harmony.Patch(effectReads[0], prefix: new HarmonyMethod(typeof(CharacterStepTrims), nameof(ContinuousEffects)));
            harmony.Patch(friction, prefix: new HarmonyMethod(typeof(CharacterStepTrims), nameof(BodyFriction)));
            harmony.Patch(AccessTools.Method(typeof(Character), "UpdateGroundContact"), transpiler: new HarmonyMethod(typeof(CharacterStepTrims), nameof(GroundTranspiler)));
        }

        private static bool Ready(ref int ready, params MethodBase[] methods)
        {
            if (ready != 0) return ready > 0;
            string problem = methods.Select(method => PatchSets.Unknown(method)).FirstOrDefault(found => found != null);
            ready = problem == null ? 1 : -1;
            if (problem != null) Plugin.Fallback("CharacterStepTrims unavailable: " + problem);
            return ready > 0;
        }

        // Character.UpdateContinousEffects, with each effect's arguments read only when the character has that effect;
        // SetupContinuousEffect returns at once for an effect list without effects.
        private static bool ContinuousEffects(Character __instance)
        {
            if (!Ready(ref effectsReady, effectReads)) return true;
            var character = __instance;
            if (character.m_slideEffects.HasEffects())
                Character.SetupContinuousEffect(character.transform, character.transform.position, sliding(character), character.m_slideEffects, ref slideInstances(character), character.GetZDOID());
            if (character.m_waterEffects.HasEffects() || character.m_tarEffects.HasEffects())
            {
                Vector3 position = character.transform.position;
                position.y = character.GetLiquidLevel() + 0.05f;
                var list = inTar(character) && character.m_tarEffects.HasEffects() ? character.m_tarEffects : character.m_waterEffects;
                Character.SetupContinuousEffect(character.transform, position, character.InLiquid(), list, ref waterInstances(character), character.GetZDOID());
            }
            if (character.m_flyingContinuousEffect.HasEffects())
                Character.SetupContinuousEffect(character.transform, character.transform.position, character.IsFlying(), character.m_flyingContinuousEffect, ref flyingInstances(character), character.GetZDOID());
            return false;
        }

        // Character.UpdateBodyFriction, writing each value only where the material holds another.
        private static bool BodyFriction(Character __instance)
        {
            if (!Ready(ref frictionReady, friction)) return true;
            var character = __instance;
            var material = collider(character).material;
            float value;
            var combine = PhysicsMaterialCombine.Multiply;
            if (character.IsDead())
            {
                value = 1f;
                combine = PhysicsMaterialCombine.Maximum;
            }
            else if (character.IsSwimming()) value = 0.2f;
            else if (!character.IsOnGround()) value = 0f;
            else if (character.IsFlying()) value = 0f;
            else if (moveDirection(character).magnitude < 0.1f)
            {
                value = 0.8f * (1f - slippage(character));
                combine = PhysicsMaterialCombine.Maximum;
            }
            else value = 0.4f * (1f - slippage(character));
            if (material.frictionCombine != combine) material.frictionCombine = combine;
            if (material.staticFriction != value) material.staticFriction = value;
            if (material.dynamicFriction != value) material.dynamicFriction = value;
            return false;
        }

        // m_lastGroundCollider.gameObject.GetComponent<Heightmap>() becomes Ground(m_lastGroundCollider).
        private static IEnumerable<CodeInstruction> GroundTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var gameObject = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
            var heightmap = AccessTools.Method(typeof(GameObject), nameof(GameObject.GetComponent), Type.EmptyTypes).MakeGenericMethod(typeof(Heightmap));
            var code = instructions.ToList();
            int call = code.FindIndex(instruction => instruction.Calls(heightmap));
            if (call < 1 || !code[call - 1].Calls(gameObject) || code[call].labels.Count > 0 || code.FindLastIndex(instruction => instruction.Calls(heightmap)) != call)
                throw new InvalidOperationException("The character's ground contact changed.");
            code[call - 1].opcode = OpCodes.Call;
            code[call - 1].operand = AccessTools.Method(typeof(CharacterStepTrims), nameof(Ground));
            code[call].opcode = OpCodes.Nop;
            code[call].operand = null;
            return code;
        }

        // Destroyed colliders are never seen again; their entries go when the table fills.
        internal static Heightmap Ground(Collider ground)
        {
            if (grounds.TryGetValue(ground, out var heightmap)) return heightmap;
            if (grounds.Count >= 4096) grounds.Clear();
            heightmap = ground.gameObject.GetComponent<Heightmap>();
            grounds[ground] = heightmap;
            return heightmap;
        }
    }
}
