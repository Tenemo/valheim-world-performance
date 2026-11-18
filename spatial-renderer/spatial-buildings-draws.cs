using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Which slots of a merged mesh draw, the compaction kernel that copies them into the drawn region, and switching members' native renderers.
    internal static partial class SpatialBuildings
    {
        // Draws only the slots that draw now. A slot draws when its matrix is set, whose last element is 1, and holds nothing
        // otherwise. With the compaction kernel a changed set of slots is queued for the drawn region, filled before the next
        // camera draws; without it the renderer draws the span of the whole mesh holding them.
        private static void Drawn(Batch batch) => Drawn(batch, 0, int.MaxValue);

        // Only the slots from first to last can have changed since the mesh's drawn slots were last known.
        private static void Drawn(Batch batch, int first, int last)
        {
            if (batch.IndexBuffer == null)
            {
                int start = int.MaxValue, end = 0;
                for (int slot = 0; slot < batch.IndexFirst.Length; slot++)
                    if (batch.TransformData[slot * 2].m33 != 0f)
                    {
                        start = Math.Min(start, batch.IndexFirst[slot]);
                        end = Math.Max(end, batch.IndexFirst[slot] + batch.IndexCount[slot]);
                    }
                if (start == int.MaxValue) start = end = 0;
                DrawRange(batch, start, end);
                return;
            }
            ulong low = 0, high = 0;
            if (!batch.DrawnKnown) { first = 0; last = int.MaxValue; }
            else { low = batch.DrawnLow; high = batch.DrawnHigh; }
            for (int slot = Math.Max(first, 0); slot <= last && slot < batch.IndexFirst.Length; slot++)
            {
                bool drawn = batch.TransformData[slot * 2].m33 != 0f;
                if (slot < 64) low = drawn ? low | 1UL << slot : low & ~(1UL << slot);
                else high = drawn ? high | 1UL << (slot - 64) : high & ~(1UL << (slot - 64));
            }
            if (batch.DrawnKnown && low == batch.DrawnLow && high == batch.DrawnHigh) return;
            batch.DrawnKnown = true;
            batch.DrawnLow = low;
            batch.DrawnHigh = high;
            if (!batch.Compacting) { batch.Compacting = true; compactions.Add(batch); }
            flushPending = true;
        }

        private static void DrawRange(Batch batch, int first, int end)
        {
            if (first == batch.SpanFirst && end == batch.SpanEnd) return;
            batch.SpanFirst = first;
            batch.SpanEnd = end;
            batch.Mesh.SetSubMesh(0, new SubMeshDescriptor(first, end - first) { firstVertex = 0, vertexCount = batch.Mesh.vertexCount, bounds = batch.Bounds }, quiet);
            ShowDrawn(batch);
        }

        // About half the merged meshes draw nothing at a time, their slots all at levels drawn by other meshes or natively, yet
        // the cameras and shadow passes still culled and submitted each of them. A mesh whose drawn range is empty is switched
        // off until it holds indices again; it drew nothing, so every image stays the same. The isolated client can turn this
        // off to compare.
        internal static bool SkipEmptyDraws = true;

        private static bool DrawsNothing(Batch batch) => SkipEmptyDraws && batch.SpanEnd >= 0 && batch.SpanEnd <= batch.SpanFirst;

        // A mesh the sky depth render switched off stays off through any flush that render triggers; its end shows it again.
        private static void ShowDrawn(Batch batch)
        {
            if (batch.SkyHidden) return;
            bool off = DrawsNothing(batch);
            if (batch.Renderer && batch.Renderer.forceRenderingOff != off) batch.Renderer.forceRenderingOff = off;
            if (batch.Twin && batch.Twin.forceRenderingOff != off) batch.Twin.forceRenderingOff = off;
        }

        internal static void SetSkipEmptyDraws(bool skip)
        {
            SkipEmptyDraws = skip;
            foreach (var cell in cells.Values) foreach (var batch in cell.Batches) if (batch.Mesh) ShowDrawn(batch);
        }

        // Fills each queued mesh's drawn region with the indices of its slots drawing now, in their order, and draws that
        // region. Slots next to each other in the whole mesh copy as one run, and all runs upload at once. Each of the mesh's
        // first lights gets its own copy of the slots drawing now within its reach after the drawn region, where the buffer
        // has room, so that no submesh shares indices with another; a light without room draws the whole drawn region.
        private static void Compact()
        {
            int count = 0;
            foreach (var batch in compactions)
            {
                batch.FirstRun = count;
                int drawn = AddRuns(batch, null, ref count, 0);
                batch.DrawnCount = drawn;
                int lights = Math.Min(batch.Lights.Count, LightRanges), room = batch.Total - batch.DrawnFirst;
                for (int light = 0; light < lights; light++)
                {
                    var range = batch.Lights[light];
                    int runsBefore = count, copied = AddRuns(batch, range, ref count, drawn);
                    range.Whole = copied > room;
                    if (range.Whole) { count = runsBefore; continue; }
                    range.Place = drawn;
                    range.Length = copied - drawn;
                    drawn = copied;
                }
                batch.RunCount = count - batch.FirstRun;
                batch.CopiedCount = drawn;
            }
            if (count > 0)
            {
                if (slotRuns == null || slotRuns.count < count)
                {
                    slotRuns?.Dispose();
                    slotRuns = new GraphicsBuffer(GraphicsBuffer.Target.Structured, runs.Length, Marshal.SizeOf<Run>());
                }
                slotRuns.SetData(runs, 0, 0, count);
            }
            foreach (var batch in compactions)
            {
                batch.Compacting = false;
                if (!batch.Mesh || batch.IndexBuffer == null) continue;
                int drawn = batch.DrawnCount, copied = batch.CopiedCount;
                bool shortIndices = batch.Mesh.indexFormat == IndexFormat.UInt16;
                int groups = ((shortIndices ? (copied + 1) / 2 : copied) + 63) / 64;
                EmptyLightSubmeshes(batch);
                // Beyond the drawn region or what one dispatch reaches, the renderer draws the whole mesh, whose slots drawing
                // nothing collapse.
                if (drawn > batch.Capacity || groups > 65535) { DrawRange(batch, 0, batch.Indices); LightSubmeshes(batch, false); drawnWhole++; continue; }
                if (copied > 0)
                {
                    int kernel = shortIndices ? compact16 : compact32;
                    compaction.SetVector(slotCompactionId, new Vector4(batch.FirstRun, batch.RunCount, copied, batch.DrawnFirst));
                    compaction.SetBuffer(kernel, slotRunsId, slotRuns);
                    compaction.SetBuffer(kernel, mergedIndicesId, batch.IndexBuffer);
                    compaction.Dispatch(kernel, groups, 1, 1);
                    compacted++;
                }
                DrawRange(batch, batch.DrawnFirst, batch.DrawnFirst + drawn);
                LightSubmeshes(batch, true);
            }
            compactions.Clear();
        }

        // Adds the runs copying a mesh's slots drawing now, or those of them within a light's reach, after the places already
        // filled, and returns the places filled.
        private static int AddRuns(Batch batch, LightRange light, ref int count, int drawn)
        {
            int first = -1, end = -1;
            foreach (int slot in batch.Order)
            {
                if (batch.TransformData[slot * 2].m33 == 0f) continue;
                if (light != null && ((slot < 64 ? light.Low >> slot : light.High >> (slot - 64)) & 1UL) == 0) continue;
                int start = batch.IndexFirst[slot], length = batch.IndexCount[slot];
                if (start == end) { end += length; continue; }
                if (first >= 0) AddRun(ref count, first, end, ref drawn);
                first = start;
                end = start + length;
            }
            if (first >= 0) AddRun(ref count, first, end, ref drawn);
            return drawn;
        }

        private static void AddRun(ref int count, int first, int end, ref int drawn)
        {
            if (count == runs.Length) Array.Resize(ref runs, runs.Length * 2);
            runs[count++] = new Run { First = (uint)first, Place = (uint)drawn, Count = (uint)(end - first) };
            drawn += end - first;
        }

        // Whether a merged part draws at its group's current level.
        private static bool Shown(Member member, Part part)
        {
            if (part.Detail < 0) return true;
            int level = member.Details[part.Detail].Level;
            return level >= 0 && (part.Levels & 1 << level) != 0;
        }

        // Gives each of an owned member's slots its matrices when its part draws at the current levels, and nothing otherwise.
        private static void WriteSlots(Member member)
        {
            foreach (var (batch, slot, part) in member.Slots)
            {
                bool shown = Shown(member, part);
                batch.TransformData[slot * 2] = shown ? member.ToWorld[part.Renderer] : default;
                batch.TransformData[slot * 2 + 1] = shown ? member.ToLocal[part.Renderer] : default;
                if (batch.Appearances != null) batch.AppearanceData[slot] = AppearanceOf(member, part);
                batch.TransformFrom = Math.Min(batch.TransformFrom, slot);
                batch.TransformTo = Math.Max(batch.TransformTo, slot);
                batch.AppearanceFrom = Math.Min(batch.AppearanceFrom, slot);
                batch.AppearanceTo = Math.Max(batch.AppearanceTo, slot);
                Flush(batch);
            }
            flushPending = true;
        }

        // Queues a changed member for planning; a change of the member itself restarts its cell's settling time.
        private static void MarkDirty(Member member, bool settle)
        {
            member.Dirty = true;
            if (!member.Queued) { member.Queued = true; member.Cell.Changes.Add(member); }
            if (settle) { member.Cell.Changed = Time.realtimeSinceStartup; member.Cell.ChangedBy = member; }
            active.Add(member.Cell);
        }

        private static void MarkBatch(Batch batch)
        {
            batch.Dirty = true;
            active.Add(batch.Cell);
        }

        private static void Changed(Cell cell)
        {
            cell.Changed = Time.realtimeSinceStartup;
            active.Add(cell);
        }

        // Forgets the member's live slots; an owned member's slots stop drawing before the next render.
        private static void ClearSlots(Member member)
        {
            if (member.Owned) ZeroSlots(member);
            member.Slots.Clear();
        }

        private static void ZeroSlots(Member member)
        {
            foreach (var slot in member.Slots)
            {
                slot.batch.TransformData[slot.slot * 2] = default;
                slot.batch.TransformData[slot.slot * 2 + 1] = default;
                slot.batch.TransformFrom = Math.Min(slot.batch.TransformFrom, slot.slot);
                slot.batch.TransformTo = Math.Max(slot.batch.TransformTo, slot.slot);
                Flush(slot.batch);
                SkySlot(slot.batch, slot.slot, member, slot.part, false);
                flushPending = true;
            }
        }

        // Gives a member drawing merged its sky slots at the levels the sky depth camera selects.
        private static void WriteSky(Member member)
        {
            foreach (var (batch, slot, part) in member.Slots) SkySlot(batch, slot, member, part, true);
        }

        private static void SkySlot(Batch batch, int slot, Member member, Part part, bool drawn)
        {
            if (batch.SkyData == null) return;
            bool shown = drawn && SkyShown(member, part);
            batch.SkyData[slot * 2] = shown ? member.ToWorld[part.Renderer] : default;
            batch.SkyData[slot * 2 + 1] = shown ? member.ToLocal[part.Renderer] : default;
            batch.SkyFrom = Math.Min(batch.SkyFrom, slot);
            batch.SkyTo = Math.Max(batch.SkyTo, slot);
        }

        // A mesh's sky slots from its live members, when the sky depth camera first draws it, when it goes live again and when
        // that camera selects levels anew.
        private static void FillSky(Batch batch)
        {
            if (batch.SkyData == null) batch.SkyData = new Matrix4x4[SlotLimit * 2];
            else Array.Clear(batch.SkyData, 0, batch.SkyData.Length);
            for (int slot = 0; slot < batch.Live.Length; slot++)
            {
                var (member, part, version) = batch.Live[slot];
                if (!member.Removed && member.Version == version && member.Owned && !member.Away) SkySlot(batch, slot, member, part, true);
            }
            batch.SkyFrom = 0;
            batch.SkyTo = SlotLimit - 1;
        }

        // A member's native renderers are hidden at most once, with their own settings, and shown again only while hidden: the
        // measurement switches show and hide them in place as well.
        private static void HideNative(Member member)
        {
            // A crop shows natively while its merged meshes are switched off, also when it arrives meanwhile.
            if (member.NativeHidden || CropsNative && member.Crop) return;
            member.NativeHidden = true;
            for (int index = 0; index < member.Renderers.Length; index++)
            {
                var renderer = member.Renderers[index];
                if (!renderer || !member.Merging[index]) continue;
                member.NativeForcedOff[index] = renderer.forceRenderingOff;
                renderer.forceRenderingOff = true;
            }
            foreach (var detail in member.Details)
                if (detail.Owned && !detail.Fades && detail.Group) { detail.NativeEnabled = detail.Group.enabled; detail.Group.enabled = false; }
        }

        private static void ShowNative(Member member)
        {
            if (!member.NativeHidden) return;
            member.NativeHidden = false;
            foreach (var detail in member.Details)
                if (detail.Owned && !detail.Fades && detail.Group) detail.Group.enabled = detail.NativeEnabled;
            for (int index = 0; index < member.Renderers.Length; index++)
            {
                var renderer = member.Renderers[index];
                if (renderer && member.Merging[index]) renderer.forceRenderingOff = member.NativeForcedOff[index];
            }
        }
    }
}
