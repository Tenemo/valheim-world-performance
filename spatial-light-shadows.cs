using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Point and spot lights that cast shadows draw every caster whose bounds reach into their range into each face of their
    // shadow maps, so a merged mesh drew whole there: the enchanting table's two 2 m lights drew the base's 64 m meshes six
    // times. Merged meshes now cast the sun's shadows from shadow-only twins on two unnamed layers. A shadowed light within
    // reach of merged meshes leaves those layers out of its culling mask and draws, from each mesh it reaches, only the slots
    // whose renderers reach into its range, from its own copy of them after the mesh's drawn region: a shadow-only
    // renderer on one of two more unnamed layers, which the sun leaves out, draws that copy as a submesh, with bounds of a
    // box around the light, so the engine's own shadow culling draws it into that light's faces and those of nearby lights on
    // the same layer. A light is taken over within half a second of casting shadows near merged meshes; until then it draws
    // the twins as before.
    internal static partial class SpatialBuildings
    {
        // The twins' layers: realtime reflection probes see the first, added to their masks, which takes the twins of meshes
        // on layers a probe sees; the second takes the rest. No object of the game uses either.
        private const int ProbeTwinLayer = 30, TwinLayer = 3, TwinLayers = 1 << ProbeTwinLayer | 1 << TwinLayer;
        // The layers of the lights' renderers, which no object of the game uses either: each light taken over sees one of them.
        private const int FirstRangeLayer = 6, RangeLayers = 1 << FirstRangeLayer | 1 << FirstRangeLayer + 1;
        private const string TwinName = "World Performance spatial building shadows", RangeName = "World Performance spatial light shadows";
        // Each merged mesh keeps copies for this many lights, as submeshes after the whole mesh's; further lights, and those
        // its index buffer has no room for, draw its whole drawn region.
        private const int LightRanges = 4, FirstLightSubmesh = 2;
        private const float LightSearchSeconds = .5f;
        // A light's slots are read this far beyond its range, so it moves or grows this much before they are read again: a
        // flickering flame moves every frame.
        private const float LightSlack = 1f;

        private sealed class ShadowLight
        {
            internal Light Light;
            internal int Id, Layer, Excluded;
            // Where its slots were read from, how far, and its shadow maps' near plane, which serve until a destroyed light is
            // released.
            internal Vector3 Position;
            internal float Reach, NearPlane;
            internal readonly List<LightRange> Ranges = new List<LightRange>();
        }

        // A light's slots in one merged mesh, those whose renderers reach into its range, its copy of those drawing now after
        // the mesh's drawn region, and the renderer drawing it.
        private sealed class LightRange
        {
            internal ShadowLight Light;
            internal Batch Batch;
            internal ulong Low, High;
            internal int Submesh = -1, Count;
            // Where the compaction being prepared copies them, or that the buffer had no room, so the light draws the whole
            // drawn region.
            internal int Place, Length;
            internal bool Whole;
            internal MeshFilter Filter;
            internal MeshRenderer Renderer;
        }

        private static readonly List<ShadowLight> shadowLights = new List<ShadowLight>();
        private static readonly Dictionary<int, ShadowLight> lightStates = new Dictionary<int, ShadowLight>();
        // The layers added to realtime reflection probes' masks and taken from directional lights' masks.
        private static readonly Dictionary<ReflectionProbe, int> changedProbes = new Dictionary<ReflectionProbe, int>();
        private static readonly Dictionary<Light, int> changedSuns = new Dictionary<Light, int>();
        private static int probeLayers;
        private static float nextLightSearch;
        // The lights of a search in progress and how many of them it has considered.
        private static Light[] searching;
        private static int searched;
        // The light being taken over, the merged meshes within its reach and the next one whose slots it reads.
        private static ShadowLight taking;
        private static readonly List<Batch> takingBatches = new List<Batch>();
        private static int takingNext;
        private static long lightsTaken;
        // Fills the material slots of the submeshes before a light's range: its shader has no shadow caster pass, so they draw
        // nothing in shadow passes.
        private static Material skipMaterial;
        // Whether shadowed lights draw the twins' whole merged meshes, as lights not yet taken over do, and whether the lights
        // taken over draw their slots, for checks.
        internal static bool WholeLightShadows { get; private set; }
        internal static bool LightSlots { get; private set; } = true;

        private static void StartLights()
        {
            probeLayers = 0;
            nextLightSearch = 0f;
            searching = null;
            taking = null;
            takingBatches.Clear();
            lightsTaken = 0;
            WholeLightShadows = false;
            LightSlots = true;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (!shader) throw new InvalidOperationException("The engine's colored shader is missing.");
            skipMaterial = new Material(shader) { name = "World Performance light shadow gap", hideFlags = HideFlags.HideAndDontSave };
            if (skipMaterial.FindPass("ShadowCaster") >= 0) throw new InvalidOperationException("The engine's colored shader has a shadow caster pass.");
            WatchProbes();
        }

        private static void StopLights()
        {
            ReleaseLights();
            searching = null;
            foreach (var pair in changedProbes) if (pair.Key) pair.Key.cullingMask &= ~pair.Value;
            foreach (var pair in changedSuns) if (pair.Key) pair.Key.cullingMask |= pair.Value;
            changedProbes.Clear();
            changedSuns.Clear();
            if (skipMaterial) UnityEngine.Object.Destroy(skipMaterial);
            skipMaterial = null;
        }

        // Realtime reflection probes see the twins of meshes on the layers they see and the lights' renderers, and twins move
        // when those layers change, as when a world loads.
        private static void WatchProbes()
        {
            int layers = 0;
            foreach (var probe in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
            {
                if (probe.mode != ReflectionProbeMode.Realtime) continue;
                int added = (1 << ProbeTwinLayer | RangeLayers) & ~probe.cullingMask;
                if (added != 0)
                {
                    probe.cullingMask |= added;
                    changedProbes[probe] = (changedProbes.TryGetValue(probe, out int before) ? before : 0) | added;
                }
                layers |= probe.cullingMask & ~(TwinLayers | RangeLayers);
            }
            if (layers == probeLayers) return;
            probeLayers = layers;
            foreach (var cell in cells.Values)
                foreach (var batch in cell.Batches) if (batch.Twin) batch.Twin.gameObject.layer = TwinLayerOf(batch.Key.Layer);
        }

        private static int TwinLayerOf(int layer) => (probeLayers & 1 << layer) != 0 ? ProbeTwinLayer : TwinLayer;

        // A merged mesh that casts shadows casts them from a twin, which draws the same submesh with the same buffers in
        // shadow passes only.
        private static void CreateTwin(Batch batch, Material material, MaterialPropertyBlock properties)
        {
            batch.Renderer.shadowCastingMode = ShadowCastingMode.Off;
            var twin = new GameObject(TwinName) { hideFlags = HideFlags.HideAndDontSave, layer = TwinLayerOf(batch.Key.Layer) };
            twin.transform.SetParent(batch.Object.transform, false);
            batch.TwinFilter = twin.AddComponent<MeshFilter>();
            batch.Twin = twin.AddComponent<MeshRenderer>();
            batch.Twin.sharedMaterial = material;
            batch.Twin.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            batch.Twin.receiveShadows = false;
            batch.Twin.lightProbeUsage = LightProbeUsage.Off;
            batch.Twin.reflectionProbeUsage = ReflectionProbeUsage.Off;
            batch.Twin.renderingLayerMask = batch.Key.Mask;
            batch.Twin.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            batch.Twin.SetPropertyBlock(properties);
        }

        internal static void SetWholeLightShadows(bool whole)
        {
            if (!Running || whole == WholeLightShadows) return;
            WholeLightShadows = whole;
            try { if (whole) ReleaseLights(); else if (TakingLights) TakeLights(); }
            catch (Exception error) { Fail(error); }
        }

        internal static void SetLightSlots(bool draw)
        {
            if (!Running || draw == LightSlots) return;
            LightSlots = draw;
            foreach (var state in shadowLights)
                foreach (var range in state.Ranges) Shape(range);
            if (taking != null) foreach (var range in taking.Ranges) Shape(range);
        }

        // Every frame the lights taken over are followed: one that moved or changed its range beyond the slack reads its slots
        // again, and one that stopped casting shadows, reaches no merged mesh or went away gets its mask back. Twice a second
        // the lights are looked through within the frame's budget, and one still drawing the twins that cast shadows near
        // merged meshes is taken over once its slots are read, one merged mesh at a time; until then it draws the twins, as
        // before.
        private static void UpdateLights()
        {
            if (!TakingLights) return;
            for (int index = shadowLights.Count - 1; index >= 0; index--)
            {
                var state = shadowLights[index];
                var light = state.Light;
                if (!light || !light.isActiveAndEnabled || light.shadows == LightShadows.None || state.Ranges.Count == 0) { Release(state); continue; }
                if (Within(state)) continue;
                if (!Read(state)) Release(state);
            }
            if (searching == null && Time.realtimeSinceStartup >= nextLightSearch && Admit(findCost.Expected(1)))
            {
                var step = Step.Start();
                nextLightSearch = Time.realtimeSinceStartup + LightSearchSeconds;
                WatchProbes();
                searching = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                searched = 0;
                step.Done(findCost, 1);
            }
            while (Open())
            {
                if (taking != null) ReadTaking();
                else if (searching == null) break;
                else if (searched == searching.Length) searching = null;
                else Consider(searching[searched++], true);
            }
        }

        // Whether a light is still within the slack of where and how far its slots were read.
        private static bool Within(ShadowLight state)
        {
            float range = state.Light.range, moved = (state.Light.transform.position - state.Position).magnitude;
            return moved + range <= state.Reach && range + 2f * LightSlack >= state.Reach;
        }

        // Lights are taken over while merged meshes draw their compacted regions; a switch back takes them over at once, so
        // the next render draws as the renderer does.
        private static bool TakingLights => !WholeLightShadows && !Native;

        private static void TakeLights()
        {
            nextLightSearch = Time.realtimeSinceStartup + LightSearchSeconds;
            searching = null;
            DropTaking();
            WatchProbes();
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) Consider(light, false);
        }

        // Directional lights leave out the lights' layers. A point or spot light near merged meshes is taken over: at once
        // when merged meshes switch back to their compacted regions, and in a search once its slots are read within the
        // frame's budget.
        private static void Consider(Light light, bool budgeted)
        {
            if (!light) return;
            if (light.type == LightType.Directional)
            {
                int removed = light.cullingMask & RangeLayers;
                if (removed == 0) return;
                light.cullingMask &= ~removed;
                changedSuns[light] = (changedSuns.TryGetValue(light, out int before) ? before : 0) | removed;
                return;
            }
            if (!Eligible(light) || lightStates.ContainsKey(light.GetInstanceID())) return;
            var state = new ShadowLight { Light = light, Id = light.GetInstanceID(), Position = light.transform.position, Reach = light.range + LightSlack, NearPlane = light.shadowNearPlane };
            float reach = state.Reach * state.Reach;
            foreach (var cell in cells.Values)
                foreach (var batch in cell.Batches)
                    if (batch.Twin && batch.Mesh && batch.Bounds.SqrDistance(state.Position) <= reach) takingBatches.Add(batch);
            if (takingBatches.Count == 0) return;
            Layer(state);
            taking = state;
            takingNext = 0;
            if (!budgeted) while (taking != null) ReadTaking();
        }

        // A shadowed point or spot light that draws the twins and both lights' layers, whose shadows a takeover can draw from
        // merged slots.
        private static bool Eligible(Light light) => light.shadows != LightShadows.None && (light.type == LightType.Point || light.type == LightType.Spot) &&
            light.isActiveAndEnabled && (light.cullingMask & TwinLayers) != 0 && (light.cullingMask & RangeLayers) == RangeLayers;

        // Reads the slots of the light being taken over in its next merged mesh, on the layer it will draw, and takes it over
        // once all are read. Until then other lights draw those slots too, which cast the same shadows as the twins they also
        // draw. One that moved, changed or stopped casting shadows meanwhile is left to a later search.
        private static void ReadTaking()
        {
            var state = taking;
            if (takingNext < takingBatches.Count)
            {
                var batch = takingBatches[takingNext++];
                if (batch.Twin && batch.Mesh && batch.Bounds.SqrDistance(state.Position) <= state.Reach * state.Reach) Reach(state, batch);
                return;
            }
            taking = null;
            takingBatches.Clear();
            if (state.Light && Eligible(state.Light) && Within(state) && state.Ranges.Count > 0) { Take(state); return; }
            foreach (var range in state.Ranges) Forget(range);
            state.Ranges.Clear();
        }

        // Drops the takeover in progress with the slots read for it.
        private static void DropTaking()
        {
            if (taking == null) return;
            foreach (var range in taking.Ranges) Forget(range);
            taking.Ranges.Clear();
            taking = null;
            takingBatches.Clear();
        }

        // Reads where a light is and the slots of each merged mesh with a twin that reach into its range, with the slack;
        // false when it reaches none.
        private static bool Read(ShadowLight state)
        {
            state.Position = state.Light.transform.position;
            state.Reach = state.Light.range + LightSlack;
            state.NearPlane = state.Light.shadowNearPlane;
            float reach = state.Reach * state.Reach;
            for (int index = state.Ranges.Count - 1; index >= 0; index--)
            {
                var range = state.Ranges[index];
                if (range.Batch.Mesh && range.Batch.Bounds.SqrDistance(state.Position) <= reach) continue;
                Forget(range);
                state.Ranges.RemoveAt(index);
            }
            foreach (var cell in cells.Values)
                foreach (var batch in cell.Batches)
                    if (batch.Twin && batch.Mesh && batch.Bounds.SqrDistance(state.Position) <= reach) Reach(state, batch);
            foreach (var range in state.Ranges) range.Renderer.bounds = LightBox(state);
            return state.Ranges.Count > 0;
        }

        // A box around where the light's slots were read, reaching past its shadow maps' near plane into every face wherever
        // the light moves before they are read again.
        private static Bounds LightBox(ShadowLight state) => new Bounds(state.Position, Vector3.one * (2f * (LightSlack + state.NearPlane) + 1f));

        // A light's slots in one merged mesh, read again when the light moves or the mesh is rebuilt; a changed set queues the
        // mesh's compaction, which copies them.
        private static void Reach(ShadowLight state, Batch batch)
        {
            float reach = state.Reach * state.Reach;
            ulong low = 0, high = 0;
            for (int slot = 0; slot < batch.Live.Length; slot++)
            {
                var (member, part, version) = batch.Live[slot];
                if (member.Removed || member.Version != version || member.RendererBounds[part.Renderer].SqrDistance(state.Position) > reach) continue;
                if (slot < 64) low |= 1UL << slot;
                else high |= 1UL << (slot - 64);
            }
            var range = batch.Lights.Find(each => each.Light == state);
            if (low == 0 && high == 0)
            {
                if (range != null) { Forget(range); state.Ranges.Remove(range); }
                return;
            }
            if (range == null)
            {
                range = new LightRange { Light = state, Batch = batch };
                var holder = new GameObject(RangeName) { hideFlags = HideFlags.HideAndDontSave, layer = state.Layer };
                holder.transform.SetParent(batch.Object.transform, false);
                range.Filter = holder.AddComponent<MeshFilter>();
                range.Filter.sharedMesh = batch.Mesh;
                range.Renderer = holder.AddComponent<MeshRenderer>();
                range.Renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                range.Renderer.receiveShadows = false;
                range.Renderer.lightProbeUsage = LightProbeUsage.Off;
                range.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                range.Renderer.renderingLayerMask = batch.Key.Mask;
                range.Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                // A light's copy of small natural parts draws only the slots that cast, as their native renderers do.
                range.Renderer.SetPropertyBlock(batch.TwinProperties ?? batch.Properties);
                range.Renderer.bounds = LightBox(state);
                Shape(range);
                state.Ranges.Add(range);
                batch.Lights.Add(range);
            }
            else if (range.Low == low && range.High == high) return;
            range.Low = low;
            range.High = high;
            Recompact(batch);
        }

        // A range draws its submesh, or what the renderer draws before the compaction copies it, while the mesh draws whole,
        // without room for the copy or beyond the first four lights; with none of its slots drawing now it draws nothing.
        private static void Shape(LightRange range)
        {
            var materials = new Material[Math.Max(0, range.Submesh) + 1];
            for (int index = 0; index < materials.Length - 1; index++) materials[index] = skipMaterial;
            materials[materials.Length - 1] = range.Batch.Renderer.sharedMaterial;
            range.Renderer.sharedMaterials = materials;
            range.Renderer.enabled = LightSlots && (range.Submesh < 0 || range.Count > 0);
        }

        private static void Forget(LightRange range)
        {
            range.Batch.Lights.Remove(range);
            if (range.Renderer) UnityEngine.Object.Destroy(range.Renderer.gameObject);
            Recompact(range.Batch);
        }

        private static void Recompact(Batch batch)
        {
            if (!batch.Mesh || batch.IndexBuffer == null) return;
            batch.DrawnKnown = false;
            if (!batch.Compacting) { batch.Compacting = true; compactions.Add(batch); }
            flushPending = true;
        }

        // A light taken over sees the one of the lights' layers that fewer lights taken over near it draw on.
        private static void Layer(ShadowLight state)
        {
            int first = 0, second = 0;
            foreach (var other in shadowLights)
            {
                if ((other.Position - state.Position).magnitude > Mathf.Max(other.Reach, state.Reach) + 2f * LightSlack) continue;
                if (other.Layer == FirstRangeLayer) first++;
                else second++;
            }
            state.Layer = first <= second ? FirstRangeLayer : FirstRangeLayer + 1;
        }

        // Takes a light over: it leaves out the twins and the other light layer, so it draws only its own slots and those of
        // nearby lights on its layer.
        private static void Take(ShadowLight state)
        {
            int excluded = TwinLayers | RangeLayers & ~(1 << state.Layer);
            state.Excluded = state.Light.cullingMask & excluded;
            state.Light.cullingMask &= ~excluded;
            shadowLights.Add(state);
            lightStates.Add(state.Id, state);
            lightsTaken++;
        }

        // Gives a light back its mask, so it draws the twins again.
        private static void Release(ShadowLight state)
        {
            if (state.Light) state.Light.cullingMask |= state.Excluded;
            foreach (var range in state.Ranges) Forget(range);
            state.Ranges.Clear();
            shadowLights.Remove(state);
            lightStates.Remove(state.Id);
        }

        private static void ReleaseLights()
        {
            DropTaking();
            for (int index = shadowLights.Count - 1; index >= 0; index--) Release(shadowLights[index]);
        }

        // A rebuilt or new mesh: its ranges draw the new mesh, lights taken over and the one being taken over read its slots,
        // and one it newly reaches draws them too.
        private static void LightsReach(Batch batch)
        {
            if (!batch.Twin) return;
            foreach (var range in batch.Lights)
            {
                range.Filter.sharedMesh = batch.Mesh;
                range.Renderer.bounds = LightBox(range.Light);
            }
            foreach (var state in shadowLights) ReachAgain(state, batch);
            if (taking != null) ReachAgain(taking, batch);
        }

        private static void ReachAgain(ShadowLight state, Batch batch)
        {
            if (batch.Bounds.SqrDistance(state.Position) <= state.Reach * state.Reach || batch.Lights.Exists(range => range.Light == state)) Reach(state, batch);
        }

        // A mesh going away leaves the lights that drew from it, and its ranges' renderers go with it.
        private static void LightsLeave(Batch batch)
        {
            foreach (var range in batch.Lights) range.Light.Ranges.Remove(range);
            batch.Lights.Clear();
        }

        // Empties the light submeshes holding a copy, before the drawn range moves over where they were.
        private static void EmptyLightSubmeshes(Batch batch)
        {
            for (int light = 0; light < LightRanges; light++)
                if ((batch.LightSubmeshes & 1 << light) != 0)
                    batch.Mesh.SetSubMesh(FirstLightSubmesh + light, new SubMeshDescriptor(batch.Total, 0) { firstVertex = 0, vertexCount = batch.Mesh.vertexCount, bounds = batch.Bounds }, quiet);
            batch.LightSubmeshes = 0;
        }

        // Gives each of a compacted mesh's first lights a submesh over its copy; the others, those without room, and every
        // light of a mesh drawn whole, draw what the renderer draws.
        private static void LightSubmeshes(Batch batch, bool ranged)
        {
            for (int light = 0; light < batch.Lights.Count; light++)
            {
                var range = batch.Lights[light];
                bool own = ranged && light < LightRanges && !range.Whole;
                int submesh = own ? FirstLightSubmesh + light : -1, count = own ? range.Length : 0;
                if (count > 0)
                {
                    batch.Mesh.SetSubMesh(submesh, new SubMeshDescriptor(batch.DrawnFirst + range.Place, count) { firstVertex = 0, vertexCount = batch.Mesh.vertexCount, bounds = batch.Bounds }, quiet);
                    batch.LightSubmeshes |= 1 << light;
                }
                if (range.Submesh == submesh && range.Count == count) continue;
                bool reshape = range.Submesh != submesh || (range.Count > 0) != (count > 0);
                range.Submesh = submesh;
                range.Count = count;
                if (reshape) Shape(range);
            }
        }
    }
}
