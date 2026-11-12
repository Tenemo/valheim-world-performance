using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Nobody simulates an unowned log or dropped item, but its dynamic body still contacts nearby bodies every step.
    // Keep its mesh, collider and native transform synchronization; restore dynamics before either native sync path
    // observes an owner. Only initially dynamic floating props participate, never fish, creatures or boats.
    // A body whose pause a check has just confirmed leaves the game's lists of transform syncs and floating updates, which do
    // nothing for a body nobody owns. Right after the game's transform syncs in each physics step it gets the same check, and
    // right after the late update's owner syncs the same test for an owner; when either would act, the body returns to the
    // lists and its sync runs at once.
    internal static class RemoteFloatingBodies
    {
        // A registered body's last applied revision, the record's rotation angles with their converted rotation, its components
        // and whether it is out of the game's lists.
        internal sealed class Body
        {
            internal uint? revision;
            internal Vector3 angles;
            internal Quaternion rotation;
            internal bool converted;
            internal ZSyncTransform sync;
            internal ZNetView view;
            internal Rigidbody rigidbody;
            internal Floating floating;
            internal bool parked, parking;
        }

        private static readonly Dictionary<ZSyncTransform, Body> Bodies = new Dictionary<ZSyncTransform, Body>();
        private static readonly Dictionary<Floating, Body> Floaters = new Dictionary<Floating, Body>();
        private static readonly List<Body> Parked = new List<Body>();
        private static readonly List<Body> ToPark = new List<Body>();
        private static readonly HashSet<IMonoUpdater> leaving = new HashSet<IMonoUpdater>();
        private static readonly Predicate<IMonoUpdater> leaves = leaving.Contains;
        private static readonly AccessTools.FieldRef<ZDO, Vector3> recordAngles = AccessTools.FieldRefAccess<ZDO, Vector3>("m_rotation");
        private static readonly AccessTools.FieldRef<ZSyncTransform, int> lastUpdateFrames = AccessTools.FieldRefAccess<ZSyncTransform, int>("m_lastUpdateFrame");
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> wasOwners = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_wasOwner");
        // Off reads a body before looking it up and converts the record's rotation each time, as before; the isolated client
        // switches it for paired measurements.
        internal static bool Lean = true;
        // Off keeps every body in the game's lists, as before; the isolated client switches it for paired measurements.
        internal static bool Parking = true;
        // Whether both checks are in place after the game's syncs; without them no body leaves the lists.
        private static bool fixedHooked, lateHooked;
        internal static long Parks, Returns;

        [HarmonyPatch(typeof(ZSyncTransform), "OnEnable")]
        private static class Register
        {
            private static void Postfix(ZSyncTransform __instance, Rigidbody ___m_body, ZNetView ___m_nview, bool ___m_isKinematicBody)
            {
                if (ZNet.instance == null || ZNet.instance.IsServer() || !___m_body || ___m_isKinematicBody) return;
                var floating = __instance.GetComponent<Floating>();
                if (floating && !__instance.GetComponent<Fish>() && !__instance.GetComponent<Character>()
                    && !__instance.GetComponent<Ship>() && !__instance.GetComponent<Leviathan>() && !__instance.GetComponent<Piece>())
                {
                    var state = new Body { sync = __instance, view = ___m_nview, rigidbody = ___m_body, floating = floating };
                    Bodies[__instance] = state;
                    Floaters[floating] = state;
                }
            }
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OnDisable")]
        private static class Unregister
        {
            private static void Postfix(ZSyncTransform __instance, Rigidbody ___m_body, ref bool ___m_isKinematicBody)
            {
                if (!Bodies.TryGetValue(__instance, out var state)) return;
                Bodies.Remove(__instance);
                if ((object)state.floating != null && Floaters.TryGetValue(state.floating, out var floater) && floater == state) Floaters.Remove(state.floating);
                // The game's own removal found nothing in its list; the body leaves this one instead.
                if (state.parked)
                {
                    state.parked = false;
                    Parked.Remove(state);
                }
                state.parking = false;
                if (___m_body) ___m_body.isKinematic = false;
                ___m_isKinematicBody = false;
            }
        }

        // A floating update switched on while its body is parked stays out of the game's list.
        [HarmonyPatch(typeof(Floating), "OnEnable")]
        private static class FloatingEnabled
        {
            private static void Postfix(Floating __instance)
            {
                if (Floaters.TryGetValue(__instance, out var state) && state.parked) Floating.Instances.Remove(__instance);
            }
        }

        private static bool Pause(ZSyncTransform sync, Rigidbody body, ZNetView view, ref bool kinematic, out Body state)
        {
            state = null;
            // Most synchronized transforms are not registered, and the table answers that without reading their bodies.
            if ((Lean ? !Bodies.TryGetValue(sync, out state) || !body : !body || !Bodies.TryGetValue(sync, out state)) || !view.IsValid()) return false;
            bool paused = !view.GetZDO().HasOwner();
            if (body.isKinematic != paused)
            {
                body.isKinematic = paused;
                if (!paused) body.WakeUp();
            }
            // Native ClientSync must use its kinematic branch too, rather than write velocities on a kinematic body.
            kinematic = paused;
            if (!paused) state.revision = null;
            return paused;
        }

        // The record's rotation, converted again only when its angles change: the same angles give the same rotation.
        internal static Quaternion RecordRotation(Body state, ZDO data)
        {
            Vector3 angles = recordAngles(data);
            if (!state.converted || !Same(angles.x, state.angles.x) || !Same(angles.y, state.angles.y) || !Same(angles.z, state.angles.z))
            {
                state.angles = angles;
                state.rotation = Quaternion.Euler(angles);
                state.converted = true;
            }
            return state.rotation;
        }

        private static unsafe bool Same(float a, float b) => *(int*)&a == *(int*)&b;

        // Whether a paused body's record and body still agree, so native sync has nothing to apply.
        private static bool InSync(Body state, Rigidbody body, ZDO data) =>
            state.revision == data.DataRevision && (!state.sync.m_syncPosition || (body.position - data.GetPosition()).sqrMagnitude < .0000001f)
            && (!state.sync.m_syncRotation || Mathf.Abs(Quaternion.Dot(body.rotation, Lean ? RecordRotation(state, data) : data.GetRotation())) > .999999f);

        [HarmonyPatch(typeof(ZSyncTransform), "ClientSync")]
        private static class ClientSynchronization
        {
            private static bool Prefix(ZSyncTransform __instance, Rigidbody ___m_body, ZNetView ___m_nview, ref bool ___m_isKinematicBody, int ___m_lastUpdateFrame, bool ___m_wasOwner)
            {
                if (!Pause(__instance, ___m_body, ___m_nview, ref ___m_isKinematicBody, out var state)) return true;
                // Native sync runs at most once per rendered frame. Do not mark a newer revision as applied when it
                // would return before applying it in a second physics step of that frame.
                int frame = Time.frameCount;
                if (___m_lastUpdateFrame == frame) return true;
                var data = ___m_nview.GetZDO();
                // MovePosition would wake an unchanged kinematic body each step. Run native sync for a new revision and
                // until its position converges, retaining scale updates and any externally moved body's correction.
                if (InSync(state, ___m_body, data))
                {
                    if (Parking && fixedHooked && lateHooked && !state.parked && !state.parking && !___m_wasOwner)
                    {
                        state.parking = true;
                        ToPark.Add(state);
                    }
                    return false;
                }
                state.revision = data.DataRevision;
                return true;
            }
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        private static class OwnerSynchronization
        {
            private static void Prefix(ZSyncTransform __instance, Rigidbody ___m_body, ZNetView ___m_nview, ref bool ___m_isKinematicBody) =>
                Pause(__instance, ___m_body, ___m_nview, ref ___m_isKinematicBody, out _);
        }

        [HarmonyPatch(typeof(MonoUpdaters), "FixedUpdate")]
        private static class FixedStep
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                AfterSyncs(instructions, nameof(MonoUpdatersExtra.CustomFixedUpdate), nameof(AfterClientSync), ref fixedHooked);
        }

        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        private static class LateStep
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                AfterSyncs(instructions, nameof(MonoUpdatersExtra.CustomLateUpdate), nameof(AfterOwnerSync), ref lateHooked);
        }

        // Calls the check with the step's time right after the game's pass over its synchronized transforms.
        private static IEnumerable<CodeInstruction> AfterSyncs(IEnumerable<CodeInstruction> instructions, string pass, string check, ref bool hooked)
        {
            var code = new List<CodeInstruction>(instructions);
            int list = code.FindIndex(instruction => instruction.Calls(AccessTools.PropertyGetter(typeof(ZSyncTransform), nameof(ZSyncTransform.Instances))));
            int call = list < 0 ? -1 : code.FindIndex(list, instruction => instruction.Calls(AccessTools.Method(typeof(MonoUpdatersExtra), pass)));
            if (call < 1 || !code[call - 1].IsLdloc())
            {
                Plugin.Fallback("MonoUpdaters no longer passes over its synchronized transforms as expected, so paused bodies stay in the game's lists.");
                return code;
            }
            code.InsertRange(call + 1, new[]
            {
                new CodeInstruction(code[call - 1].opcode, code[call - 1].operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RemoteFloatingBodies), check))
            });
            hooked = true;
            return code;
        }

        // The prefix's check for a parked body, without its effects: true when native sync would do nothing this step.
        private static bool Keeps(Body state, int frame)
        {
            var view = state.view;
            // Native fixed update skips an invalid view altogether.
            if (!view.IsValid()) return true;
            var body = state.rigidbody;
            if (!body) return false;
            var data = view.GetZDO();
            if (data.HasOwner() || !body.isKinematic) return false;
            return lastUpdateFrames(state.sync) == frame || InSync(state, body, data);
        }

        private static void AfterClientSync(float deltaTime)
        {
            if (Parked.Count > 0)
            {
                int frame = Time.frameCount;
                for (int index = Parked.Count - 1; index >= 0; index--)
                {
                    var state = Parked[index];
                    if (Keeps(state, frame))
                    {
                        if (!Parking) Return(state, index);
                        continue;
                    }
                    Return(state, index);
                    state.sync.CustomFixedUpdate(deltaTime);
                }
            }
            if (ToPark.Count > 0) ParkWaiting();
        }

        // Native owner sync does nothing for a paused body no owner has claimed since this game last owned it.
        private static void AfterOwnerSync(float deltaTime)
        {
            for (int index = Parked.Count - 1; index >= 0; index--)
            {
                var state = Parked[index];
                var view = state.view;
                if (!view.IsValid()) continue;
                var body = state.rigidbody;
                if (body && body.isKinematic && !view.GetZDO().HasOwner() && !wasOwners(state.sync))
                {
                    if (!Parking) Return(state, index);
                    continue;
                }
                Return(state, index);
                state.sync.CustomLateUpdate(deltaTime);
            }
        }

        private static void ParkWaiting()
        {
            leaving.Clear();
            foreach (var state in ToPark)
            {
                if (!state.parking) continue;
                state.parking = false;
                if (state.parked || !Bodies.TryGetValue(state.sync, out var current) || current != state) continue;
                state.parked = true;
                Parked.Add(state);
                Parks++;
                leaving.Add(state.sync);
                if ((object)state.floating != null) leaving.Add(state.floating);
            }
            ToPark.Clear();
            if (leaving.Count == 0) return;
            ZSyncTransform.Instances.RemoveAll(leaves);
            Floating.Instances.RemoveAll(leaves);
            leaving.Clear();
        }

        private static void Return(Body state, int index)
        {
            state.parked = false;
            int last = Parked.Count - 1;
            Parked[index] = Parked[last];
            Parked.RemoveAt(last);
            Returns++;
            ZSyncTransform.Instances.Add(state.sync);
            if (state.floating && state.floating.isActiveAndEnabled) Floating.Instances.Add(state.floating);
        }
    }
}
