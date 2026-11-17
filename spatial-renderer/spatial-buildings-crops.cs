using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WorldPerformance
{
    // Crops' merged meshes, which paired measurements can return to native drawing, and the sun's shadows of small natural parts, which cast only near the camera.
    internal static partial class SpatialBuildings
    {
        // Natural objects players planted, such as PlantEverything's berry bushes, and scenery materials with instancing off,
        // such as the ripe barley's grass, merge too, into merged meshes of their own. The isolated client switches MergeCrops
        // off, which restarts the renderer, and sets CropsNative for paired measurements, when those meshes give way to the
        // members' native renderers in place.
        internal static bool MergeCrops = true;
        internal static bool CropsNative { get; private set; }

        // Whether a merged mesh draws, and whether its twin casts the sun's shadows.
        private static bool Drawn(BatchKey key) => !Native && !(key.Crop && CropsNative);
        private static bool Casts(Batch batch) => Drawn(batch.Key);

        // Merged small natural parts cast the sun's shadows only near the camera, as small far shadows does; the isolated client
        // switches it off to compare their shadows to 150 m.
        internal static bool SmallShadowsNear = true;
        private static float nextShadowCheck;

        // Twice a second each slot of a mesh of small natural parts stops casting when the camera is more than 60 m from its
        // renderer and casts again within 55 m, as small far shadows switches native renderers.
        private static void CheckSmallShadows(Vector3 camera, bool now)
        {
            if (!now && Time.realtimeSinceStartup < nextShadowCheck) return;
            nextShadowCheck = Time.realtimeSinceStartup + .5f;
            foreach (var batch in twins)
                if (FarSlots(batch, camera)) UploadTwin(batch, 0, batch.Live.Length - 1);
        }

        // Whether any slot's distance band changed.
        private static bool FarSlots(Batch batch, Vector3 camera)
        {
            bool changed = false, switching = SmallShadowsNear && SmallFarShadows.Enabled;
            for (int slot = 0; slot < batch.Live.Length; slot++)
            {
                var (member, part, _) = batch.Live[slot];
                bool far = false;
                if (switching && member != null && member.Radius != null)
                {
                    float distance = Vector3.Distance(camera, member.RendererBounds[part.Renderer].center) - member.Radius[part.Renderer];
                    far = batch.SlotFar[slot] ? distance >= SmallFarShadows.NearDistance : distance > SmallFarShadows.FarDistance;
                }
                if (far == batch.SlotFar[slot]) continue;
                batch.SlotFar[slot] = far;
                changed = true;
            }
            return changed;
        }

        // Gives the twin the slots' matrices from first to last, except those of slots too far to cast.
        private static void UploadTwin(Batch batch, int first, int last)
        {
            if (batch.TwinTransforms == null || last < first) return;
            for (int slot = first; slot <= last; slot++)
            {
                bool far = slot < batch.SlotFar.Length && batch.SlotFar[slot];
                batch.TwinData[slot * 2] = far ? default : batch.TransformData[slot * 2];
                batch.TwinData[slot * 2 + 1] = far ? default : batch.TransformData[slot * 2 + 1];
            }
            batch.TwinTransforms.SetData(batch.TwinData, first * 2, first * 2, (last - first + 1) * 2);
        }

        internal static void SetSmallShadowsNear(bool near)
        {
            SmallShadowsNear = near;
            if (Running) try { CheckSmallShadows(lastCamera, true); } catch (Exception error) { Fail(error); }
        }

        internal static void SetCropsNative(bool native)
        {
            if (!Running || Native || native == CropsNative) return;
            CropsNative = native;
            try
            {
                var shown = new HashSet<Member>();
                foreach (var cell in cells.Values)
                    foreach (var batch in cell.Batches)
                    {
                        if (!batch.Key.Crop) continue;
                        if (batch.Renderer) batch.Renderer.enabled = Drawn(batch.Key);
                        if (batch.Twin) batch.Twin.enabled = Casts(batch);
                        foreach (var member in batch.Members)
                            if (member.Owned && !member.Away && shown.Add(member)) { if (native) ShowNative(member); else HideNative(member); }
                    }
            }
            catch (Exception error) { Fail(error); }
        }

        // The crops' merged meshes: meshes, slots, vertices, graphics bytes, members, planted members, shadow twins and vertex
        // bytes; then all meshes of small natural parts with twins and how many of their slots cast nothing.
        internal static long[] CropCounts()
        {
            var batches = cells.Values.SelectMany(cell => cell.Batches).Where(batch => batch.Mesh && batch.Key.Crop).ToList();
            var members = new HashSet<Member>(batches.SelectMany(batch => batch.Members));
            var small = cells.Values.SelectMany(cell => cell.Batches).Where(batch => batch.Mesh && batch.TwinTransforms != null).ToList();
            return new[] { batches.Count, batches.Sum(batch => (long)batch.Count), batches.Sum(batch => (long)batch.Mesh.vertexCount), batches.Sum(GraphicsBytes),
                members.Count, members.Count(member => member.Planted), batches.Count(batch => batch.Twin),
                batches.Sum(batch => { long bytes = 0; for (int stream = 0; stream < batch.Mesh.vertexBufferCount; stream++) bytes += (long)batch.Mesh.GetVertexBufferStride(stream) * batch.Mesh.vertexCount; return bytes; }),
                small.Count, small.Sum(batch => batch.Live.Where((entry, slot) => entry.member != null && batch.SlotFar[slot]).Count()) };
        }
    }
}
