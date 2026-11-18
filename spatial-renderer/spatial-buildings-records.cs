using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Registering building pieces and natural objects: recording their renderers, detail groups and parts, or the reason they stay native.
    internal static partial class SpatialBuildings
    {
        private static void Register()
        {
            while (pending.Count > 0 && Open())
            {
                // Registered a frame after creation, once every component of the member has woken and started.
                if (pending.Peek().frame == Time.frameCount) break;
                var wear = pending.Dequeue().wear;
                if (!wear || pieces.ContainsKey(wear)) continue;
                long started = Stopwatch.GetTimestamp();
                string reason = Exclusion(wear, out var member);
                long spent = Stopwatch.GetTimestamp() - started;
                if (spent > slowestRegistration) { slowestRegistration = spent; slowestPrefab = Utils.GetPrefabName(wear.gameObject); }
                if (reason != null)
                {
                    Count(excluded, reason);
                    var ids = MapSwitched(wear, null);
                    if (ids.Length > 0) nativeSwitched[wear] = ids;
                    continue;
                }
                pieces.Add(wear, member);
                member.SwitchedIds = MapSwitched(wear, member);
                Join(member, wear.transform.position);
            }
            if (pendingNatural.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            while (pendingNatural.Count > 0 && Open())
            {
                var (view, time) = pendingNatural.Peek();
                if (now - time < NaturalDelay) break;
                pendingNatural.Dequeue();
                if (!view || natural.ContainsKey(view)) continue;
                long started = Stopwatch.GetTimestamp();
                string reason = NaturalExclusion(view, out var member);
                long spent = Stopwatch.GetTimestamp() - started;
                if (spent > slowestRegistration) { slowestRegistration = spent; slowestPrefab = Utils.GetPrefabName(view.gameObject); }
                if (reason == Waiting) { pendingNatural.Enqueue((view, now)); continue; }
                if (reason != null) { if (reason != BuildingPiece) Count(naturalExcluded, reason); continue; }
                natural.Add(view, member);
                Join(member, view.transform.position);
            }
        }

        // Maps the piece's switched behaviours, those whose nearest piece up the hierarchy is this one, to its member or to none.
        // A behaviour of a native piece with a network view of its own is left to the search, which looks for a natural object
        // with that view.
        private static int[] MapSwitched(WearNTear wear, Member member)
        {
            wear.GetComponentsInChildren(true, behaviourList);
            try
            {
                switchedIdList.Clear();
                var root = wear.gameObject;
                foreach (var behaviour in behaviourList)
                {
                    if (behaviour is null || !switchedTypes.Contains(behaviour.GetType()) || behaviour.GetComponentInParent<WearNTear>(true) != wear) continue;
                    if (member == null && behaviour.TryGetComponent<ZNetView>(out var view) && view.gameObject != root) continue;
                    int id = behaviour.GetInstanceID();
                    switchedMembers[id] = (member, root);
                    switchedIdList.Add(id);
                }
                return switchedIdList.Count > 0 ? switchedIdList.ToArray() : Array.Empty<int>();
            }
            finally { behaviourList.Clear(); }
        }

        // Adds a registered member to its cell; its source meshes are read while the cell settles.
        private static void Join(Member member, Vector3 position)
        {
            foreach (int id in member.RendererIds) rendererMembers[id] = member;
            var key = Vector3Int.FloorToInt(new Vector3(position.x + CellSize / 2, position.y, position.z + CellSize / 2) / CellSize);
            if (!cells.TryGetValue(key, out var cell))
                cells.Add(key, cell = new Cell { Key = key, Center = new Vector3(key.x, key.y + .5f, key.z) * CellSize });
            member.Cell = cell;
            cell.Members.Add(member);
            MarkDirty(member, true);
            foreach (var part in member.Parts)
                if (!part.Source.Requested && !part.Source.Queued) { part.Source.Queued = true; requests.Enqueue(part.Source); }
        }

        // Why a building piece keeps native rendering, or null with its record when stable parts of it can merge.
        private static string Exclusion(WearNTear wear, out Member member)
        {
            member = null;
            var view = wear.GetComponent<ZNetView>();
            if (!view || view.GetZDO() == null) return "no network record";
            int prefab = view.GetZDO().GetPrefab();
            if (!prefabExclusions.TryGetValue(prefab, out string reason))
            {
                var source = ZNetScene.instance.GetPrefab(prefab);
                prefabExclusions[prefab] = reason = source ? Components(source, null, null, allowedBehaviours) : "unknown prefab";
            }
            if (reason != null) return reason;
            var blocks = MaterialMan.instance ? materialBlocks(MaterialMan.instance) : null;
            bool managed = blocks != null && blocks.Contains(wear.gameObject.GetInstanceID());
            if ((reason = Record(wear.gameObject, allowedBehaviours, managed, true, out member)) != null) { recordExclusions[prefab] = reason; return reason; }
            member.Wear = wear;
            if (managed) member.Properties = Properties(containerProperties(blocks[wear.gameObject.GetInstanceID()]));
            return null;
        }

        // Why a natural object keeps native rendering, or null with its record. Pieces a player built stay with the building
        // path or native, and values from the material manager have no slots in the scenery shaders.
        private static string NaturalExclusion(ZNetView view, out Member member)
        {
            member = null;
            var zdo = view.GetZDO();
            if (zdo == null || ghostView(view)) return "no network record";
            int prefab = zdo.GetPrefab();
            if (!naturalExclusions.TryGetValue(prefab, out string reason))
            {
                var source = ZNetScene.instance.GetPrefab(prefab);
                naturalExclusions[prefab] = reason = !source ? "unknown prefab" : source.GetComponent<WearNTear>() ? BuildingPiece : Components(source, null, null, naturalBehaviours);
            }
            if (reason != null) return reason;
            bool planted = view.TryGetComponent<Piece>(out var piece) && piece.GetCreator() != 0;
            if (planted && !MergeCrops) return "built";
            if (view.TryGetComponent<TreeBase>(out var tree) && tree.m_trunk && !tree.m_trunk.activeSelf) return Waiting;
            var blocks = MaterialMan.instance ? materialBlocks(MaterialMan.instance) : null;
            if (blocks != null && blocks.Contains(view.gameObject.GetInstanceID())) return "material manager";
            if ((reason = Record(view.gameObject, naturalBehaviours, false, false, out member)) != null) { recordExclusions[prefab] = reason; return reason; }
            member.View = view;
            member.Planted = planted;
            member.Physics = view.GetComponent<StaticPhysics>();
            return null;
        }

        // A member's renderers, detail groups and mergeable parts, or why it keeps native rendering.
        private static string Record(GameObject root, HashSet<Type> allowed, bool managed, bool building, out Member member)
        {
            member = null;
            rendererList.Clear();
            groupList.Clear();
            string reason;
            if ((reason = Components(root, rendererList, groupList, allowed)) != null) return reason;
            if (rendererList.Count > 64) return "over 64 renderers";
            var renderers = rendererList.ToArray();
            var ids = new int[renderers.Length];
            for (int index = 0; index < renderers.Length; index++) ids[index] = renderers[index].GetInstanceID();
            membership.Clear();
            var details = new Detail[groupList.Count];
            for (int index = 0; index < details.Length; index++)
            {
                var group = groupList[index];
                // An animated crossfade runs for a fixed time after a level change; other fades depend on distance.
                bool fades = group.fadeMode != LODFadeMode.None;
                if (fades && (group.fadeMode != LODFadeMode.CrossFade || !group.animateCrossFading)) return "distance-faded detail levels";
                // The game's fade-in of a distant spawn moves its group's reference point away for a moment.
                if (group.localReferencePoint.x > 999998f) return Waiting;
                var levels = group.GetLODs();
                var thresholds = new float[levels.Length];
                for (int level = 0; level < levels.Length; level++)
                {
                    thresholds[level] = levels[level].screenRelativeTransitionHeight;
                    foreach (var renderer in levels[level].renderers)
                    {
                        // A missing renderer keeps its group choosing levels natively.
                        if (renderer is null) continue;
                        int id = renderer.GetInstanceID();
                        bool known = membership.TryGetValue(id, out var entry);
                        if (!known || entry.detail == index) membership[id] = (index, (known ? entry.levels : 0) | 1 << level);
                    }
                }
                bool descending = true;
                for (int level = 1; level < thresholds.Length; level++) if (!(thresholds[level] <= thresholds[level - 1])) descending = false;
                details[index] = new Detail { Group = group, Thresholds = thresholds, Descending = descending, Level = -2, Owned = true, Fades = fades };
                Place(details[index]);
            }
            var merging = new bool[renderers.Length];
            partList.Clear();
            mergingIds.Clear();
            string rendererReason = null;
            for (int index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                string excluded = RendererExclusion(renderer, managed, building, out var mesh);
                if (excluded != null) { if (rendererReason == null) rendererReason = excluded; continue; }
                if (!sources.TryGetValue(mesh, out var source)) sources.Add(mesh, source = new Source { Mesh = mesh });
                var (detail, mask) = membership.TryGetValue(ids[index], out var levels) ? levels : (-1, 0);
                for (int submesh = 0; submesh < materialList.Count; submesh++)
                    partList.Add(new Part { Renderer = index, Submesh = submesh, Source = source, Material = materialList[submesh], Detail = detail, Levels = mask });
                merging[index] = true;
                mergingIds.Add(ids[index]);
                if (detail >= 0) details[detail].Merging = true;
            }
            if (partList.Count == 0) return "no supported renderer: " + (rendererReason ?? "none in the hierarchy");
            if (partList.Count > SlotLimit) return "over " + SlotLimit + " merged parts";
            // A group whose renderers are not all merged keeps choosing its level natively.
            foreach (var entry in membership) if (!mergingIds.Contains(entry.Key)) details[entry.Value.detail].Owned = false;
            member = new Member { Root = root, Renderers = renderers, RendererIds = ids, NativeForcedOff = new bool[renderers.Length], Merging = merging,
                ToWorld = new Matrix4x4[renderers.Length], ToLocal = new Matrix4x4[renderers.Length], RendererBounds = new Bounds[renderers.Length],
                Parts = partList.ToArray(), Details = details };
            if (member.Parts.Any(part => !part.Material.enableInstancing)) member.Coverage |= UninstancedPieces;
            member.Switches = SwitchedObjects(root, building ? root.GetComponent<WearNTear>() : null);
            member.Switched = SwitchState(member);
            member.Signature = Signature(member);
            return null;
        }

        // Collects a hierarchy's mesh renderers and detail groups, or names the component that keeps it native. Objects a
        // local behaviour moves, and everything under them, are left out instead of keeping the whole piece native, as are
        // an item stand's attach point and a cooking station's slots, under which the game creates the models of the item and
        // the food they hold and destroys them at the end of the frame they change in.
        private static string Components(GameObject root, List<MeshRenderer> renderers, List<LODGroup> groups, HashSet<Type> allowed)
        {
            root.GetComponentsInChildren(true, components);
            try
            {
                localRoots.Clear();
                foreach (var component in components)
                {
                    if (component is null) continue;
                    if (component is MonoBehaviour behaviour)
                    {
                        var type = behaviour.GetType();
                        if (!allowed.Contains(type) && !inertComponents.Contains(type.Name)) return "behaviour " + type.Name;
                        if (localBehaviours.Contains(type)) localRoots.Add(behaviour.transform);
                        else if (behaviour is ItemStand stand) { if (stand.m_attachOther) localRoots.Add(stand.m_attachOther); }
                        else if (behaviour is CookingStation station && station.m_slots != null)
                            foreach (var slot in station.m_slots) if (slot) localRoots.Add(slot);
                    }
                    else if (component is Rigidbody) return "rigidbody";
                    else if (component is Animator || component is Animation) return "animation";
                }
                if (renderers == null && groups == null) return null;
                foreach (var component in components)
                {
                    if (component is null) continue;
                    if (component is MeshRenderer renderer) { if (!Local(renderer.transform, root.transform)) renderers?.Add(renderer); }
                    else if (component is LODGroup group && !Local(group.transform, root.transform)) groups?.Add(group);
                }
                return null;
            }
            finally { components.Clear(); localRoots.Clear(); }
        }

        // Whether an object sits on or under an object a local behaviour moves.
        private static bool Local(Transform transform, Transform root)
        {
            if (localRoots.Count == 0) return false;
            for (var current = transform; current; current = current.parent)
            {
                if (localRoots.Contains(current)) return true;
                if (current == root) break;
            }
            return false;
        }

        // Leaves the renderer's materials in the shared material list when it can merge: piece or Standard materials for building
        // pieces, and scenery or piece materials for natural objects, such as a swamp tree's piece-shaded bark, whose piece
        // values keep their defaults, since only the game's wear code sets them. Natural objects with Standard materials and
        // pieces with scenery materials stay native: merged, they took more draw calls and render-thread time than natively.
        private static string RendererExclusion(MeshRenderer renderer, bool managed, bool building, out Mesh mesh)
        {
            mesh = null;
            if (!renderer.TryGetComponent<MeshFilter>(out var filter) || !(mesh = filter.sharedMesh)) return "no mesh";
            renderer.GetSharedMaterials(materialList);
            if (materialList.Count != mesh.subMeshCount) return "material count";
            foreach (var material in materialList)
            {
                string source = SupportedShader(material);
                if (source == null || (building ? source != pieceShader && source != standardShader : source == standardShader)) return "material";
            }
            if (renderer.forceRenderingOff || renderer.isPartOfStaticBatch || renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534 || renderer.probeAnchor ||
                renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return "renderer settings";
            if (renderer.localToWorldMatrix.determinant <= 0f) return "mirrored";
            // Property blocks the material manager did not set are invisible to the merged slots.
            if (renderer.HasPropertyBlock() && !managed) return "property block";
            return null;
        }

        // Opaque materials of the game shaders with derived copies, instanced except for piece materials. The ripe barley's
        // grass material is not instanced, and the shader's instanced variant, which the copies derive from, bends its stalks
        // differently; the piece shader's two variants draw alike, so bird nests and beehives merge too.
        private static string SupportedShader(Material material)
        {
            if (!material) return null;
            if (!supportedMaterials.TryGetValue(material, out string source))
            {
                string name = material.shader ? material.shader.name : null;
                supportedMaterials[material] = source = name != null && derived.ContainsKey(name) && (material.enableInstancing || name == pieceShader || MergeCrops) && material.renderQueue <= 2500 ? name : null;
            }
            return source;
        }

        private static float MaximumScale(Vector3 scale) => Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
    }
}
