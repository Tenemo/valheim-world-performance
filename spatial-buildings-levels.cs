using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WorldPerformance
{
    // Detail-level checks: each member's level from the camera's distance, and when it is checked again.
    internal static partial class SpatialBuildings
    {
        // A detail group's world reference point and size, which stay put while the member does.
        private static void Place(Detail detail)
        {
            var group = detail.Group;
            if (!group) return;
            detail.Reference = group.transform.TransformPoint(group.localReferencePoint);
            detail.Size = group.size * MaximumScale(group.transform.lossyScale);
        }

        // The level a native group shows for the camera, as the engine selects it, and how far the camera can move before it changes.
        private static int Level(Detail detail, Vector3 camera, out float margin)
        {
            float distance = Vector3.Distance(camera, detail.Reference);
            float scale = detail.Size * detailScale;
            float relative = scale / Mathf.Max(distance, .001f);
            int level = -1;
            for (int index = 0; index < detail.Thresholds.Length; index++)
                if (relative >= detail.Thresholds[index]) { level = index; break; }
            float near = level <= 0 ? 0f : scale / detail.Thresholds[level - 1];
            float far = level < 0 ? float.PositiveInfinity : scale / detail.Thresholds[level];
            if (level < 0) near = detail.Thresholds.Length == 0 ? 0f : scale / detail.Thresholds[detail.Thresholds.Length - 1];
            margin = Mathf.Min(distance - near, far - distance);
            measuredLevel = level;
            measuredScale = scale;
            measuredNear = near;
            measuredFar = far;
            return level < 0 ? -1 : Math.Max(level, detailMaximum);
        }

        // The last measured group's level before the maximum level applies, its scale and the distances of its changes.
        private static int measuredLevel;
        private static float measuredScale, measuredNear, measuredFar;

        // Keeps a member's only group's level as Level just measured it, for the due checks that find it unchanged.
        private static void KeepBand(Member member, Detail detail)
        {
            member.Banded = detail.Descending && member.Active.Count == 1;
            if (!member.Banded) return;
            var thresholds = detail.Thresholds;
            int level = measuredLevel;
            member.BandFades = detail.Fades;
            member.BandReference = detail.Reference;
            member.BandScale = measuredScale;
            member.BandLower = level >= 0 ? thresholds[level] : float.NegativeInfinity;
            member.BandUpper = level > 0 ? thresholds[level - 1] : level == 0 || thresholds.Length == 0 ? float.PositiveInfinity : thresholds[thresholds.Length - 1];
            member.BandNear = measuredNear;
            member.BandFar = measuredFar;
        }

        // A due check of an owned member drawing merged whose only group keeps its level, done as CheckLevels does it without
        // reading the member's groups: the same screen share, level test, margin and next check.
        private static bool CheckBand(Member member, Vector3 camera)
        {
            if (!member.Banded || !member.Owned || member.Away) return false;
            float distance = Vector3.Distance(camera, member.BandReference);
            float relative = member.BandScale / Mathf.Max(distance, .001f);
            if (!(relative >= member.BandLower && relative < member.BandUpper)) return false;
            float margin = Mathf.Min(distance - member.BandNear, member.BandFar - distance);
            if (member.BandFades && !(margin >= DetailMargin)) return false;
            evaluations++;
            CheckAfter(member, member.BandFades ? margin - DetailMargin : margin - SwitchMargin);
            return true;
        }

        // The level the orthographic sky depth camera selects for a group, with the maximum level read by the last detail-level
        // checks, whose change fills the sky slots again.
        private static int SkyLevel(Detail detail)
        {
            float relative = detail.Size * skyScale;
            for (int index = 0; index < detail.Thresholds.Length; index++)
                if (relative >= detail.Thresholds[index]) return Math.Max(index, detailMaximum >= 0 ? detailMaximum : QualitySettings.maximumLODLevel);
            return -1;
        }

        // Whether a merged part draws at the level the sky depth camera selects for its group.
        private static bool SkyShown(Member member, Part part)
        {
            if (part.Detail < 0) return true;
            int level = SkyLevel(member.Details[part.Detail]);
            return level >= 0 && (part.Levels & 1 << level) != 0;
        }

        // Whether the sky depth camera draws one of the member's merged parts, at another level than the main camera for one of
        // its groups.
        private static bool OtherSkyLevels(Member member)
        {
            int mask = skyCamera.cullingMask;
            bool drawn = false;
            foreach (var part in member.Merged) if ((mask & 1 << part.Key.Layer) != 0) { drawn = true; break; }
            if (drawn) foreach (var detail in member.Active) if (detail.Level != SkyLevel(detail)) return true;
            return false;
        }

        // Evaluates the owned members whose detail-level checks the camera's travel has made due, or every owned member when the
        // detail settings change.
        private static void CheckDetails(Camera camera, Vector3 position)
        {
            float bias = QualitySettings.lodBias, view = camera.fieldOfView;
            int maximum = QualitySettings.maximumLODLevel;
            bool settings = bias != detailBias || view != detailView || maximum != detailMaximum;
            if (settings)
            {
                // The sky depth camera's levels follow the same maximum level.
                if (maximum != detailMaximum) ForgetSky();
                detailBias = bias;
                detailView = view;
                detailMaximum = maximum;
                detailScale = bias * .5f / Mathf.Tan(view * Mathf.Deg2Rad * .5f);
            }
            if (cameraKnown) travelled += Vector3.Distance(position, lastCamera);
            lastCamera = position;
            cameraKnown = true;
            if (settings)
            {
                checks.Clear();
                parked.Clear();
                foreach (var member in Members()) if (member.Owned) CheckLevels(member, position);
            }
            if (!ParkedChecks && parked.Count > 0)
            {
                // Parked checks go back to waiting for the camera's travel.
                parked.Keep(entry => { float moved = Vector3.Distance(position, entry.From); entry.Due = travelled + Math.Max(entry.Distance - moved, 0f); checks.Push(entry); return -1; });
                anchored = false;
            }
            float fromAnchor = 0f;
            if (ParkedChecks)
            {
                if (!anchored || parked.Count == 0) { anchor = position; anchored = true; }
                fromAnchor = Vector3.Distance(position, anchor);
                if (fromAnchor > AnchorReach)
                {
                    anchor = position;
                    fromAnchor = 0f;
                    parked.Keep(Reanchored);
                    foreach (var member in pendingNow) CheckLevels(member, position);
                    pendingNow.Clear();
                }
            }
            while (checks.Count > 0 && checks.TopDue <= travelled)
            {
                var check = checks.Top;
                bool valid = check.Stamp == check.Member.CheckStamp && !check.Member.Removed;
                if (valid && ReplaceDueChecks) checks.Replacing = true;
                else checks.Pop();
                if (valid && !(ParkedChecks && Defer(check, position, fromAnchor))) Due(check.Member, position);
                if (!checks.Replacing) continue;
                checks.Replacing = false;
                checks.Pop();
            }
            while (parked.Count > 0 && parked.TopDue <= fromAnchor)
            {
                var check = parked.Pop();
                if (check.Stamp == check.Member.CheckStamp && !check.Member.Removed) Due(check.Member, position);
            }
        }

        // The isolated client can turn these off to compare: a due check's replacement taking its place in the heap, and a
        // member's kept level settling its due checks.
        internal static bool ReplaceDueChecks = true, BandedChecks = true;

        // Makes a member's due check.
        private static void Due(Member member, Vector3 position)
        {
            if (!(BandedChecks && CheckBand(member, position))) CheckLevels(member, position);
        }

        // A check whose member the camera cannot yet have brought to a change waits, parked while the anchor bounds the
        // camera's distance from where it was made.
        private static bool Defer(Check check, Vector3 position, float fromAnchor)
        {
            if (Vector3.Distance(position, check.From) >= check.Distance) return false;
            float key = check.Distance - Vector3.Distance(anchor, check.From);
            if (!(key > fromAnchor)) return false;
            check.Due = key;
            parked.Push(check);
            return true;
        }

        // A parked check's due distance from the new anchor, where the camera is; one the camera may already have brought to
        // a change is made once the parked checks are ordered again.
        private static double Reanchored(Check check)
        {
            if (check.Stamp != check.Member.CheckStamp || check.Member.Removed) return -1;
            float key = check.Distance - Vector3.Distance(anchor, check.From);
            if (key > 0f) return key;
            pendingNow.Add(check.Member);
            return -1;
        }

        private static readonly List<Member> pendingNow = new List<Member>();

        // Void detail-level checks are dropped within the frame's budget once they outnumber the members.
        private static void DropVoidChecks()
        {
            int entries = checks.Count + parked.Count;
            if (entries <= (pieces.Count + natural.Count) * 2 + 64 || !Admit(cleanupCost.Expected(entries))) return;
            var step = Step.Start();
            checks.Keep(check => check.Stamp != check.Member.CheckStamp || check.Member.Removed ? -1 : check.Due);
            parked.Keep(check => check.Stamp != check.Member.CheckStamp || check.Member.Removed ? -1 : check.Due);
            step.Done(cleanupCost, entries);
        }

        // A merged member is checked again before the camera can reach its next detail-level change. One whose groups switch at
        // once switches its slots there; one with a crossfading group shows natively from a margin before it.
        private static void CheckLevels(Member member, Vector3 camera)
        {
            if (!member.Owned) return;
            if (member.Away) { CheckAway(member, camera, false); return; }
            if (!Crossfades(member))
            {
                float next = Levels(member, camera, out bool switched);
                if (switched) Switch(member);
                CheckAfter(member, next - SwitchMargin);
                return;
            }
            evaluations++;
            bool changed = false;
            float closest = float.PositiveInfinity;
            foreach (var detail in member.Active)
            {
                if (Level(detail, camera, out float margin) != detail.Level) changed = true;
                closest = Mathf.Min(closest, margin);
                KeepBand(member, detail);
            }
            if (!changed && closest >= DetailMargin) { CheckAfter(member, closest - DetailMargin); return; }
            Leave(member, "detail level");
        }

        // Sets each of the member's groups to its current level, and returns how far the camera is from the nearest change.
        private static float Levels(Member member, Vector3 camera, out bool changed)
        {
            evaluations++;
            changed = false;
            float closest = float.PositiveInfinity;
            foreach (var detail in member.Active)
            {
                int level = Level(detail, camera, out float margin);
                if (level != detail.Level) { detail.Level = level; changed = true; }
                closest = Mathf.Min(closest, margin);
                KeepBand(member, detail);
            }
            return closest;
        }

        private static void Switch(Member member)
        {
            WriteSlots(member);
            switches++;
        }

        // A member showing natively follows the level the engine selects, and a crossfade starts where it changes. Once the
        // camera is clear of every change, the fade has ended and a shaken or growing tree has finished, it draws merged again
        // where arriving is allowed, within the frame's budget, and otherwise waits among the finishing members for that.
        private static void CheckAway(Member member, Vector3 camera, bool arrive)
        {
            float closest = Levels(member, camera, out bool changed);
            if (changed) Faded(member);
            if (!arrive || !ClearOfChanges(member, closest) || !Finished(member, Time.realtimeSinceStartup, Time.time)) { Wait(member, closest); return; }
            if (Moved(member)) HandBack(member, "moved"); else Arrive(member, closest);
        }

        // A member showing natively is checked again before the camera can reach one of its changes or be clear of them; once
        // clear, its fade and tree are followed within the frame's budget.
        private static void Wait(Member member, float closest)
        {
            bool clear = ClearOfChanges(member, closest);
            if (clear) Finish(member); else Unfinish(member);
            CheckAfter(member, clear ? closest : Mathf.Min(closest, DetailMargin * 4f - closest));
        }

        private static void Finish(Member member)
        {
            if (member.Finishing >= 0) return;
            member.Finishing = finishing.Count;
            finishing.Add(member);
        }

        // The last finishing member takes the place of one that leaves.
        private static void Unfinish(Member member)
        {
            int index = member.Finishing;
            if (index < 0) return;
            var last = finishing[finishing.Count - 1];
            finishing[index] = last;
            last.Finishing = index;
            finishing.RemoveAt(finishing.Count - 1);
            member.Finishing = -1;
        }

        // A member with a crossfading group draws merged again only with the camera four margins from every change.
        private static bool ClearOfChanges(Member member, float closest) => closest >= DetailMargin * 4f || !Crossfades(member);

        // Whether a crossfade started on the member has ended, in real and game time, and a shaken or growing tree has finished.
        private static bool Finished(Member member, float now, float game) =>
            (now >= member.FadeUntil && game >= member.FadeUntilGame || !Crossfades(member)) && !Busy(member);

        // The members clear of every change draw merged again once their fade and tree have finished, looked through in turn
        // within the frame's budget, at most once each a frame.
        private static void CheckFinishing(Vector3 camera)
        {
            float now = Time.realtimeSinceStartup, game = Time.time;
            for (int looked = finishing.Count; looked > 0 && finishing.Count > 0 && Open(); looked--)
            {
                if (finishingNext >= finishing.Count) finishingNext = 0;
                var member = finishing[finishingNext];
                if (Finished(member, now, game)) CheckAway(member, camera, true);
                // One that drew merged again or went back to waiting near a change gave its place to the last one.
                if (member.Finishing == finishingNext) finishingNext++;
            }
        }

        // Whether a merging renderer has left the place its slots hold, as a shaken trunk might.
        private static bool Moved(Member member)
        {
            foreach (var part in member.Merged)
            {
                var renderer = member.Renderers[part.Renderer];
                if (renderer && renderer.localToWorldMatrix != member.ToWorld[part.Renderer]) return true;
            }
            return false;
        }

        // Queues the member's next check for when the camera has travelled the given distance, voiding its earlier ones. One on a
        // change is checked again as soon as the camera moves.
        private static void CheckAfter(Member member, float distance)
        {
            if (float.IsPositiveInfinity(distance)) { member.CheckStamp++; return; }
            distance = Math.Max(distance, .0001f);
            checks.Push(new Check { Due = travelled + distance, Member = member, Stamp = ++member.CheckStamp, From = lastCamera, Distance = distance });
        }
    }
}
