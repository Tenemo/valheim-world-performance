using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Planning merged meshes, building them in jobs, applying the results and reading back source meshes the jobs need.
    internal static partial class SpatialBuildings
    {
        // Reads every source vertex a batch uses and writes the merged vertices, slot indices and indices.
        private struct BuildJob : IJob
        {
            [ReadOnly] public Mesh.MeshDataArray Sources;
            [ReadOnly] public NativeArray<Input> Inputs;
            public Mesh.MeshDataArray Output;
            public NativeArray<int> Result;
            // Vegetation and Standard vertices also carry the second texture coordinates.
            public bool Vegetation;
            // A mesh of at most 65,535 vertices takes 16-bit indices.
            public bool ShortIndices;

            public void Execute()
            {
                var output = Output[0];
                var vertices = Vegetation ? default : output.GetVertexData<Vertex>(0);
                var vegetation = Vegetation ? output.GetVertexData<VegetationVertex>(0) : default;
                var slots = output.GetVertexData<uint>(1);
                var indices = ShortIndices ? default : output.GetIndexData<uint>();
                var shortIndices = ShortIndices ? output.GetIndexData<ushort>() : default;
                int vertexOffset = 0, indexOffset = 0, current = -1;
                var positions = default(NativeArray<Vector3>);
                var normals = default(NativeArray<Vector3>);
                var tangents = default(NativeArray<Vector4>);
                var uv0 = default(NativeArray<Vector2>);
                var uv1 = default(NativeArray<Vector2>);
                bool hasNormals = false, hasTangents = false, has0 = false, has1 = false;
                for (int member = 0; member < Inputs.Length; member++)
                {
                    var input = Inputs[member];
                    var source = Sources[input.Source];
                    if (input.Source != current)
                    {
                        current = input.Source;
                        if (positions.IsCreated)
                        {
                            positions.Dispose();
                            if (normals.IsCreated) normals.Dispose();
                            if (tangents.IsCreated) tangents.Dispose();
                            if (uv0.IsCreated) uv0.Dispose();
                            if (uv1.IsCreated) uv1.Dispose();
                        }
                        normals = default; tangents = default; uv0 = default; uv1 = default;
                        int count = source.vertexCount;
                        positions = new NativeArray<Vector3>(count, Allocator.Temp);
                        source.GetVertices(positions);
                        if (hasNormals = source.HasVertexAttribute(VertexAttribute.Normal)) source.GetNormals(normals = new NativeArray<Vector3>(count, Allocator.Temp));
                        if (hasTangents = source.HasVertexAttribute(VertexAttribute.Tangent)) source.GetTangents(tangents = new NativeArray<Vector4>(count, Allocator.Temp));
                        if (has0 = source.HasVertexAttribute(VertexAttribute.TexCoord0)) source.GetUVs(0, uv0 = new NativeArray<Vector2>(count, Allocator.Temp));
                        if (has1 = Vegetation && source.HasVertexAttribute(VertexAttribute.TexCoord1)) source.GetUVs(1, uv1 = new NativeArray<Vector2>(count, Allocator.Temp));
                    }
                    int first = input.First, length = input.Count;
                    for (int vertex = 0; vertex < length; vertex++)
                    {
                        int at = first + vertex;
                        // A three-component normal reaches the programs with a fourth of 1, as natively.
                        var direction = hasNormals ? normals[at] : Vector3.zero;
                        var normal = new Unit(new Vector4(direction.x, direction.y, direction.z, 1f));
                        var tangent = hasTangents ? new Unit(tangents[at]) : default;
                        var texture0 = has0 ? uv0[at] : Vector2.zero;
                        if (Vegetation)
                            vegetation[vertexOffset + vertex] = new VegetationVertex {
                                Position = positions[at], Normal = normal, Tangent = tangent, Texture0 = texture0, Texture1 = has1 ? uv1[at] : Vector2.zero };
                        else
                            vertices[vertexOffset + vertex] = new Vertex { Position = positions[at], Normal = normal, Tangent = tangent, Texture0 = texture0 };
                        slots[vertexOffset + vertex] = (uint)input.Slot;
                    }
                    var sourceIndices = new NativeArray<int>(source.GetSubMesh(input.Submesh).indexCount, Allocator.Temp);
                    source.GetIndices(sourceIndices, input.Submesh, true);
                    for (int index = 0; index < sourceIndices.Length; index++)
                    {
                        int local = sourceIndices[index] - first;
                        if (local < 0 || local >= length) { Result[0] = 1; return; }
                        if (ShortIndices) shortIndices[indexOffset + index] = (ushort)(vertexOffset + local);
                        else indices[indexOffset + index] = (uint)(vertexOffset + local);
                    }
                    vertexOffset += length;
                    indexOffset += sourceIndices.Length;
                    sourceIndices.Dispose();
                }
                // Any rest of the buffer is the drawn region, which the compaction kernel fills.
                if (ShortIndices) for (int index = indexOffset; index < shortIndices.Length; index++) shortIndices[index] = 0;
                else for (int index = indexOffset; index < indices.Length; index++) indices[index] = 0;
            }
        }

        // Applies finished jobs within the frame's budget; each mesh goes live on its own, and the members it completes are
        // owned within the budget after it. A new mesh's renderer objects are made in a step of their own while its job runs,
        // so its first apply takes no longer than later ones; they draw nothing until it goes live.
        private static void Complete(Vector3 camera)
        {
            int applied = 0;
            for (int index = 0; index < builds.Count && applied < AppliesPerFrame; index++)
            {
                var build = builds[index];
                if (!build.Batch.Object)
                {
                    if (!Admit(createCost.Expected(1))) break;
                    var creating = Step.Start();
                    CreateBatch(build.Batch);
                    creating.Done(createCost, 1);
                    continue;
                }
                if (!build.Handle.IsCompleted) continue;
                if (!Admit(applyCost.Expected(build.Vertices))) break;
                builds.RemoveAt(index--);
                var step = Step.Start();
                long start = Stopwatch.GetTimestamp();
                build.Handle.Complete();
                bool valid = build.Result[0] == 0;
                build.Sources.Dispose();
                build.Inputs.Dispose();
                build.Result.Dispose();
                if (!valid) { build.Output.Dispose(); build.Batch.Build = null; throw new InvalidOperationException("A source mesh's indices fall outside its submesh range."); }
                var mesh = new Mesh { name = "World Performance spatial buildings", hideFlags = HideFlags.HideAndDontSave };
                Mesh.ApplyAndDisposeWritableMeshData(build.Output, mesh, quiet);
                // The compaction kernel writes the drawn region through a raw view of the index buffer, set while the mesh still
                // has its processor copy to create the buffer from.
                if (compactIndices) mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
                mesh.bounds = build.Bounds;
                mesh.UploadMeshData(true);
                long spent = Stopwatch.GetTimestamp() - start;
                jobTicks += spent;
                if (spent > slowestApply) { slowestApply = spent; slowestApplyVertices = mesh.vertexCount; }
                Swap(build, mesh);
                step.Done(applyCost, build.Vertices);
                applied++;
            }
        }

        // Owns, within the frame's budget, the members whose merged parts went live.
        private static void OwnCompleted(Vector3 camera)
        {
            while (owning.Count > 0 && Open())
            {
                var member = owning.Dequeue();
                member.OwnQueued = false;
                Own(member, camera);
            }
        }

        // Puts a rebuilt mesh live. Owned members keep drawing from their new slots at their current levels; other members
        // reserve theirs with empty transforms and are queued to become owned once every merged part of their current version
        // is live.
        private static void Swap(Build build, Mesh mesh)
        {
            var batch = build.Batch;
            batch.Build = null;
            foreach (var (member, _, _) in batch.Live)
                for (int index = member.Slots.Count - 1; index >= 0; index--) if (member.Slots[index].batch == batch) member.Slots.RemoveAt(index);
            var old = batch.Mesh;
            batch.IndexBuffer?.Dispose();
            batch.Mesh = mesh;
            batch.Bounds = build.Bounds;
            batch.Filter.sharedMesh = mesh;
            if (batch.TwinFilter) batch.TwinFilter.sharedMesh = mesh;
            batch.LightSubmeshes = 0;
            if (old) UnityEngine.Object.Destroy(old);
            batch.IndexBuffer = compactIndices ? mesh.GetIndexBuffer() : null;
            if (batch.IndexBuffer != null && (batch.IndexBuffer.target & GraphicsBuffer.Target.Raw) == 0)
                throw new InvalidOperationException("A merged mesh's index buffer has no raw view for the compaction kernel: mesh " + mesh.indexBufferTarget +
                    ", buffer " + batch.IndexBuffer.target + ".");
            Array.Clear(batch.TransformData, 0, batch.TransformData.Length);
            for (int slot = 0; slot < build.Slots.Length; slot++)
            {
                var (member, part, version) = build.Slots[slot];
                if (member.Removed || member.Version != version) continue;
                member.Slots.Add((batch, slot, part));
                if (batch.Appearances != null) batch.AppearanceData[slot] = AppearanceOf(member, part);
                if (member.Owned)
                {
                    if (!member.Away && Shown(member, part))
                    {
                        batch.TransformData[slot * 2] = member.ToWorld[part.Renderer];
                        batch.TransformData[slot * 2 + 1] = member.ToLocal[part.Renderer];
                    }
                }
                else if (!member.OwnQueued) { member.OwnQueued = true; owning.Enqueue(member); }
            }
            batch.Live = build.Slots;
            batch.IndexFirst = build.IndexFirst;
            batch.IndexCount = build.IndexCount;
            batch.Order = build.Order;
            batch.Indices = (int)mesh.GetIndexCount(1);
            batch.Total = build.Total;
            mergedBytes -= batch.Bytes;
            mergedBytes += batch.Bytes = GraphicsBytes(batch);
            batch.DrawnFirst = build.DrawnFirst;
            batch.Capacity = build.Capacity;
            batch.SpanFirst = build.DrawnFirst;
            batch.SpanEnd = compactIndices ? build.DrawnFirst : -1;
            batch.DrawnKnown = false;
            if (batch.SkyData != null) FillSky(batch);
            QueueSky(batch);
            batch.Transforms.SetData(batch.TransformData);
            if (batch.TwinTransforms != null)
            {
                Array.Clear(batch.SlotFar, 0, batch.SlotFar.Length);
                FarSlots(batch, lastCamera);
                UploadTwin(batch, 0, SlotLimit - 1);
            }
            Drawn(batch);
            ShowDrawn(batch);
            batch.Appearances?.SetData(batch.AppearanceData);
            batch.TransformFrom = batch.AppearanceFrom = int.MaxValue;
            batch.TransformTo = batch.AppearanceTo = -1;
            batch.Renderer.enabled = Drawn(batch.Key);
            if (batch.Twin) batch.Twin.enabled = Casts(batch);
            LightsReach(batch);
            swaps++;
        }

        // Takes over a member whose merged parts are all live: it draws merged at once at the levels the camera selects. One
        // with a crossfading group keeps showing natively while the camera is near one of its detail-level changes, and waits
        // one fade first, since the camera may have passed a change just before. One that moved or was scaled since it was
        // planned is planned again instead.
        private static void Own(Member member, Vector3 camera)
        {
            if (member.Owned || member.Removed || member.Dirty || member.Merged.Count == 0 || member.Slots.Count != member.Merged.Count) return;
            if (Moved(member)) { HandBack(member, "moved"); return; }
            member.Owned = true;
            float closest = Levels(member, camera, out _);
            if (Crossfades(member)) Faded(member);
            if (ClearOfChanges(member, closest) && Finished(member, Time.realtimeSinceStartup, Time.time)) { Arrive(member, closest); return; }
            member.Away = true;
            near.Add(member);
            Wait(member, closest);
        }

        // Builds readable copies of source meshes the GPU has returned, one at a time within the frame's budget.
        private static void CopyReadables()
        {
            while (received.Count > 0)
            {
                var source = received.Peek();
                var mesh = source.Mesh;
                int vertices = mesh ? mesh.vertexCount : 0;
                if (!Admit(copyCost.Expected(vertices))) break;
                received.Dequeue();
                if (!mesh) { source.Failed = true; ReleaseData(source); continue; }
                var step = Step.Start();
                var readable = new Mesh { name = mesh.name + " readable copy", indexFormat = mesh.indexFormat, hideFlags = HideFlags.HideAndDontSave };
                readable.SetVertexBufferParams(mesh.vertexCount, mesh.GetVertexAttributes());
                for (int index = 0; index < source.Streams.Length; index++)
                    readable.SetVertexBufferData(source.Streams[index], 0, 0, source.Streams[index].Length, index, quiet);
                int size = mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4;
                readable.SetIndexBufferParams(source.Indices.Length / size, mesh.indexFormat);
                if (size == 2) readable.SetIndexBufferData(source.Indices.Reinterpret<ushort>(1), 0, 0, source.Indices.Length / 2, quiet);
                else readable.SetIndexBufferData(source.Indices.Reinterpret<uint>(1), 0, 0, source.Indices.Length / 4, quiet);
                readable.subMeshCount = mesh.subMeshCount;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) readable.SetSubMesh(submesh, mesh.GetSubMesh(submesh), quiet);
                readable.bounds = mesh.bounds;
                source.Ranges = Ranges(readable);
                source.Readable = readable;
                ReleaseData(source);
                step.Done(copyCost, vertices);
            }
        }

        // Plans settled cells' changed members, nearest cells first, and starts jobs for the meshes they change.
        private static void Schedule(Vector3 camera)
        {
            float now = Time.realtimeSinceStartup;
            cellOrder.Clear();
            cellOrder.AddRange(active);
            orderFrom = camera;
            cellOrder.Sort(nearestFirst);
            foreach (var cell in cellOrder)
            {
                if (!Open()) break;
                if (now - cell.Changed < SettleSeconds) continue;
                // Every source mesh of a member must be readable before it plans, and a falling, shaken or growing natural object
                // must have come to rest; readbacks run while it waits.
                for (int index = cell.Changes.Count - 1; index >= 0 && Open(); index--)
                {
                    var member = cell.Changes[index];
                    if (member.Dirty && !member.Removed && (Busy(member) || !Readable(member))) continue;
                    cell.Changes.RemoveAt(index);
                    member.Queued = false;
                    if (!member.Dirty || member.Removed) continue;
                    Plan(member);
                    member.Dirty = false;
                }
                if (cell.Changes.Count > 0) continue;
                bool waiting = false;
                for (int index = cell.Batches.Count - 1; index >= 0; index--)
                {
                    var batch = cell.Batches[index];
                    if (!batch.Dirty) continue;
                    // A new mesh waits while the merged meshes take their share of graphics memory; its members stay native.
                    if (batch.Build != null || builds.Count >= BuildsInFlight || !batch.Mesh && batch.Members.Count > 0 && mergedBytes >= MemoryLimit ||
                        !Admit(buildCost.Expected(batch.Count))) { waiting = true; continue; }
                    batch.Dirty = false;
                    // Nothing to draw: the mesh goes, and members that lose their place are planned again.
                    if (batch.Members.Count == 0) { DestroyBatch(batch); continue; }
                    var step = Step.Start();
                    StartBuild(batch);
                    step.Done(buildCost, batch.Count);
                    rebuilds++;
                }
                if (!waiting) idle.Add(cell);
            }
            foreach (var cell in idle)
            {
                if (cell.Changes.Count > 0) continue;
                active.Remove(cell);
                // Cells the player has left are forgotten, so frame work follows the loaded area.
                if (cell.Members.Count == 0 && cell.Batches.Count == 0) cells.Remove(cell.Key);
            }
            idle.Clear();
            cellOrder.Clear();
        }

        private static bool Readable(Member member)
        {
            foreach (var part in member.Parts)
            {
                if (!part.Source.Requested) Request(part.Source);
                if (!part.Source.Readable && !part.Source.Failed) return false;
            }
            return true;
        }

        // Chooses the parts of a changed member's visible renderers at every detail level and assigns them to batches with
        // room. Members with values the merged shader cannot express or with unreadable meshes stay native.
        private static void Plan(Member member)
        {
            Unassign(member);
            // Jobs started before this plan no longer hold the member, and its earlier detail-level checks are void.
            member.Version++;
            member.CheckStamp++;
            member.Banded = false;
            member.Slots.Clear();
            member.Merged.Clear();
            member.Active.Clear();
            if (!Expressible(member) || (member.Coverage & NativeCoverage) != 0) return;
            foreach (var part in member.Parts) if (part.Source.Failed) return;
            member.Signature = Signature(member);
            member.Switched = SwitchState(member);
            foreach (var part in member.Parts)
                if (part.Detail >= 0 && (member.Signature & 1UL << part.Renderer) != 0 && !member.Active.Contains(member.Details[part.Detail]))
                    member.Active.Add(member.Details[part.Detail]);
            // Its groups' places and sizes are read with its renderers' matrices, since ground physics moves and mods scale
            // natural objects without the game's own scale change.
            foreach (var detail in member.Details) Place(detail);
            var cell = member.Cell;
            int previous = -1;
            BatchKey style = default;
            bool any = false, crop = member.Planted || member.Parts.Any(part => !part.Material.enableInstancing);
            member.Crop = crop;
            foreach (var part in member.Parts)
            {
                if ((member.Signature & 1UL << part.Renderer) == 0) continue;
                if (part.Source.Ranges[part.Submesh].count == 0) continue;
                if (part.Renderer != previous)
                {
                    // A renderer's settings, matrices and bounds are read once; its submeshes follow it.
                    previous = part.Renderer;
                    var renderer = member.Renderers[part.Renderer];
                    style = new BatchKey { Cell = cell.Key, Layer = renderer.gameObject.layer, Shadows = SmallFarShadows.NativeShadows(renderer), Receive = renderer.receiveShadows,
                        Probes = renderer.lightProbeUsage, Reflections = renderer.reflectionProbeUsage, Mask = renderer.renderingLayerMask, Motion = renderer.motionVectorGenerationMode };
                    member.ToWorld[part.Renderer] = renderer.localToWorldMatrix;
                    // The engine gives shaders the 3D affine inverse of the object's matrix. The renderer's own inverse differs
                    // from it in the last bits, enough to move the edges of stalks that the vegetation shader's wind bends in
                    // the object's space.
                    Matrix4x4.Inverse3DAffine(member.ToWorld[part.Renderer], ref member.ToLocal[part.Renderer]);
                    var bounds = member.RendererBounds[part.Renderer] = renderer.bounds;
                    if (any) member.Bounds.Encapsulate(bounds); else { member.Bounds = bounds; any = true; }
                }
                style.Material = part.Material.GetInstanceID();
                style.Crop = crop;
                style.Small = member.View && style.Shadows == ShadowCastingMode.On && SmallFarShadows.Enabled && SmallFarShadows.IsSmall(member.Renderers[part.Renderer]);
                if (style.Small)
                {
                    if (member.Radius == null) member.Radius = new float[member.Renderers.Length];
                    member.Radius[part.Renderer] = SmallFarShadows.Radius(member.Renderers[part.Renderer]);
                }
                part.Key = style;
                member.Merged.Add(part);
            }
            for (int first = 0; first < member.Merged.Count; first++)
            {
                var key = member.Merged[first].Key;
                bool assigned = false;
                for (int earlier = 0; earlier < first && !assigned; earlier++) assigned = member.Merged[earlier].Key.Equals(key);
                if (assigned) continue;
                int needed = 0, vertices = 0;
                for (int index = first; index < member.Merged.Count; index++)
                {
                    var part = member.Merged[index];
                    if (!part.Key.Equals(key)) continue;
                    needed++;
                    vertices += part.Source.Ranges[part.Submesh].count;
                }
                if (!cell.ByKey.TryGetValue(key, out var candidates)) cell.ByKey.Add(key, candidates = new List<Batch>());
                Batch batch = null;
                foreach (var candidate in candidates)
                    if (candidate.Count + needed <= SlotLimit && (candidate.Count == 0 || candidate.Vertices + vertices <= VertexLimit)) { batch = candidate; break; }
                if (batch == null)
                {
                    var material = member.Merged[first].Material;
                    string source = SupportedShader(material);
                    candidates.Add(batch = new Batch { Key = key, Material = material, Cell = cell, Vegetation = source == vegetationShader || source == standardShader });
                    cell.Batches.Add(batch);
                }
                batch.Members.Add(member);
                batch.Count += needed;
                batch.Vertices += vertices;
                MarkBatch(batch);
                member.Batches[batch] = (needed, vertices);
            }
        }

        private static void StartBuild(Batch batch)
        {
            var slots = new (Member member, Part part, int version)[batch.Count];
            int count = 0;
            foreach (var member in batch.Members)
                foreach (var part in member.Merged)
                    if (part.Key.Equals(batch.Key)) slots[count++] = (member, part, member.Version);
            if (count != slots.Length) throw new InvalidOperationException("A merged mesh's slot count disagrees with its members.");
            uniqueSources.Clear();
            sourceIndices.Clear();
            var order = new int[count];
            var sourceOf = new int[count];
            var levels = new int[count];
            for (int slot = 0; slot < count; slot++)
            {
                order[slot] = slot;
                var source = slots[slot].part.Source;
                if (!sourceIndices.TryGetValue(source, out sourceOf[slot])) { sourceIndices.Add(source, sourceOf[slot] = uniqueSources.Count); uniqueSources.Add(source.Readable); }
                levels[slot] = LevelOrder(slots[slot].part);
            }
            // Inputs are grouped by the detail levels their parts draw at, so the slots drawing at any moment lie in a short
            // span of the indices, and then by source mesh, so the job reads each once.
            Array.Sort(order, (a, b) => levels[a] != levels[b] ? levels[a].CompareTo(levels[b]) : sourceOf[a] != sourceOf[b] ? sourceOf[a].CompareTo(sourceOf[b]) : a.CompareTo(b));
            var inputs = new NativeArray<Input>(count, Allocator.Persistent);
            var indexFirst = new int[count];
            var indexCounts = new int[count];
            int vertexCount = 0, indexCount = 0;
            var bounds = new Bounds();
            for (int at = 0; at < order.Length; at++)
            {
                int slot = order[at];
                var (member, part, _) = slots[slot];
                var range = part.Source.Ranges[part.Submesh];
                vertexCount += range.count;
                indexFirst[slot] = indexCount;
                indexCounts[slot] = (int)part.Source.Readable.GetIndexCount(part.Submesh);
                indexCount += indexCounts[slot];
                inputs[at] = new Input { Source = sourceOf[slot], Submesh = part.Submesh, Slot = slot, First = range.first, Count = range.count };
                var rendererBounds = member.RendererBounds[part.Renderer];
                if (at == 0) bounds = rendererBounds; else bounds.Encapsulate(rendererBounds);
            }
            var output = Mesh.AllocateWritableMeshData(1);
            var data = output[0];
            data.SetVertexBufferParams(vertexCount, batch.Vegetation ? vegetationLayout : layout);
            bool shortIndices = vertexCount <= ushort.MaxValue;
            // With the compaction kernel the whole mesh is followed by a drawn region, which starts at an even index and holds an
            // even count, since the kernel reads and writes a 16-bit mesh's indices in pairs. Shadowed lights' copies of their
            // slots follow it, in what the drawn slots leave of the region and as much again: inside a base nearly every slot
            // draws, and with a quarter more most lights there had no room. Without the kernel the buffer holds the whole mesh alone.
            int drawnFirst = compactIndices ? (indexCount + 1) & ~1 : 0, capacity = compactIndices ? (DrawnCapacity(slots, indexCounts) + 1) & ~1 : 0;
            int total = compactIndices ? drawnFirst + 2 * capacity : indexCount;
            data.SetIndexBufferParams(total, shortIndices ? IndexFormat.UInt16 : IndexFormat.UInt32);
            // The renderer draws the first submesh, the slots drawing now, empty until the first compaction; the sky depth render
            // draws the second, the whole mesh, since its slots follow that camera's own detail levels. Shadowed lights draw the
            // rest, their copies after the drawn region.
            data.subMeshCount = FirstLightSubmesh + LightRanges;
            var whole = new SubMeshDescriptor(0, indexCount) { firstVertex = 0, vertexCount = vertexCount, bounds = bounds };
            var empty = new SubMeshDescriptor(drawnFirst, 0) { firstVertex = 0, vertexCount = vertexCount, bounds = bounds };
            data.SetSubMesh(0, compactIndices ? empty : whole, quiet);
            data.SetSubMesh(1, whole, quiet);
            for (int submesh = FirstLightSubmesh; submesh < data.subMeshCount; submesh++) data.SetSubMesh(submesh, empty, quiet);
            var build = new Build { Batch = batch, Slots = slots, Bounds = bounds, Output = output, Inputs = inputs, IndexFirst = indexFirst, IndexCount = indexCounts,
                Order = order, Total = total, DrawnFirst = drawnFirst, Capacity = capacity, Vertices = vertexCount, Sources = Mesh.AcquireReadOnlyMeshData(uniqueSources),
                Result = new NativeArray<int>(1, Allocator.Persistent) };
            build.Handle = new BuildJob { Sources = build.Sources, Inputs = inputs, Output = output, Result = build.Result, Vegetation = batch.Vegetation, ShortIndices = shortIndices }.Schedule();
            batch.Build = build;
            builds.Add(build);
        }

        // The most indices a mesh's slots can draw at once: each member's parts outside detail groups, and in each of its
        // groups the parts of the level holding the most. A member's slots are next to each other.
        private static int DrawnCapacity((Member member, Part part, int version)[] slots, int[] indexCounts)
        {
            int capacity = 0;
            for (int start = 0, end; start < slots.Length; start = end)
            {
                var member = slots[start].member;
                for (end = start; end < slots.Length && slots[end].member == member; end++)
                    if (slots[end].part.Detail < 0) capacity += indexCounts[end];
                for (int slot = start; slot < end; slot++)
                {
                    int detail = slots[slot].part.Detail;
                    bool first = detail >= 0;
                    for (int earlier = start; earlier < slot && first; earlier++) first = slots[earlier].part.Detail != detail;
                    if (!first) continue;
                    int largest = 0;
                    for (int level = 0; level < 32; level++)
                    {
                        int sum = 0;
                        for (int other = slot; other < end; other++)
                            if (slots[other].part.Detail == detail && (slots[other].part.Levels & 1 << level) != 0) sum += indexCounts[other];
                        largest = Math.Max(largest, sum);
                    }
                    capacity += largest;
                }
            }
            return capacity;
        }

        private static void Discard(Build build)
        {
            build.Handle.Complete();
            build.Sources.Dispose();
            build.Inputs.Dispose();
            build.Result.Dispose();
            build.Output.Dispose();
            build.Batch.Build = null;
        }

        private static void CreateBatch(Batch batch)
        {
            var native = batch.Material;
            var key = batch.Key;
            if (!materials.TryGetValue(native, out var material))
            {
                material = new Material(native) { name = native.name + " spatial buildings", shader = derived[SupportedShader(native)], enableInstancing = false,
                    hideFlags = HideFlags.HideAndDontSave };
                material.EnableKeyword("TENEMO_PIECES");
                materials.Add(native, material);
            }
            batch.Object = new GameObject("World Performance spatial buildings") { hideFlags = HideFlags.HideAndDontSave, layer = key.Layer };
            batch.Filter = batch.Object.AddComponent<MeshFilter>();
            batch.Renderer = batch.Object.AddComponent<MeshRenderer>();
            batch.Renderer.sharedMaterial = material;
            batch.Renderer.shadowCastingMode = key.Shadows;
            batch.Renderer.receiveShadows = key.Receive;
            batch.Renderer.lightProbeUsage = key.Probes;
            batch.Renderer.reflectionProbeUsage = key.Reflections;
            batch.Renderer.renderingLayerMask = key.Mask;
            batch.Renderer.motionVectorGenerationMode = key.Motion;
            batch.Transforms = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 2, 64);
            if (SupportedShader(native) == pieceShader) batch.Appearances = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit, Marshal.SizeOf<Appearance>());
            batch.Context = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 1, 16);
            batch.Context.SetData(new[] { Vector4.zero });
            var properties = new MaterialPropertyBlock();
            properties.SetConstantBuffer(transformsId, batch.Transforms, 0, SlotLimit * 128);
            if (batch.Appearances != null) properties.SetConstantBuffer(appearanceId, batch.Appearances, 0, SlotLimit * Marshal.SizeOf<Appearance>());
            else properties.SetConstantBuffer(probeStateId, probeState, 0, SlotLimit * 7 * 16);
            properties.SetConstantBuffer(contextId, batch.Context, 0, 16);
            batch.Renderer.SetPropertyBlock(properties);
            batch.Properties = properties;
            if (key.Shadows == ShadowCastingMode.On && key.Small)
            {
                batch.TwinTransforms = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 2, 64);
                twins.Add(batch);
                batch.TwinData = new Matrix4x4[SlotLimit * 2];
                batch.SlotFar = new bool[SlotLimit];
                batch.TwinProperties = new MaterialPropertyBlock();
                batch.TwinProperties.SetConstantBuffer(transformsId, batch.TwinTransforms, 0, SlotLimit * 128);
                if (batch.Appearances != null) batch.TwinProperties.SetConstantBuffer(appearanceId, batch.Appearances, 0, SlotLimit * Marshal.SizeOf<Appearance>());
                else batch.TwinProperties.SetConstantBuffer(probeStateId, probeState, 0, SlotLimit * 7 * 16);
                batch.TwinProperties.SetConstantBuffer(contextId, batch.Context, 0, 16);
            }
            if (key.Shadows == ShadowCastingMode.On) CreateTwin(batch, material, batch.TwinProperties ?? properties);
            // The sky depth render reads the values the game's replacement shader takes from the native material.
            batch.SkyProperties = new MaterialPropertyBlock();
            var texture = native.GetTexture(mainTextureId);
            batch.SkyProperties.SetTexture(mainTextureId, texture ? texture : Texture2D.whiteTexture);
            var scale = native.GetTextureScale(mainTextureId);
            var offset = native.GetTextureOffset(mainTextureId);
            batch.SkyProperties.SetVector(mainTextureScaleId, new Vector4(scale.x, scale.y, offset.x, offset.y));
            // Rock materials have no cutoff.
            batch.SkyProperties.SetFloat(cutoffId, native.HasFloat(cutoffId) ? native.GetFloat(cutoffId) : 0f);
        }

        private static void DestroyBatch(Batch batch)
        {
            if (batch.Build != null) { builds.Remove(batch.Build); Discard(batch.Build); }
            LightsLeave(batch);
            // The twin goes with the object.
            if (batch.Object) UnityEngine.Object.Destroy(batch.Object);
            batch.IndexBuffer?.Dispose();
            batch.IndexBuffer = null;
            if (batch.Mesh) UnityEngine.Object.Destroy(batch.Mesh);
            mergedBytes -= batch.Bytes;
            batch.Bytes = 0;
            batch.Transforms?.Dispose();
            if (batch.TwinTransforms != null) twins.Remove(batch);
            batch.TwinTransforms?.Dispose();
            batch.TwinTransforms = null;
            batch.TwinProperties = null;
            batch.Appearances?.Dispose();
            batch.Context?.Dispose();
            batch.SkyTransforms?.Dispose();
            batch.Object = null;
            batch.Twin = null;
            batch.TwinFilter = null;
            batch.Mesh = null;
            batch.Transforms = batch.Appearances = batch.Context = batch.SkyTransforms = null;
            batch.Live = Array.Empty<(Member, Part, int)>();
            var cell = batch.Cell;
            cell.Batches.Remove(batch);
            if (cell.ByKey.TryGetValue(batch.Key, out var candidates)) { candidates.Remove(batch); if (candidates.Count == 0) cell.ByKey.Remove(batch.Key); }
        }

        // Unreadable source meshes are copied from the GPU without stalling the frame.
        private static void Request(Source source)
        {
            if (source.Requested) return;
            source.Requested = true;
            var mesh = source.Mesh;
            if (!mesh) { source.Failed = true; return; }
            if (mesh.isReadable) { source.Ranges = Ranges(mesh); source.Readable = mesh; return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { source.Failed = true; return; }
            source.Streams = new NativeArray<byte>[mesh.vertexBufferCount];
            source.Waiting = mesh.vertexBufferCount + 1;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
            {
                int index = stream;
                var buffer = mesh.GetVertexBuffer(index);
                AsyncGPUReadback.Request(buffer, request => { buffer.Dispose(); Received(source, request, index); });
            }
            var indexBuffer = mesh.GetIndexBuffer();
            AsyncGPUReadback.Request(indexBuffer, request => { indexBuffer.Dispose(); Received(source, request, -1); });
        }

        private static void Received(Source source, AsyncGPUReadbackRequest request, int stream)
        {
            if (!Running || !sources.TryGetValue(source.Mesh, out var current) || current != source) return;
            using (new Timed(Readbacks))
                try
                {
                    if (request.hasError) { source.Failed = true; ReleaseData(source); return; }
                    if (source.Failed) return;
                    var copy = new NativeArray<byte>(request.GetData<byte>(), Allocator.Persistent);
                    if (stream < 0) source.Indices = copy; else source.Streams[stream] = copy;
                    if (--source.Waiting == 0) received.Enqueue(source);
                }
                catch (Exception error) { Fail(error); }
        }

        // The vertices each submesh's indices reach, once per source mesh.
        private static (int first, int count)[] Ranges(Mesh mesh)
        {
            var result = new (int, int)[mesh.subMeshCount];
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                mesh.GetIndices(indexList, submesh, true);
                if (indexList.Count == 0) continue;
                int first = int.MaxValue, last = int.MinValue;
                foreach (int index in indexList) { if (index < first) first = index; if (index > last) last = index; }
                result[submesh] = (first, last - first + 1);
            }
            indexList.Clear();
            return result;
        }

        private static void ReleaseData(Source source)
        {
            if (source.Streams != null) foreach (var stream in source.Streams) if (stream.IsCreated) stream.Dispose();
            if (source.Indices.IsCreated) source.Indices.Dispose();
            source.Streams = null;
            source.Indices = default;
        }

        private static void Release(Source source)
        {
            ReleaseData(source);
            if (source.Readable && source.Readable != source.Mesh) UnityEngine.Object.Destroy(source.Readable);
            source.Readable = null;
        }
    }
}
