using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // The game events that register, change and remove members, and that show their native renderers when their merged state no longer matches.
    internal static partial class SpatialBuildings
    {
        private static void Created(WearNTear __instance)
        {
            if (!Running) return;
            // A placement ghost has no network record and stays native. The piece's wake took its view before this runs.
            var view = wearViews(__instance);
            if ((object)view == null) view = __instance.GetComponent<ZNetView>();
            if (view && view.GetZDO() != null) pending.Enqueue((__instance, Time.frameCount));
        }

        private static void Destroyed(WearNTear __instance)
        {
            if (!Running) return;
            if (nativeSwitched.Count > 0 && nativeSwitched.TryGetValue(__instance, out var ids))
            {
                foreach (int id in ids) switchedMembers.Remove(id);
                nativeSwitched.Remove(__instance);
            }
            if (!pieces.TryGetValue(__instance, out var member)) return;
            using (new Timed(RemovalHooks))
                try
                {
                    pieces.Remove(__instance);
                    Remove(member);
                }
                catch (Exception error) { Fail(error); }
        }

        // Cleared before the next render, so destroyed and removed members never linger in a merged mesh. A zone's objects are
        // destroyed together, so the rest of their removal waits for the frame's budget.
        private static void Remove(Member member)
        {
            foreach (int id in member.SwitchedIds) switchedMembers.Remove(id);
            ClearSlots(member);
            member.Owned = member.Away = false;
            near.Remove(member);
            Unfinish(member);
            member.Removed = true;
            member.Version++;
            removing.Enqueue(member);
        }

        // Takes destroyed members out of their cells and merged meshes within the frame's budget; a mesh built before then
        // leaves their slots empty.
        private static void Forget()
        {
            while (removing.Count > 0 && Open())
            {
                var member = removing.Dequeue();
                Unassign(member);
                member.Cell.Members.Remove(member);
                Changed(member.Cell);
                member.Cell.ChangedBy = member;
                foreach (int id in member.RendererIds) rendererMembers.Remove(id);
            }
        }

        // Every network object is considered once it has existed for a while; building pieces register on their own.
        private static void ViewCreated(ZNetView __instance)
        {
            if (Running && __instance.GetZDO() != null) pendingNatural.Enqueue((__instance, Time.realtimeSinceStartup));
        }

        private static void ViewDestroyed(ZNetView __instance)
        {
            if (!Running || !natural.TryGetValue(__instance, out var member)) return;
            using (new Timed(RemovalHooks))
                try
                {
                    natural.Remove(__instance);
                    Remove(member);
                }
                catch (Exception error) { Fail(error); }
        }

        // A zone leaving the active area destroys its objects together at the end of the frame. Their members are removed as
        // the scene unloads them, before the frame's budget is set, so that budget makes room for their removal.
        private static void Unloaded(ZNetScene __instance)
        {
            var views = unloadedViews(__instance);
            if (!Running || views.Count == 0) return;
            using (new Timed(RemovalHooks))
                try
                {
                    foreach (var view in views)
                    {
                        if (natural.TryGetValue(view, out var member)) natural.Remove(view);
                        else if (pieces.Count == 0 || !view.TryGetComponent<WearNTear>(out var wear) || !pieces.TryGetValue(wear, out member)) continue;
                        else pieces.Remove(wear);
                        Remove(member);
                    }
                }
                catch (Exception error) { Fail(error); }
        }

        private static Member NaturalOf(Component component) =>
            natural.Count > 0 && component.TryGetComponent<ZNetView>(out var view) && natural.TryGetValue(view, out var member) ? member : null;

        // Picking, respawning and enabling switch a pickable's picked object; a mined area switches an ore rock's renderers.
        private static void PickableChanged(Pickable __instance) => VisibilityChanged(__instance, "picked");

        private static void RockChanged(MineRock __instance) => VisibilityChanged(__instance, "mined");

        // A behaviour switched objects of its own piece or natural object: its own objects first, which is a few reads and is
        // most often all, then the renderers only when those changed. An item stand has no switched objects and compares the
        // renderers directly.
        private static void SwitchedVisualChanged(Component __instance)
        {
            if (!Running || !__instance) return;
            var member = SwitchedMember(__instance);
            if (member == null) return;
            ulong switched = 0;
            if (member.Switches.Length > 0)
            {
                switched = SwitchState(member);
                if (switched == member.Switched) return;
            }
            using (new Timed(VisualHooks))
                try
                {
                    if (member.Switches.Length > 0) member.Switched = switched;
                    ulong signature = Signature(member);
                    if (signature == member.Signature) return;
                    member.Signature = signature;
                    HandBack(member, "switched visual");
                }
                catch (Exception error) { Fail(error); }
        }

        // The member a switched behaviour belongs to, or none. The one mapped with its piece is what the search up its hierarchy
        // finds while the piece's object is active; that search skips inactive objects, so otherwise it runs as before.
        private static Member SwitchedMember(Component behaviour) =>
            switchedMembers.TryGetValue(behaviour.GetInstanceID(), out var entry) && entry.root.activeInHierarchy ? entry.member : SearchedMember(behaviour);

        private static Member SearchedMember(Component behaviour)
        {
            var wear = behaviour.GetComponentInParent<WearNTear>();
            return wear && pieces.TryGetValue(wear, out var piece) ? piece : NaturalOf(behaviour);
        }

        // A crafting station's update every frame changes nothing else it draws but its in-use object, only in
        // m_inUseObject.SetActive(flag), so the station is checked right after that call instead of after every update; no mod
        // in the pack switches a station's objects.
        private static IEnumerable<CodeInstruction> InUseSwitches(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var inUse = AccessTools.Field(typeof(CraftingStation), "m_inUseObject");
            var setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
            var calls = Enumerable.Range(0, code.Count).Where(index => code[index].Calls(setActive)).ToList();
            if (calls.Count != 1 || code.FindLastIndex(calls[0], instruction => instruction.LoadsField(inUse)) < 0)
            {
                Plugin.Fallback("CraftingStation.CustomUpdate no longer switches only its in-use object, so stations are checked after every update.");
                return code;
            }
            code.InsertRange(calls[0] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpatialBuildings), nameof(SwitchedVisualChanged))) });
            inUseFollowed = true;
            return code;
        }

        private static void VisibilityChanged(Component component, string reason)
        {
            if (!Running) return;
            var member = NaturalOf(component);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    ulong signature = Signature(member);
                    if (signature == member.Signature) return;
                    member.Signature = signature;
                    HandBack(member, reason);
                }
                catch (Exception error) { Fail(error); }
        }

        // A hit tree's trunk shakes for a second, and a grown tree swaps its trunk for an animated copy: the tree shows its
        // native renderers meanwhile and draws merged again once its trunk is back where its slots hold it.
        private static void TreeShaking(TreeBase __instance) => Busy(__instance, ShakeSeconds, "shaking");

        private static void TreeGrowing(TreeBase __instance) => Busy(__instance, GrowthSeconds, "growing");

        private static void Busy(Component component, float seconds, string reason)
        {
            if (!Running) return;
            var member = NaturalOf(component);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    member.BusyUntil = Mathf.Max(member.BusyUntil, Time.time + seconds);
                    if (member.Owned && !member.Away) Leave(member, reason);
                }
                catch (Exception error) { Fail(error); }
        }

        private static bool Busy(Member member) => Time.time < member.BusyUntil || member.Physics && member.Physics.IsFalling;

        // Ground physics moves a natural object: falling over a fraction of a second, pushing up at once. It shows its
        // native renderers from that frame and merges again once it rests.
        private static void Falling(StaticPhysics __instance) => Moved(__instance, "falling");

        private static void PushStarting(StaticPhysics __instance, out Vector3 __state) => __state = __instance.transform.position;

        private static void Pushed(StaticPhysics __instance, Vector3 __state)
        {
            if (Running && __instance.transform.position != __state) Moved(__instance, "pushed up");
        }

        private static void Moved(Component component, string reason)
        {
            if (!Running) return;
            var member = NaturalOf(component);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try { HandBack(member, reason); }
                catch (Exception error) { Fail(error); }
        }

        // The health visual ends by updating the snow visual; the member is checked once, after both.
        private static void HealthVisualStarting() => healthVisuals++;

        private static void HealthVisualChanged(WearNTear __instance)
        {
            healthVisuals--;
            VisualChanged(__instance);
        }

        private static void SnowVisualChanged(WearNTear __instance)
        {
            if (healthVisuals == 0) VisualChanged(__instance);
        }

        private static void VisualChanged(WearNTear __instance)
        {
            if (!Running || !pieces.TryGetValue(__instance, out var member)) return;
            // Renderers change visibility here only when one of the wear, wetness and snow objects switches.
            ulong switched = SwitchState(member);
            if (switched == member.Switched) return;
            using (new Timed(VisualHooks))
                try
                {
                    member.Switched = switched;
                    ulong signature = Signature(member);
                    if (signature == member.Signature) return;
                    member.Signature = signature;
                    HandBack(member, "visual state");
                }
                catch (Exception error) { Fail(error); }
        }

        private static void Scaled(ZNetView __instance)
        {
            if (!Running) return;
            Member member = null;
            if (__instance.TryGetComponent<WearNTear>(out var wear)) pieces.TryGetValue(wear, out member);
            else natural.TryGetValue(__instance, out member);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    foreach (var detail in member.Details) Place(detail);
                    HandBack(member, "scale");
                }
                catch (Exception error) { Fail(error); }
        }

        // The material manager has just given the member's native renderers its property block; merged slots follow in the
        // same frame, and a property the merged shader cannot express hands the member back.
        private static void PropertiesApplied(object __instance)
        {
            if (!Running) return;
            var renderers = containerRenderers(__instance);
            if (renderers == null || renderers.Count == 0 || !renderers[0] || !rendererMembers.TryGetValue(renderers[0].GetInstanceID(), out var member)) return;
            using (new Timed(PropertyHooks))
                try
                {
                    var container = containerProperties(__instance);
                    if (Unchanged(container, member.Properties)) return;
                    member.Properties = Properties(container);
                    if (!Expressible(member)) { HandBack(member, "material property"); return; }
                    // A member the merged meshes could not express before is planned again; one waiting for its meshes
                    // takes the values when it becomes owned.
                    if (!member.Owned) { if (member.Merged.Count == 0) MarkDirty(member, true); return; }
                    foreach (var slot in member.Slots)
                    {
                        if (slot.batch.Appearances == null) continue;
                        slot.batch.AppearanceData[slot.slot] = AppearanceOf(member, slot.part);
                        slot.batch.AppearanceFrom = Math.Min(slot.batch.AppearanceFrom, slot.slot);
                        slot.batch.AppearanceTo = Math.Max(slot.batch.AppearanceTo, slot.slot);
                        Flush(slot.batch);
                    }
                    flushPending = true;
                }
                catch (Exception error) { Fail(error); }
        }

        private static void WorldClosed()
        {
            if (!Running) return;
            try { Clear(); }
            catch (Exception error) { Fail(error); }
        }

        // Destruction breaks only the renderers the cameras drew into fragments, and a merged member's native renderers are
        // never drawn, so the visibility test asks whether its merged mesh draws them in the main camera's view instead.
        private static IEnumerable<CodeInstruction> FragmentsFromMerged(IEnumerable<CodeInstruction> instructions)
        {
            var visible = AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.isVisible));
            var code = instructions.ToList();
            var tests = code.Where(instruction => instruction.Calls(visible)).ToList();
            if (tests.Count != 1) throw new InvalidOperationException("Destructible.CreateFragments no longer tests visibility as expected; rendering stays native.");
            tests[0].opcode = OpCodes.Call;
            tests[0].operand = AccessTools.Method(typeof(SpatialBuildings), nameof(FragmentVisible));
            return code;
        }

        private static bool FragmentVisible(Renderer renderer)
        {
            if (renderer.isVisible) return true;
            if (!Running || Native || !renderer.enabled || !rendererMembers.TryGetValue(renderer.GetInstanceID(), out var member) || !member.Owned || member.Away) return false;
            int index = Array.IndexOf(member.RendererIds, renderer.GetInstanceID());
            bool drawn = false;
            foreach (var (_, _, part) in member.Slots) if (part.Renderer == index && Shown(member, part)) { drawn = true; break; }
            var camera = Utils.GetMainCamera();
            if (!drawn || !camera) return false;
            GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            return GeometryUtility.TestPlanesAABB(frustum, renderer.bounds);
        }
    }
}
